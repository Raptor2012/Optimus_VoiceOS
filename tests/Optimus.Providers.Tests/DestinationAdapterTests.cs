namespace Optimus.Providers.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers;
using Optimus.Providers.Windows;
using Xunit;

/// <summary>
/// The destination rules that must hold: never guess a window, never report Sent unless the
/// adapter actually succeeded, and never send text other than what was confirmed.
/// </summary>
public class DestinationAdapterTests
{
    private static readonly string[] ExpectedDestinationIds = { "claude", "antigravity", "codex" };
    private static readonly string[] ExpectedProcessNames = { "claude", "Antigravity", "ChatGPT" };

    private static WindowCandidate Candidate(long handle, string process = "claude", string title = "Claude") =>
        new(handle, 1234, process, title, "Chrome_WidgetWin_1");

    [Fact]
    public void ConfirmedDraft_RejectsEmptyText()
    {
        Assert.Throws<ArgumentException>(() => new ConfirmedDraft("   ", "claude"));
        Assert.Throws<ArgumentException>(() => new ConfirmedDraft("hi", " "));
    }

    /// <summary>The snapshot is immutable, so a later edit cannot change what was sent.</summary>
    [Fact]
    public void ConfirmedDraft_IsImmutableSnapshot()
    {
        var draft = new ConfirmedDraft("send exactly this", "claude");

        Assert.Equal("send exactly this", draft.Text);
        Assert.Equal("claude", draft.DestinationId);

        // Text has no setter; the only way to change it is to construct a new draft.
        Assert.Null(typeof(ConfirmedDraft).GetProperty(nameof(ConfirmedDraft.Text))!.SetMethod);
    }

    /// <summary>A draft confirmed for one destination must never be sent to another.</summary>
    [Fact]
    public async Task Send_RefusesDraftConfirmedForAnotherDestination()
    {
        var adapter = new FakeAdapter("claude", "Claude", "claude");
        adapter.SetCandidates(Candidate(1));
        adapter.Bind(Candidate(1));

        SendResult result = await adapter.SendAsync(new ConfirmedDraft("hello", "codex"));

        Assert.False(result.Succeeded);
        Assert.Equal(SendStatus.NotReady, result.Status);
        Assert.Empty(adapter.Typed);
    }

    [Fact]
    public async Task Send_FailsWhenNothingIsBound()
    {
        var adapter = new FakeAdapter("claude", "Claude", "claude");
        adapter.SetCandidates(Candidate(1));

        SendResult result = await adapter.SendAsync(new ConfirmedDraft("hello", "claude"));

        Assert.False(result.Succeeded);
        Assert.Empty(adapter.Typed);
    }

    /// <summary>Several candidates must be reported as ambiguous, never auto-picked.</summary>
    [Fact]
    public void Probe_ReportsAmbiguityAndBindsNothing()
    {
        var adapter = new FakeAdapter("codex", "Codex (ChatGPT app)", "ChatGPT");
        adapter.SetCandidates(
            Candidate(1, "ChatGPT", "ChatGPT"),
            Candidate(2, "ChatGPT", "ChatGPT"));

        DestinationStatus status = adapter.Probe();

        Assert.Equal(DestinationReadiness.AmbiguousWindow, status.Readiness);
        Assert.False(status.CanSend);
        Assert.Null(status.Bound);
        Assert.Equal(2, status.Candidates.Count);
    }

    /// <summary>Even a single candidate must be bound explicitly before a send is allowed.</summary>
    [Fact]
    public void Probe_SingleCandidateIsNotAutoBound()
    {
        var adapter = new FakeAdapter("claude", "Claude", "claude");
        adapter.SetCandidates(Candidate(1));

        DestinationStatus status = adapter.Probe();

        Assert.Equal(DestinationReadiness.NotBound, status.Readiness);
        Assert.False(status.CanSend);
        Assert.Null(adapter.BoundWindow);
    }

    [Fact]
    public void Probe_ReportsNotRunningWhenNoWindows()
    {
        var adapter = new FakeAdapter("antigravity", "Antigravity", "Antigravity");

        DestinationStatus status = adapter.Probe();

        Assert.Equal(DestinationReadiness.NotRunning, status.Readiness);
        Assert.False(status.CanSend);
    }

    /// <summary>A bound window that closes must fail, not fall back to another window.</summary>
    [Fact]
    public async Task Send_FailsWhenBoundWindowDisappears()
    {
        var adapter = new FakeAdapter("claude", "Claude", "claude");
        adapter.SetCandidates(Candidate(1));
        adapter.Bind(Candidate(1));

        // The bound window closes; a different one opens.
        adapter.SetCandidates(Candidate(99));

        SendResult result = await adapter.SendAsync(new ConfirmedDraft("hello", "claude"));

        Assert.False(result.Succeeded);
        Assert.Equal(SendStatus.NotReady, result.Status);
        Assert.Empty(adapter.Typed);
    }

