using System;
using System.Collections.Generic;
using Optimus.Core.Voice;
using Optimus.Providers.Windows;
using Xunit;

namespace Optimus.Core.Tests;

public sealed class WindowSelectionCommandClassifierTests
{
    private static readonly WindowCandidate Window1 = new(1001, 100, "Code", "Optimus_VoiceOS - Visual Studio Code", "Chrome_WidgetWin_1");
    private static readonly WindowCandidate Window2 = new(1002, 200, "Code", "Antigravity_XR - Visual Studio Code", "Chrome_WidgetWin_1");
    private static readonly WindowCandidate Window3 = new(1003, 300, "Code", "Terminal - PowerShell", "ConsoleWindowClass");

    [Theory]
    [InlineData("cancel")]
    [InlineData("no")]
    [InlineData("stop")]
    [InlineData("abort")]
    [InlineData("scratch that")]
    [InlineData("never mind")]
    public void Classify_AcceptsCancelCommands(string transcript)
    {
        var result = WindowSelectionCommandClassifier.Classify(transcript, new[] { Window1, Window2 });
        Assert.Equal(WindowSelectionCommandType.Cancel, result.Command);
        Assert.Null(result.SelectedCandidate);
    }

    [Theory]
    [InlineData("refresh windows")]
    [InlineData("refresh")]
    [InlineData("retry")]
    [InlineData("reload")]
    [InlineData("check again")]
    public void Classify_AcceptsRefreshCommands(string transcript)
    {
        var result = WindowSelectionCommandClassifier.Classify(transcript, new[] { Window1, Window2 });
        Assert.Equal(WindowSelectionCommandType.Refresh, result.Command);
        Assert.Null(result.SelectedCandidate);
    }

    [Theory]
    [InlineData("repeat options")]
    [InlineData("repeat")]
    [InlineData("say again")]
    [InlineData("options")]
    public void Classify_AcceptsRepeatCommands(string transcript)
    {
        var result = WindowSelectionCommandClassifier.Classify(transcript, new[] { Window1, Window2 });
        Assert.Equal(WindowSelectionCommandType.Repeat, result.Command);
        Assert.Null(result.SelectedCandidate);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("yeah")]
    [InlineData("yep")]
    [InlineData("confirm")]
    [InlineData("use that window")]
    [InlineData("use this window")]
    [InlineData("use window")]
    [InlineData("that one")]
    [InlineData("this one")]
    [InlineData("window one")]
    [InlineData("1")]
    public void Classify_SingleCandidate_AcceptsAffirmativeAndBinding(string transcript)
    {
        var result = WindowSelectionCommandClassifier.Classify(transcript, new[] { Window1 });
        Assert.Equal(WindowSelectionCommandType.Selected, result.Command);
        Assert.Same(Window1, result.SelectedCandidate);
    }

    [Theory]
    [InlineData("window one", 0)]
    [InlineData("one", 0)]
    [InlineData("first", 0)]
    [InlineData("1", 0)]
    [InlineData("window two", 1)]
    [InlineData("two", 1)]
    [InlineData("second", 1)]
    [InlineData("2", 1)]
    [InlineData("window three", 2)]
    [InlineData("three", 2)]
    [InlineData("third", 2)]
    [InlineData("3", 2)]
    [InlineData("use window 2", 1)]
    [InlineData("select window two", 1)]
    [InlineData("number two", 1)]
    public void Classify_MultipleCandidates_AcceptsNumberSelection(string transcript, int expectedIndex)
    {
        var candidates = new[] { Window1, Window2, Window3 };
        var result = WindowSelectionCommandClassifier.Classify(transcript, candidates);

        Assert.Equal(WindowSelectionCommandType.Selected, result.Command);
        Assert.Same(candidates[expectedIndex], result.SelectedCandidate);
    }

    [Fact]
    public void Classify_MultipleCandidates_UniqueTitleSubstringMatches()
    {
        var candidates = new[] { Window1, Window2, Window3 };

        var result1 = WindowSelectionCommandClassifier.Classify("Optimus VoiceOS", candidates);
        Assert.Equal(WindowSelectionCommandType.Selected, result1.Command);
        Assert.Same(Window1, result1.SelectedCandidate);

        var result2 = WindowSelectionCommandClassifier.Classify("Antigravity", candidates);
        Assert.Equal(WindowSelectionCommandType.Selected, result2.Command);
        Assert.Same(Window2, result2.SelectedCandidate);

        var result3 = WindowSelectionCommandClassifier.Classify("PowerShell", candidates);
        Assert.Equal(WindowSelectionCommandType.Selected, result3.Command);
        Assert.Same(Window3, result3.SelectedCandidate);
    }

    [Fact]
    public void Classify_MultipleCandidates_AmbiguousTitleReturnsUnknown()
    {
        var candidates = new[] { Window1, Window2, Window3 };
        // "Visual Studio Code" matches both Window1 and Window2
        var result = WindowSelectionCommandClassifier.Classify("Visual Studio Code", candidates);
        Assert.Equal(WindowSelectionCommandType.Unknown, result.Command);
        Assert.Null(result.SelectedCandidate);
    }

    [Fact]
    public void Classify_ZeroCandidates_AcceptsCancelAndRefreshOnly()
    {
        var candidates = Array.Empty<WindowCandidate>();

        var cancel = WindowSelectionCommandClassifier.Classify("cancel", candidates);
        Assert.Equal(WindowSelectionCommandType.Cancel, cancel.Command);

        var refresh = WindowSelectionCommandClassifier.Classify("refresh windows", candidates);
        Assert.Equal(WindowSelectionCommandType.Refresh, refresh.Command);

        var unknown = WindowSelectionCommandClassifier.Classify("yes", candidates);
        Assert.Equal(WindowSelectionCommandType.Unknown, unknown.Command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("window five")]
    public void Classify_RejectsInvalidInput(string? transcript)
    {
        var result = WindowSelectionCommandClassifier.Classify(transcript, new[] { Window1, Window2 });
        Assert.Equal(WindowSelectionCommandType.Unknown, result.Command);
        Assert.Null(result.SelectedCandidate);
    }
}
