namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using Optimus.Providers.Windows;
using Xunit;

public sealed class AgentWindowObserverTests
{
    private static readonly WindowCandidate BoundWindow =
        new(42, 1234, "claude", "Project X", "Chrome_WidgetWin_1");

    [Fact]
    public void Poll_ReadsOnlyTheExactBoundHwnd()
    {
        var source = new FakeTextSource(new VisibleTextNode("1", "Planning"));
        var observer = new AgentWindowObserver(BoundWindow, source, _ => true);

        IReadOnlyList<VisibleAgentUpdate> updates = observer.Poll();

        Assert.Equal(new IntPtr(42), source.LastHwnd);
        Assert.Single(updates);
        Assert.Equal("Planning", updates[0].Text);
        Assert.Equal(VisibleAgentActivity.Progress, updates[0].Activity);
    }

    [Fact]
    public void Poll_DeduplicatesRerenderAndEmitsOnlyStreamingSuffix()
    {
        var source = new FakeTextSource(new VisibleTextNode("assistant-1", "Tests"));
        var observer = new AgentWindowObserver(BoundWindow, source, _ => true);

        Assert.Equal("Tests", Assert.Single(observer.Poll()).Text);
        Assert.Empty(observer.Poll());

        source.Nodes = new[] { new VisibleTextNode("assistant-1", "Tests passed") };

        Assert.Equal("passed", Assert.Single(observer.Poll()).Text);
        Assert.Empty(observer.Poll());
    }

    [Fact]
    public void Poll_DeduplicatesWhenRerenderReplacesUiaNode()
    {
        var source = new FakeTextSource(new VisibleTextNode("old-node", "Editing files"));
        var observer = new AgentWindowObserver(BoundWindow, source, _ => true);

        Assert.Single(observer.Poll());
        source.Nodes = new[] { new VisibleTextNode("replacement-node", "Editing files") };
        Assert.Empty(observer.Poll());

        source.Nodes = new[] { new VisibleTextNode("replacement-node-2", "Editing files complete") };
        Assert.Equal("complete", Assert.Single(observer.Poll()).Text);
    }

    [Fact]
    public void Poll_ClassifiesVisibleToolAndFinalResponseSemantics()
    {
        var source = new FakeTextSource(
            new VisibleTextNode("tool-1", "dotnet test", AutomationId: "tool-call"),
            new VisibleTextNode("reply-1", "Implemented the fix.", AutomationId: "assistant-response-complete"));
        var observer = new AgentWindowObserver(BoundWindow, source, _ => true);

        IReadOnlyList<VisibleAgentUpdate> updates = observer.Poll();

        Assert.Equal(VisibleAgentActivity.ToolOrSkill, updates[0].Activity);
        Assert.Equal(VisibleAgentActivity.FinalResponseCandidate, updates[1].Activity);
    }

    [Fact]
    public void Poll_DoesNotReadWhenBoundWindowIsGone()
    {
        var source = new FakeTextSource(new VisibleTextNode("1", "Wrong window text"));
        var observer = new AgentWindowObserver(BoundWindow, source, _ => false);

        Assert.Empty(observer.Poll());
        Assert.Null(source.LastHwnd);
    }

    [Fact]
    public void Poll_IgnoresSidebarHeadersAndTimestamps()
    {
        var source = new FakeTextSource(
            new VisibleTextNode("sb-1", "Projects"),
            new VisibleTextNode("sb-2", "Conversation History"),
            new VisibleTextNode("sb-3", "Task T002 Scaffold Implementation now"),
            new VisibleTextNode("sb-4", "Commit and Push Changes 4d"),
            new VisibleTextNode("sb-5", "Settings"),
            new VisibleTextNode("chat-1", "I updated the code in Optimus_VoiceOS."));
        var observer = new AgentWindowObserver(BoundWindow, source, _ => true);

        IReadOnlyList<VisibleAgentUpdate> updates = observer.Poll();

        VisibleAgentUpdate single = Assert.Single(updates);
        Assert.Equal("I updated the code in Optimus_VoiceOS.", single.Text);
        Assert.Equal(VisibleAgentActivity.VisibleText, single.Activity);
    }

    [Fact]
    public void Poll_IgnoresIdeChromeBannersSymbolsAndClocks()
    {
        var source = new FakeTextSource(
            new VisibleTextNode("c-1", "Restart to Update"),
            new VisibleTextNode("c-2", "Open IDE"),
            new VisibleTextNode("c-3", "Load older messages"),
            new VisibleTextNode("c-4", "->"),
            new VisibleTextNode("c-5", "•"),
            new VisibleTextNode("c-6", "/"),
            new VisibleTextNode("c-7", "..."),
            new VisibleTextNode("c-8", "10:08 AM"),
            new VisibleTextNode("c-9", "2mo"),
            new VisibleTextNode("c-10", "See all (11)"),
            new VisibleTextNode("c-11", "I have finished the implementation."));
        var observer = new AgentWindowObserver(BoundWindow, source, _ => true);

        IReadOnlyList<VisibleAgentUpdate> updates = observer.Poll();

        VisibleAgentUpdate single = Assert.Single(updates);
        Assert.Equal("I have finished the implementation.", single.Text);
    }

    [Fact]
    public void Poll_RemembersSeenTextAcrossVisibilityChanges()
    {
        var source = new FakeTextSource(
            new VisibleTextNode("msg-1", "Initial historical message 1"),
            new VisibleTextNode("msg-2", "Initial historical message 2"));
        var observer = new AgentWindowObserver(BoundWindow, source, _ => true);

        // Baseline poll captures both messages
        IReadOnlyList<VisibleAgentUpdate> baseline = observer.Poll();
        Assert.Equal(2, baseline.Count);
        Assert.Equal(2, observer.CapturedNodeCount);

        // Subsequent poll with same messages produces no updates
        Assert.Empty(observer.Poll());

        // msg-1 scrolls offscreen
        source.Nodes = new[] { new VisibleTextNode("msg-2", "Initial historical message 2") };
        Assert.Empty(observer.Poll());

        // msg-1 scrolls back on screen along with a new message msg-3
        source.Nodes = new[]
        {
            new VisibleTextNode("msg-1", "Initial historical message 1"),
            new VisibleTextNode("msg-2", "Initial historical message 2"),
            new VisibleTextNode("msg-3", "Brand new message 3")
        };
        IReadOnlyList<VisibleAgentUpdate> newUpdates = observer.Poll();
        VisibleAgentUpdate single = Assert.Single(newUpdates);
        Assert.Equal("Brand new message 3", single.Text);
    }

    private sealed class FakeTextSource(params VisibleTextNode[] nodes) : IWindowTextSource
    {
        public IReadOnlyList<VisibleTextNode> Nodes { get; set; } = nodes;

        public IntPtr? LastHwnd { get; private set; }

        public IReadOnlyList<VisibleTextNode> Read(IntPtr hwnd)
        {
            LastHwnd = hwnd;
            return Nodes;
        }
    }
}
