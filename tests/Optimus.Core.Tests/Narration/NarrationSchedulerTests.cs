namespace Optimus.Core.Tests.Narration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Narration;
using Xunit;

public sealed class NarrationSchedulerTests
{
    [Fact]
    public void ConciseMode_SuppressesProgress_EmitsTransitionsAndFinalResponse()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Concise,
            NarrateToolsAndSkills = false
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Thinking about how to solve this."));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.StatusTransition, "Editing files"));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.AgentQuestion, "Should I proceed with refactoring?"));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.FinalResponse, "Done with implementation."));

        var items = DrainAll(scheduler);

        Assert.Equal(3, items.Count);
        Assert.Equal(NarrationEventType.StatusTransition, items[0].SourceType);
        Assert.Equal("Editing files", items[0].Text);

        Assert.Equal(NarrationEventType.AgentQuestion, items[1].SourceType);
        Assert.Equal("Should I proceed with refactoring?", items[1].Text);

        Assert.Equal(NarrationEventType.FinalResponse, items[2].SourceType);
        Assert.Equal("Done with implementation.", items[2].Text);
    }

    [Fact]
    public void ConciseMode_EmitsError()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Concise
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Error, "Build failed with exit code 1."));

        Assert.True(scheduler.TryDequeue(out var item));
        Assert.NotNull(item);
        Assert.Equal(NarrationEventType.Error, item.SourceType);
        Assert.Equal("Build failed with exit code 1.", item.Text);
    }

    [Fact]
    public void ComprehensiveMode_EmitsProgressInOrder()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Inspecting the codebase. "));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Inspecting the codebase. Found target method."));

        var items = DrainAll(scheduler);

        Assert.Equal(2, items.Count);
        Assert.Equal(NarrationEventType.Progress, items[0].SourceType);
        Assert.Equal("Inspecting the codebase.", items[0].Text);

        Assert.Equal(NarrationEventType.Progress, items[1].SourceType);
        Assert.Equal("Found target method.", items[1].Text);
    }

    [Theory]
    [InlineData(NarrationMode.Concise, false, false)]
    [InlineData(NarrationMode.Concise, true, true)]
    [InlineData(NarrationMode.Comprehensive, false, false)]
    [InlineData(NarrationMode.Comprehensive, true, true)]
    public void NarrateToolsAndSkills_ControlsToolEventsIndependentlyOfMode(
        NarrationMode mode,
        bool narrateTools,
        bool expectEmitted)
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = mode,
            NarrateToolsAndSkills = narrateTools
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.ToolCall, "git diff", name: "git"));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.SkillUse, "run search", name: "search"));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.ToolResult, "Clean working tree"));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.SkillResult, "Found 2 files"));

        var items = DrainAll(scheduler);

        if (expectEmitted)
        {
            Assert.Equal(4, items.Count);
            Assert.Equal(NarrationEventType.ToolCall, items[0].SourceType);
            Assert.Contains("git", items[0].Text, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(NarrationEventType.SkillUse, items[1].SourceType);
            Assert.Contains("search", items[1].Text, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(NarrationEventType.ToolResult, items[2].SourceType);
            Assert.Contains("Clean working tree", items[2].Text, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(NarrationEventType.SkillResult, items[3].SourceType);
            Assert.Contains("Found 2 files", items[3].Text, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Empty(items);
        }
    }

    [Fact]
    public void CodeBlockCollapsing_ReplacesFencedCodeBlocksWithTypedMarkers()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        string message = """
        I updated the service.
        ```csharp
        public class Greeter
        {
            public string SayHello() => "Hello";
        }
        ```
        The build succeeded.
        """;

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, message));

        var items = DrainAll(scheduler);

        Assert.Contains(items, item => item.Text.Contains("I updated the service.", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Text.StartsWith("[Code block: 4 lines of csharp]", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Text.Contains("The build succeeded.", StringComparison.Ordinal));
    }

    [Fact]
    public void RepetitiveLogCollapsing_ReplacesRepetitiveLinesWithTypedMarkers()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        string message = """
        Starting deployment.
        [INFO] Compiling module A
        [INFO] Compiling module B
        [INFO] Compiling module C
        [INFO] Compiling module D
        Deployment completed.
        """;

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, message));

        var items = DrainAll(scheduler);

        Assert.Contains(items, item => item.Text.Contains("Starting deployment.", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Text.StartsWith("[Log output: 4 lines]", StringComparison.Ordinal));
        Assert.Contains(items, item => item.Text.Contains("Deployment completed.", StringComparison.Ordinal));
    }

    [Fact]
    public void IncrementalStreaming_BuffersIncompleteSentencesUntilComplete()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        // 1. Partial fragment without sentence terminator
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "I am verifying the", isStreamingFragment: true));
        Assert.Equal(0, scheduler.QueuedCount);

        // 2. Fragment completed
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "I am verifying the test results now. Next we", isStreamingFragment: true));
        Assert.Equal(1, scheduler.QueuedCount);

        Assert.True(scheduler.TryDequeue(out var firstItem));
        Assert.NotNull(firstItem);
        Assert.Equal("I am verifying the test results now.", firstItem.Text);

        // 3. Final chunk flushes trailing fragment
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "I am verifying the test results now. Next we will ship it", isStreamingFragment: false));
        Assert.Equal(1, scheduler.QueuedCount);

        Assert.True(scheduler.TryDequeue(out var secondItem));
        Assert.NotNull(secondItem);
        Assert.Equal("Next we will ship it", secondItem.Text);
    }

    [Fact]
    public void Deduplication_IgnoresExactDuplicateVisibleTextRerenders()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Processing task 1."));
        Assert.Equal(1, scheduler.QueuedCount);

        // Exact duplicate re-render from UI
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Processing task 1."));
        Assert.Equal(1, scheduler.QueuedCount);

        var items = DrainAll(scheduler);
        Assert.Single(items);
        Assert.Equal("Processing task 1.", items[0].Text);
    }

    [Fact]
    public void Deduplication_HandlesSlidingWindowOverlaps()
    {
        var segmenter = new IncrementalSentenceSegmenter();

        // Virtualized UI stream where old prefix rolled off
        var chunk1 = segmenter.Ingest("Starting operation. Executing step 1. Executing step 2.", isFinal: false);
        Assert.Equal(3, chunk1.Count);

        var chunk2 = segmenter.Ingest("Executing step 2. Executing step 3.", isFinal: false);
        Assert.Single(chunk2);
        Assert.Equal("Executing step 3.", chunk2[0]);
    }

    [Fact]
    public void AbbreviationAndNumber_DoesNotSplitPrematurely()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "We deployed version 3.14 to e.g. production servers."));

        var items = DrainAll(scheduler);
        Assert.Single(items);
        Assert.Equal("We deployed version 3.14 to e.g. production servers.", items[0].Text);
    }

    [Fact]
    public void StrictOrdering_PreservesSequenceNumbersAcrossEventTypes()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive,
            NarrateToolsAndSkills = true
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.StatusTransition, "Step 1"));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Working on it."));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.ToolCall, "run tests", name: "runner"));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.FinalResponse, "Done."));

        var items = DrainAll(scheduler);

        Assert.Equal(4, items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            Assert.True(items[i].SequenceNumber > 0);
            if (i > 0)
            {
                Assert.True(items[i].SequenceNumber > items[i - 1].SequenceNumber);
            }
        }
    }

    [Fact]
    public void NewRunId_DiscardsQueuedItemsFromPreviousRun()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Run 1 sentence one. Run 1 sentence two."));
        Assert.True(scheduler.QueuedCount >= 2);

        // A new run starts!
        scheduler.Enqueue(new NarrationEvent("run-2", NarrationEventType.StatusTransition, "Run 2 starting"));

        var items = DrainAll(scheduler);

        // Queued items from run-1 should have been discarded
        Assert.Single(items);
        Assert.Equal("run-2", items[0].RunId);
        Assert.Equal("Run 2 starting", items[0].Text);
    }

    [Fact]
    public void DelayedEventFromOlderRun_IsDiscarded()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Run 1 started."));
        scheduler.Enqueue(new NarrationEvent("run-2", NarrationEventType.Progress, "Run 2 started."));

        // Delayed event from run-1 arrives after run-2 is active
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Run 1 late event."));

        var items = DrainAll(scheduler);

        Assert.All(items, item => Assert.Equal("run-2", item.RunId));
    }

    [Fact]
    public void ExplicitCancel_DiscardsQueuedItemsAndResetsBuffers()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.Progress, "Message one. Message two."));
        Assert.True(scheduler.QueuedCount > 0);

        scheduler.Cancel("run-1");

        Assert.Equal(0, scheduler.QueuedCount);
        Assert.False(scheduler.TryDequeue(out _));
    }

    [Fact]
    public async Task DequeueAsync_And_GetSpeakableStreamAsync_WorkCorrectly()
    {
        var scheduler = new NarrationScheduler(new NarrationOptions
        {
            Mode = NarrationMode.Comprehensive
        });

        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.StatusTransition, "Item 1"));
        scheduler.Enqueue(new NarrationEvent("run-1", NarrationEventType.StatusTransition, "Item 2"));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var item1 = await scheduler.DequeueAsync(cts.Token);
        Assert.Equal("Item 1", item1.Text);

        var item2 = await scheduler.DequeueAsync(cts.Token);
        Assert.Equal("Item 2", item2.Text);
    }

    private static List<SpeakableItem> DrainAll(NarrationScheduler scheduler)
    {
        var list = new List<SpeakableItem>();
        while (scheduler.TryDequeue(out var item))
        {
            list.Add(item);
        }
        return list;
    }
}
