namespace Optimus.Core.Tests.Memory;

using System;
using System.Collections.Generic;
using Optimus.Core.Conversation;
using Optimus.Core.Memory;
using Xunit;

public sealed class ContextMemoryTests
{
    [Fact]
    public void SlidingWindow_RetainsUpToMaxTurns_DropsOldest()
    {
        var context = new ContextMemory(maxTurns: 3);

        context.AddTurn("1", "first");
        context.AddTurn("2", "second");
        context.AddTurn("3", "third");
        Assert.Equal(3, context.Count);

        context.AddTurn("4", "fourth");
        Assert.Equal(3, context.Count);

        IReadOnlyList<ConversationTurnRecord> turns = context.Turns;
        Assert.Equal("2", turns[0].TurnId);
        Assert.Equal("3", turns[1].TurnId);
        Assert.Equal("4", turns[2].TurnId);
    }

    [Fact]
    public void SlidingWindow_DefaultCapacityIs20()
    {
        var context = new ContextMemory();
        Assert.Equal(20, context.MaxTurns);

        for (int i = 1; i <= 25; i++)
        {
            context.AddTurn($"turn-{i}", $"query {i}");
        }

        Assert.Equal(20, context.Count);
        Assert.Equal("turn-6", context.Turns[0].TurnId);
        Assert.Equal("turn-25", context.Turns[^1].TurnId);
    }

    [Fact]
    public void AddTurn_WithTurnContext_ExtractsFieldsCorrectly()
    {
        var context = new ContextMemory();
        var turnContext = new TurnContext("pixel", "inspect audio tests", "audio testing");

        context.AddTurn(turnContext, response: "35 tests passed");

        Assert.Single(context.Turns);
        ConversationTurnRecord record = context.Turns[0];
        Assert.Equal(turnContext.TurnId.ToString(), record.TurnId);
        Assert.Equal("pixel", record.Device);
        Assert.Equal("inspect audio tests", record.Transcript);
        Assert.Equal("audio testing", record.Objective);
        Assert.Equal("35 tests passed", record.Response);
    }

    [Fact]
    public void GetRelevantContext_MatchesKeywordOverlap()
    {
        var context = new ContextMemory();

        context.AddTurn("1", "compile WasapiInterop audio driver", objective: "audio interop", response: "built successfully");
        context.AddTurn("2", "run tests for desktop navigation", objective: "navigation tests", response: "12 tests passed");
        context.AddTurn("3", "check audio pipeline latency and sample rate", objective: "latency check", response: "15ms latency");

        IReadOnlyList<ConversationTurnRecord> audioMatches = context.GetRelevantContext("audio latency");

        Assert.Equal(2, audioMatches.Count);
        // Turn 3 has 2 matches (audio, latency), Turn 1 has 1 match (audio)
        Assert.Equal("3", audioMatches[0].TurnId);
        Assert.Equal("1", audioMatches[1].TurnId);
    }

    [Fact]
    public void GetRelevantContext_IgnoresCommonStopWords()
    {
        var context = new ContextMemory();

        context.AddTurn("1", "refactor the project", objective: "cleanup", response: "done");
        context.AddTurn("2", "build android app", objective: "compile", response: "ok");

        // "the", "and", "is", "of" are stop words; only "refactor" is meaningful
        IReadOnlyList<ConversationTurnRecord> matches = context.GetRelevantContext("is the refactor done and ready");

        Assert.Single(matches);
        Assert.Equal("1", matches[0].TurnId);
    }

    [Fact]
    public void GetRelevantContext_EmptyOrWhitespaceObjective_ReturnsEmpty()
    {
        var context = new ContextMemory();
        context.AddTurn("1", "hello world");

        Assert.Empty(context.GetRelevantContext(""));
        Assert.Empty(context.GetRelevantContext("   "));
    }

    [Fact]
    public void FormatRelevantContext_FormatsReadableText()
    {
        var context = new ContextMemory();
        context.AddTurn("turn-100", "run unit tests", objective: "test run", response: "all passed");

        string formatted = context.FormatRelevantContext("run tests");

        Assert.Contains("Turn [turn-100]: run unit tests", formatted);
        Assert.Contains("Objective: test run", formatted);
        Assert.Contains("Response: all passed", formatted);
    }

    [Fact]
    public void UpdateResponse_UpdatesMatchingTurn()
    {
        var context = new ContextMemory();
        context.AddTurn("t1", "what time is it?", response: null);

        bool updated = context.UpdateResponse("t1", "It is 12:00 PM.");
        Assert.True(updated);

        Assert.Equal("It is 12:00 PM.", context.Turns[0].Response);
    }
}
