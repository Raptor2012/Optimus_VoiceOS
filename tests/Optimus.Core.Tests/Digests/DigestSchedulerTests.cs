namespace Optimus.Core.Tests.Digests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Digests;
using Optimus.Core.Voice;
using Optimus.Inference;
using Optimus.Providers.Ao;
using Xunit;

public sealed class DigestSchedulerTests
{
    private sealed class MockDigestSummarizer : IDigestSummarizer
    {
        public bool IsLoaded => true;
        public Func<string?, string?, string?, IReadOnlyList<DigestFact>, DigestEvent>? SummarizeFunc { get; set; }
        public int SummarizeCount { get; private set; }

        public void EnsureLoaded() { }

        public Task<DigestEvent> SummarizeAsync(
            string? projectId,
            string? projectName,
            string? taskRef,
            IReadOnlyList<DigestFact> facts,
            CancellationToken cancellationToken = default)
        {
            SummarizeCount++;
            if (SummarizeFunc != null)
            {
                return Task.FromResult(SummarizeFunc(projectId, projectName, taskRef, facts));
            }

            bool requiresDecision = facts.Any(f => f.IsDecision);
            var ids = facts.Select(f => f.EventId).ToList();

            return Task.FromResult(new DigestEvent(
                Id: $"mock-dig-{SummarizeCount}",
                ProjectId: projectId,
                ProjectName: projectName,
                TaskRef: taskRef,
                Headline: requiresDecision ? "Decision required" : "Routine update",
                SpokenSummary: requiresDecision ? "Please make a decision." : "Updates completed.",
                RequiresDecision: requiresDecision,
                SourceEventIds: ids,
                CreatedAt: DateTimeOffset.UtcNow,
                OriginalResponse: "Original assistant output text.",
                SourceEvents: facts,
                IsMeaningful: requiresDecision || facts.Any(f => f.IsMeaningful)
            ));
        }

        public Task PrimeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public void IsToolChatter_FiltersOutLowLevelCommandsAndChatter()
    {
        Assert.True(DigestScheduler.IsToolChatter("Tool: view_file", "tool"));
        Assert.True(DigestScheduler.IsToolChatter("Tool: grep_search", "tool"));
        Assert.True(DigestScheduler.IsToolChatter("Tool: find_by_name", "tool"));
        Assert.True(DigestScheduler.IsToolChatter("Tool: list_dir", "tool"));
        Assert.True(DigestScheduler.IsToolChatter("Tool: read_url_content", "tool"));
        Assert.True(DigestScheduler.IsToolChatter("Running: git status", "tool"));
        Assert.True(DigestScheduler.IsToolChatter("view_file details in path", "tool_call"));

        Assert.False(DigestScheduler.IsToolChatter("All 35 unit tests passed", "test"));
        Assert.False(DigestScheduler.IsToolChatter("Build succeeded", "build"));
        Assert.False(DigestScheduler.IsToolChatter("Confirm refactoring?", "approval"));
    }

    [Fact]
    public void IsRepeatedStatus_FiltersOutConsecutiveIdenticalStatuses()
    {
        using var summarizer = new MockDigestSummarizer();
        using var scheduler = new DigestScheduler(summarizer);

        Assert.False(scheduler.IsRepeatedStatus("sess-1", "working"));
        Assert.True(scheduler.IsRepeatedStatus("sess-1", "working")); // Duplicate!
        Assert.True(scheduler.IsRepeatedStatus("sess-1", "working")); // Duplicate!

        Assert.False(scheduler.IsRepeatedStatus("sess-1", "idle"));   // State changed
        Assert.True(scheduler.IsRepeatedStatus("sess-1", "idle"));    // Duplicate!

        Assert.False(scheduler.IsRepeatedStatus("sess-2", "working")); // Different session
    }

    [Fact]
    public async Task IngestFact_GroupsFactsByProject()
    {
        using var summarizer = new MockDigestSummarizer();
        using var scheduler = new DigestScheduler(summarizer);

        scheduler.IngestFact(new DigestFact("evt-1", "proj-a", "t1", "progress", "Step 1", DateTimeOffset.UtcNow));
        scheduler.IngestFact(new DigestFact("evt-2", "proj-b", "t2", "progress", "Step 1", DateTimeOffset.UtcNow));
        scheduler.IngestFact(new DigestFact("evt-3", "proj-a", "t1", "progress", "Step 2", DateTimeOffset.UtcNow));

        var digestA = await scheduler.FlushProjectAsync("proj-a");
        var digestB = await scheduler.FlushProjectAsync("proj-b");

        Assert.NotNull(digestA);
        Assert.Equal("proj-a", digestA.ProjectId);
        Assert.Equal(2, digestA.SourceEventIds.Count);

        Assert.NotNull(digestB);
        Assert.Equal("proj-b", digestB.ProjectId);
        Assert.Single(digestB.SourceEventIds);
    }