    /// <summary>Focus failure must not type anything, and must not report Sent.</summary>
    [Fact]
    public async Task Send_FocusFailureTypesNothingAndDoesNotReportSent()
    {
        var adapter = new FakeAdapter("claude", "Claude", "claude") { FocusSucceeds = false };
        adapter.SetCandidates(Candidate(1));
        adapter.Bind(Candidate(1));

        SendResult result = await adapter.SendAsync(new ConfirmedDraft("hello", "claude"));

        Assert.Equal(SendStatus.FocusFailed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Empty(adapter.Typed);
    }

    /// <summary>Rejected keystrokes must not be reported as a successful send.</summary>
    [Fact]
    public async Task Send_InputRejectionDoesNotReportSent()
    {
        var adapter = new FakeAdapter("claude", "Claude", "claude") { TypingSucceeds = false };
        adapter.SetCandidates(Candidate(1));
        adapter.Bind(Candidate(1));

        SendResult result = await adapter.SendAsync(new ConfirmedDraft("hello", "claude"));

        Assert.Equal(SendStatus.InputRejected, result.Status);
        Assert.False(result.Succeeded);
    }

    /// <summary>The happy path sends exactly the confirmed text, unchanged.</summary>
    [Fact]
    public async Task Send_DeliversExactlyTheConfirmedText()
    {
        var adapter = new FakeAdapter("claude", "Claude", "claude");
        adapter.SetCandidates(Candidate(1));
        adapter.Bind(Candidate(1));

        const string text = "Add a retry to fetchUser in api.ts.";
        SendResult result = await adapter.SendAsync(new ConfirmedDraft(text, "claude"));

        Assert.True(result.Succeeded);
        Assert.Equal(SendStatus.Sent, result.Status);
        Assert.Equal(new[] { text }, adapter.Typed);
        Assert.True(adapter.Submitted);
    }

    /// <summary>Binding a window from a different process must be refused outright.</summary>
    [Fact]
    public void Bind_RejectsWindowFromAnotherProcess()
    {
        var adapter = new FakeAdapter("claude", "Claude", "claude");
        adapter.SetCandidates(Candidate(1, "ChatGPT", "ChatGPT"));

        Assert.Throws<ArgumentException>(() => adapter.Bind(Candidate(1, "ChatGPT", "ChatGPT")));
    }

    [Fact]
    public void Registry_ExposesExactlyTheThreeConfiguredDestinations()
    {
        var registry = new DestinationRegistry();

        Assert.Equal(3, registry.Adapters.Count);
        Assert.Equal(ExpectedDestinationIds, registry.Adapters.Select(a => a.DestinationId).ToArray());
        Assert.Equal(ExpectedProcessNames, registry.Adapters.Select(a => a.ProcessName).ToArray());
    }

    /// <summary>
    /// Mirrors <see cref="WindowsAppAdapter"/>'s decision logic with the Win32 calls replaced,
    /// so the rules can be tested without real windows or stealing focus from the test run.
    /// </summary>
    private sealed class FakeAdapter : IDestinationAdapter
    {
        private readonly List<WindowCandidate> _candidates = new();
        private WindowCandidate? _bound;

        public FakeAdapter(string id, string displayName, string processName)
        {
            DestinationId = id;
            DisplayName = displayName;
            ProcessName = processName;
        }

        public string DestinationId { get; }

        public string DisplayName { get; }

        public string ProcessName { get; }

        public WindowCandidate? BoundWindow => _bound;

        public bool FocusSucceeds { get; set; } = true;

        public bool TypingSucceeds { get; set; } = true;

        public List<string> Typed { get; } = new();

        public bool Submitted { get; private set; }

        public void SetCandidates(params WindowCandidate[] candidates)
        {
            _candidates.Clear();
            _candidates.AddRange(candidates);
        }

        public DestinationStatus Probe()
        {
            if (_bound != null)
            {
                bool stillThere = _candidates.Any(c => c.Handle == _bound.Handle);
                return stillThere
                    ? new DestinationStatus(DestinationReadiness.Ready, _candidates, _bound, "bound")
                    : new DestinationStatus(DestinationReadiness.BoundWindowGone, _candidates, null, "window gone");
            }

            return _candidates.Count switch
            {
                0 => new DestinationStatus(DestinationReadiness.NotRunning, _candidates, null, "not running"),
                1 => new DestinationStatus(DestinationReadiness.NotBound, _candidates, null, "not bound"),
                _ => new DestinationStatus(DestinationReadiness.AmbiguousWindow, _candidates, null, "ambiguous")
            };
        }

        public void Bind(WindowCandidate candidate)
        {
            if (!string.Equals(candidate.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("wrong process", nameof(candidate));
            }

            _bound = candidate;
        }

        public void Unbind() => _bound = null;

        public Task<SendResult> SendAsync(ConfirmedDraft draft, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(draft.DestinationId, DestinationId, StringComparison.Ordinal))
            {
                return Task.FromResult(new SendResult(SendStatus.NotReady, "wrong destination", 0));
            }

            DestinationStatus status = Probe();
            if (!status.CanSend)
            {
                return Task.FromResult(new SendResult(SendStatus.NotReady, status.Detail, 0));
            }

            if (!FocusSucceeds)
            {
                return Task.FromResult(new SendResult(SendStatus.FocusFailed, "focus failed", 0));
            }

            if (!TypingSucceeds)
            {
                return Task.FromResult(new SendResult(SendStatus.InputRejected, "input rejected", 0));
            }

            Typed.Add(draft.Text);
            Submitted = true;
            return Task.FromResult(new SendResult(SendStatus.Sent, "delivered", 1));
        }
    }
}
