namespace Optimus.Inference.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Inference;
using Xunit;

public sealed class GemmaDigestSummarizerTests
{
    private static readonly string[] SampleOptionsA = new[] { "Confirm & Send", "Decline" };
    private static readonly string[] SampleOptionsB = new[] { "Send", "Cancel" };
    private static readonly string[] SampleOptionsC = new[] { "Approve", "Reject" };

    [Fact]
    public void FormatFacts_FormatsFactsAndBoundsCount()
    {
        var facts = new List<DigestFact>();
        for (int i = 0; i < 25; i++)
        {
            facts.Add(new DigestFact(
                EventId: $"evt-{i}",
                ProjectId: "proj-1",
                SessionOrTaskId: "task-1",
                EventType: "progress",
                Content: $"Step {i} completed",
                Timestamp: DateTimeOffset.UtcNow.AddSeconds(i)
            ));
        }

        string formatted = GemmaDigestSummarizer.FormatFacts("My Project", "task-1", facts);

        Assert.Contains("Project: My Project", formatted);
        Assert.Contains("Task: task-1", formatted);
        // Should only take the last 15 bounded facts
        Assert.DoesNotContain("Step 0 completed", formatted);
        Assert.DoesNotContain("Step 9 completed", formatted);
        Assert.Contains("Step 24 completed", formatted);
    }

    [Fact]
    public void FormatFacts_IncludesDecisionAndOptions()
    {
        var facts = new List<DigestFact>
        {
            new(
                EventId: "evt-dec",
                ProjectId: "proj-1",
                SessionOrTaskId: "task-1",
                EventType: "approval",
                Content: "Confirm sending to Antigravity?",
                Timestamp: DateTimeOffset.UtcNow,
                IsDecision: true,
                Options: SampleOptionsA
            )
        };

        string formatted = GemmaDigestSummarizer.FormatFacts("Optimus", "task-1", facts);

        Assert.Contains("- [APPROVAL] Confirm sending to Antigravity?", formatted);
        Assert.Contains("Options: [Confirm & Send, Decline]", formatted);
    }