    [Fact]
    public async Task IngestConversation_ExtractsActivitiesAndAssistantMessages()
    {
        using var summarizer = new MockDigestSummarizer();
        using var scheduler = new DigestScheduler(summarizer);

        using var toolDoc = JsonDocument.Parse("{\"toolName\":\"run_command\"}");
        var conv = new AoConversationResponse(
            SessionId: "sess-1",
            Activities: new List<AoConversationActivity>
            {
                new(Id: "act-1", Kind: "progress", Delta: "All unit tests passed")
            },
            Messages: new List<AoConversationMessage>
            {
                new(Id: "msg-1", Sequence: 1, Role: "assistant", Text: "Slice implementation is complete.")
            }
        );

        scheduler.IngestConversation("sess-1", conv, "proj-1", "Optimus Voice OS");
        var digest = await scheduler.FlushProjectAsync("proj-1");

        Assert.NotNull(digest);
        Assert.Equal(2, digest.SourceEventIds.Count);
        Assert.Contains("act-1", digest.SourceEventIds);
        Assert.Contains("msg-1", digest.SourceEventIds);
    }

    [Fact]
    public async Task IngestCdcEvent_ExtractsPayloadAndSetsDecisionIfApprovalRequested()
    {
        using var summarizer = new MockDigestSummarizer();
        using var scheduler = new DigestScheduler(summarizer);

        var cdc = new AoCdcEvent(
            Seq: 101,
            ProjectId: "proj-1",
            SessionId: "sess-1",
            Type: "approval_requested",
            PayloadJson: "{\"message\":\"Approve PR #5?\",\"requiresDecision\":true,\"options\":[\"Approve\",\"Reject\"]}",
            CreatedAt: DateTimeOffset.UtcNow
        );

        DigestEvent? produced = null;
        scheduler.OnDigestProduced = d => produced = d;

        scheduler.IngestCdcEvent(cdc);

        // Decisions trigger immediate flush
        for (int i = 0; i < 20 && produced == null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(produced);
        Assert.True(produced.RequiresDecision);
        Assert.Contains("cdc-101", produced.SourceEventIds);
    }

    [Fact]
    public async Task SpeakOnlyMeaningfulResultsAndDecisions_SuppressesRoutineSpeech()
    {
        using var summarizer = new MockDigestSummarizer
        {
            SummarizeFunc = (p, n, t, facts) => new DigestEvent(
                Id: "dig-1",
                ProjectId: p,
                ProjectName: n,
                TaskRef: t,
                Headline: "Routine status",
                SpokenSummary: "Session working",
                RequiresDecision: false,
                SourceEventIds: facts.Select(f => f.EventId).ToList(),
                CreatedAt: DateTimeOffset.UtcNow,
                IsMeaningful: false // Not meaningful!
            )
        };

        using var scheduler = new DigestScheduler(summarizer);
        bool spoken = false;
        scheduler.OnSpeakDigest = _ => spoken = true;

        scheduler.IngestFact(new DigestFact("evt-1", "p1", "t1", "status", "working", DateTimeOffset.UtcNow));
        await scheduler.FlushProjectAsync("p1");

        Assert.False(spoken); // Routine non-meaningful result must not be spoken!
    }

    [Fact]
    public async Task RateLimiting_LimitsUnsolicitedSpokenDigestsToOnePerTwoMinutes()
    {
        using var summarizer = new MockDigestSummarizer();
        using var scheduler = new DigestScheduler(summarizer, options: new DigestSchedulerOptions
        {
            UnsolicitedSpokenCooldown = TimeSpan.FromMinutes(2)
        });

        int speakCount = 0;
        scheduler.OnSpeakDigest = _ => speakCount++;

        // First meaningful event speaks
        scheduler.IngestFact(new DigestFact("evt-1", "p1", "t1", "test", "Tests passed", DateTimeOffset.UtcNow, IsMeaningful: true));
        await scheduler.FlushProjectAsync("p1");
        Assert.Equal(1, speakCount);

        // Second meaningful event within 2 minutes is rate-limited
        scheduler.IngestFact(new DigestFact("evt-2", "p1", "t1", "test", "More tests passed", DateTimeOffset.UtcNow, IsMeaningful: true));
        await scheduler.FlushProjectAsync("p1");
        Assert.Equal(1, speakCount); // Still 1! Throttled by 2-minute cooldown.
    }

    [Fact]
    public async Task NeverInterruptSpeechOrApproval_PostponesSpokenDigest()
    {
        using var summarizer = new MockDigestSummarizer();
        using var scheduler = new DigestScheduler(summarizer);

        bool isCurrentlySpeaking = true;
        scheduler.IsSpeaking = () => isCurrentlySpeaking;

        int speakCount = 0;
        scheduler.OnSpeakDigest = _ => speakCount++;

        scheduler.IngestFact(new DigestFact("evt-1", "p1", "t1", "test", "Tests passed", DateTimeOffset.UtcNow, IsMeaningful: true));
        await scheduler.FlushProjectAsync("p1");

        // Should NOT speak while TTS is active
        Assert.Equal(0, speakCount);
        Assert.NotNull(scheduler.PendingSpokenDigest);

        // Now speech finishes (conversational pause)
        isCurrentlySpeaking = false;
        scheduler.NotifyConversationalPause();

        Assert.Equal(1, speakCount);
        Assert.Null(scheduler.PendingSpokenDigest);
    }

    [Fact]
    public async Task NeverInterruptApproval_PostponesUntilApprovalComplete()
    {
        using var summarizer = new MockDigestSummarizer();
        using var scheduler = new DigestScheduler(summarizer);

        bool isAwaitingApproval = true;
        scheduler.IsAwaitingApproval = () => isAwaitingApproval;

        int speakCount = 0;
        scheduler.OnSpeakDigest = _ => speakCount++;

        scheduler.IngestFact(new DigestFact("evt-1", "p1", "t1", "test", "Tests passed", DateTimeOffset.UtcNow, IsMeaningful: true));
        await scheduler.FlushProjectAsync("p1");

        Assert.Equal(0, speakCount);
        Assert.NotNull(scheduler.PendingSpokenDigest);

        // Approval complete
        isAwaitingApproval = false;
        scheduler.NotifyConversationalPause();

        Assert.Equal(1, speakCount);
    }

    [Fact]
    public async Task UserSpeech_CancelsAndPostponesDigest()
    {
        using var summarizer = new MockDigestSummarizer();
        using var scheduler = new DigestScheduler(summarizer);

        bool cancelInvoked = false;
        scheduler.OnCancelSpeaking = () => cancelInvoked = true;

        int speakCount = 0;
        scheduler.OnSpeakDigest = _ => speakCount++;

        scheduler.NotifyUserSpeechStarted();
        Assert.True(cancelInvoked);

        scheduler.IngestFact(new DigestFact("evt-1", "p1", "t1", "test", "Tests passed", DateTimeOffset.UtcNow, IsMeaningful: true));
        await scheduler.FlushProjectAsync("p1");

        // Blocked because user is speaking
        Assert.Equal(0, speakCount);

        // User finishes speaking
        scheduler.NotifyUserSpeechEnded();

        // Speaks at conversational pause
        Assert.Equal(1, speakCount);
    }

    [Fact]
    public async Task OpenDetails_ReturnsOriginalResponse()
    {
        using var summarizer = new MockDigestSummarizer
        {
            SummarizeFunc = (p, n, t, facts) => new DigestEvent(
                Id: "dig-details",
                ProjectId: p,
                ProjectName: n,
                TaskRef: t,
                Headline: "Detailed task",
                SpokenSummary: "Short summary",
                RequiresDecision: false,
                SourceEventIds: facts.Select(f => f.EventId).ToList(),
                CreatedAt: DateTimeOffset.UtcNow,
                OriginalResponse: "Full unsummarized assistant response with code block: int x = 42;",
                SourceEvents: facts
            )
        };

        using var scheduler = new DigestScheduler(summarizer);
        string? detailsFromCallback = null;
        scheduler.OnOpenOriginalResponse = d => detailsFromCallback = d;

        scheduler.IngestFact(new DigestFact("evt-1", "p1", "t1", "response", "Some text", DateTimeOffset.UtcNow));
        await scheduler.FlushProjectAsync("p1");

        string? details = scheduler.OpenDetails();

        Assert.NotNull(details);
        Assert.Contains("Full unsummarized assistant response with code block", details);
        Assert.Equal(details, detailsFromCallback);
    }

    [Fact]
    public async Task SummarizationFailure_RetainsSourceEventsInProducedDigest()
    {
        using var summarizer = new MockDigestSummarizer
        {
            SummarizeFunc = (p, n, t, facts) =>
            {
                // Simulate model error handled with fallback retention
                return DigestEvent.CreateFallback(p, n, t, facts, "GPU out of memory");
            }
        };

        using var scheduler = new DigestScheduler(summarizer);
        DigestEvent? produced = null;
        scheduler.OnDigestProduced = d => produced = d;

        var facts = new List<DigestFact>
        {
            new("evt-a", "p1", "t1", "progress", "Reading files", DateTimeOffset.UtcNow),
            new("evt-b", "p1", "t1", "progress", "Editing files", DateTimeOffset.UtcNow)
        };

        foreach (var f in facts) scheduler.IngestFact(f);
        await scheduler.FlushProjectAsync("p1");

        Assert.NotNull(produced);
        Assert.NotNull(produced.SourceEvents);
        Assert.Equal(2, produced.SourceEvents.Count);
        Assert.Equal(2, produced.SourceEventIds.Count);
        Assert.Contains("evt-a", produced.SourceEventIds);
        Assert.Contains("evt-b", produced.SourceEventIds);
        Assert.Contains("GPU out of memory", produced.DetailedSummary!);
    }

    [Theory]
    [InlineData("give me the details")]
    [InlineData("give me details")]
    [InlineData("show details")]
    public void ConversationCommand_ParsesGiveMeTheDetails(string input)
    {
        var cmd = ConversationCommand.Parse(input);
        Assert.NotNull(cmd);
        Assert.Equal("detailsOn", cmd.Kind);
    }
}