    [Fact]
    public void BuildMessages_PutsFewShotBeforeUserPrompt()
    {
        var messages = GemmaDigestSummarizer.BuildMessages("Project: Test");

        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[^1].Role);
        Assert.Equal("Project: Test", messages[^1].Content);
        Assert.True(messages.Count >= 9);
    }

    [Fact]
    public void SanitizeAndParse_ValidJson_ReturnsExpectedDigest()
    {
        var facts = new List<DigestFact>
        {
            new(
                EventId: "evt-1",
                ProjectId: "proj-1",
                SessionOrTaskId: "sess-1",
                EventType: "test",
                Content: "35 unit tests passed",
                Timestamp: DateTimeOffset.UtcNow,
                IsMeaningful: true
            ),
            new(
                EventId: "evt-2",
                ProjectId: "proj-1",
                SessionOrTaskId: "sess-1",
                EventType: "build",
                Content: "Build succeeded",
                Timestamp: DateTimeOffset.UtcNow,
                IsMeaningful: true
            )
        };

        string modelOutput = """
        {
            "headline": "Audio tests passed",
            "spokenSummary": "All 35 audio interop tests passed cleanly.",
            "requiresDecision": false,
            "decisionOptions": [],
            "detailedSummary": "Build and unit test pass verified.",
            "isMeaningful": true
        }
        """;

        DigestEvent result = GemmaDigestSummarizer.SanitizeAndParse(modelOutput, "proj-1", "Optimus", "sess-1", facts);

        Assert.NotNull(result);
        Assert.Equal("Audio tests passed", result.Headline);
        Assert.Equal("All 35 audio interop tests passed cleanly.", result.SpokenSummary);
        Assert.False(result.RequiresDecision);
        Assert.True(result.IsMeaningful);
        Assert.Equal(2, result.SourceEventIds.Count);
        Assert.Contains("evt-1", result.SourceEventIds);
        Assert.Contains("evt-2", result.SourceEventIds);
        Assert.Equal("proj-1", result.ProjectId);
        Assert.Equal("Optimus", result.ProjectName);
        Assert.Equal("sess-1", result.TaskRef);
        Assert.NotNull(result.SourceEvents);
        Assert.Equal(2, result.SourceEvents.Count);
    }

    [Fact]
    public void SanitizeAndParse_DecisionRequired_SetsDecisionFlagAndOptions()
    {
        var facts = new List<DigestFact>
        {
            new(
                EventId: "evt-decision",
                ProjectId: "proj-1",
                SessionOrTaskId: "sess-1",
                EventType: "approval",
                Content: "Send draft to Claude?",
                Timestamp: DateTimeOffset.UtcNow,
                IsDecision: true,
                Options: SampleOptionsB
            )
        };

        string modelOutput = """
        {
            "headline": "Send confirmation required",
            "spokenSummary": "Waiting for confirmation to send draft to Claude.",
            "requiresDecision": true,
            "decisionOptions": ["Send", "Cancel"],
            "detailedSummary": "Draft is ready.",
            "isMeaningful": true
        }
        """;

        DigestEvent result = GemmaDigestSummarizer.SanitizeAndParse(modelOutput, "proj-1", "Optimus", "sess-1", facts);

        Assert.True(result.RequiresDecision);
        Assert.NotNull(result.DecisionOptions);
        Assert.Equal(2, result.DecisionOptions.Count);
        Assert.Contains("Send", result.DecisionOptions);
        Assert.Contains("Cancel", result.DecisionOptions);
    }

    [Fact]
    public void SanitizeAndParse_WithThinkingScaffoldingAndFences_StripsAndParses()
    {
        var facts = new List<DigestFact>
        {
            new("evt-1", "p1", "s1", "status", "working", DateTimeOffset.UtcNow)
        };

        string modelOutput = """
        <think>The user wants a summary of the working session.</think>
        ```json
        {
            "headline": "Session in progress",
            "spokenSummary": "Session is active.",
            "requiresDecision": false,
            "decisionOptions": [],
            "detailedSummary": "Active working status.",
            "isMeaningful": false
        }
        ```
        <end_of_turn>
        """;

        DigestEvent result = GemmaDigestSummarizer.SanitizeAndParse(modelOutput, "p1", "Project", "s1", facts);

        Assert.Equal("Session in progress", result.Headline);
        Assert.Equal("Session is active.", result.SpokenSummary);
        Assert.False(result.RequiresDecision);
    }

    [Fact]
    public void SanitizeAndParse_InvalidJson_RetainsSourceEventsViaFallback()
    {
        var facts = new List<DigestFact>
        {
            new("evt-1", "p1", "s1", "response", "First message", DateTimeOffset.UtcNow, IsMeaningful: true),
            new("evt-2", "p1", "s1", "response", "Second message", DateTimeOffset.UtcNow, IsMeaningful: true)
        };

        string invalidOutput = "I am an AI and here is my text that is not json at all.";

        DigestEvent result = GemmaDigestSummarizer.SanitizeAndParse(invalidOutput, "p1", "MyProject", "s1", facts);

        Assert.NotNull(result);
        // Source events retained!
        Assert.NotNull(result.SourceEvents);
        Assert.Equal(2, result.SourceEvents.Count);
        Assert.Equal(2, result.SourceEventIds.Count);
        Assert.Contains("evt-1", result.SourceEventIds);
        Assert.Contains("evt-2", result.SourceEventIds);
        Assert.Equal("p1", result.ProjectId);
        Assert.Equal("MyProject", result.ProjectName);
        Assert.Contains("Second message", result.SpokenSummary);
        Assert.NotNull(result.OriginalResponse);
        Assert.Contains("First message", result.OriginalResponse);
    }

    [Fact]
    public void DigestEvent_CreateFallback_PreservesAllFactsAndOriginalText()
    {
        var facts = new List<DigestFact>
        {
            new(
                EventId: "evt-approve",
                ProjectId: "p1",
                SessionOrTaskId: "t1",
                EventType: "approval",
                Content: "Approve PR #10?",
                Timestamp: DateTimeOffset.UtcNow,
                IsDecision: true,
                Options: SampleOptionsC
            )
        };

        var fallback = DigestEvent.CreateFallback("p1", "Optimus", "t1", facts, "Model timeout");

        Assert.True(fallback.RequiresDecision);
        Assert.Single(fallback.SourceEventIds);
        Assert.Equal("evt-approve", fallback.SourceEventIds[0]);
        Assert.NotNull(fallback.SourceEvents);
        Assert.Single(fallback.SourceEvents);
        Assert.Equal(2, fallback.DecisionOptions?.Count);
        Assert.Contains("Approve PR #10?", fallback.OriginalResponse!);
        Assert.Contains("Model timeout", fallback.DetailedSummary!);
    }

    [Fact]
    public async Task SummarizeAsync_EmptyFacts_ReturnsNoActivityDigest()
    {
        using var summarizer = new GemmaDigestSummarizer(new LlamaServerProcess("dummy.exe", "dummy.gguf"));
        var result = await summarizer.SummarizeAsync("p1", "Optimus", "t1", Array.Empty<DigestFact>());

        Assert.Equal("No activity", result.Headline);
        Assert.False(result.RequiresDecision);
        Assert.Empty(result.SourceEventIds);
    }

    [Fact]
    public async Task SummarizeAsync_WhenServerThrows_ReturnsFallbackRetainingSourceEvents()
    {
        // Dummy non-existent server will fail to ensure loaded or start, causing an exception
        using var summarizer = new GemmaDigestSummarizer(new LlamaServerProcess("non_existent_server.exe", "non_existent_model.gguf"));
        var facts = new List<DigestFact>
        {
            new("evt-1", "p1", "t1", "progress", "Step 1 complete", DateTimeOffset.UtcNow)
        };

        var result = await summarizer.SummarizeAsync("p1", "Optimus", "t1", facts);

        Assert.NotNull(result);
        Assert.NotNull(result.SourceEvents);
        Assert.Single(result.SourceEvents);
        Assert.Equal("evt-1", result.SourceEventIds[0]);
    }
}
