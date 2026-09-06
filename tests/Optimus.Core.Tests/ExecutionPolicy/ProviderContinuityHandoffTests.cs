#pragma warning disable CA1861

namespace Optimus.Core.Tests.ExecutionPolicy;

using System;
using System.Collections.Generic;
using Optimus.Core.ExecutionPolicy;
using Xunit;

public sealed class ProviderContinuityHandoffTests
{
    [Fact]
    public void TaskIdentity_And_UncommittedWork_PreservedThroughProviderHandoff()
    {
        var leaderRegistry = new ProjectLeaderConversationRegistry();
        var coordinator = new ProviderContinuityCoordinator(leaderRegistry);

        var initialUncommitted = new List<string>
        {
            "src/Optimus.Core/Audio/EchoCanceller.cs",
            "src/Optimus.Shell/Controls/VoiceOrb.xaml"
        };

        // 1. Register task with initial provider Codex
        TaskExecutionContext task = coordinator.RegisterTask(
            taskId: "task-dogfood-1",
            projectId: "optimus_voiceos",
            role: ExecutionRole.Leader,
            initialProvider: ExecutionProvider.Codex,
            initialUncommittedFiles: initialUncommitted
        );

        Assert.Equal("task-dogfood-1", task.TaskId);
        Assert.Equal("optimus_voiceos", task.ProjectId);
        Assert.Equal(ExecutionProvider.Codex, task.CurrentProvider);
        Assert.Equal(2, task.UncommittedFiles.Count);

        // Verify leader conversation was established
        LeaderConversation? conv = leaderRegistry.Get("optimus_voiceos");
        Assert.NotNull(conv);
        string originalConversationId = conv.ConversationId;
        Assert.Equal(ExecutionProvider.Codex, conv.Provider);

        // Update uncommitted work during task execution
        var updatedUncommitted = new List<string>(initialUncommitted)
        {
            "tests/Optimus.Core.Tests/EchoCancellerTests.cs"
        };
        coordinator.UpdateUncommittedWork("task-dogfood-1", updatedUncommitted);

        // 2. Outgoing provider hits 5% handoff threshold; prepare capacities
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 5), // at 5% threshold
            [ExecutionProvider.Claude] = new(RemainingPercent: 85),
            [ExecutionProvider.Antigravity] = new(RemainingPercent: 90),
            [ExecutionProvider.OpenCode] = new(RemainingPercent: 50)
        };

        bool outgoingStopped = false;
        void StopOutgoing()
        {
            outgoingStopped = true;
        }

        var checkpoint = new HandoffCheckpoint(
            nextAction: "Continue running integration tests for EchoCanceller",
            changes: updatedUncommitted,
            decisions: new[] { "Use WebRTC APM for Windows echo cancellation" },
            tests: new[] { "VAD pre-roll buffer unit tests pass" },
            blockers: new[] { "Codex capacity reached 5% handoff threshold" }
        );

        // 3. Execute Handoff
        HandoffRecord record = coordinator.ExecuteHandoff(
            taskId: "task-dogfood-1",
            checkpoint: checkpoint,
            capacities: capacities,
            stopOutgoingAction: StopOutgoing
        );

        // Assert that outgoing was stopped before replacement writes!
        Assert.True(outgoingStopped);

        // Assert that task identity was preserved byte-for-byte
        Assert.Equal("task-dogfood-1", record.TaskId);
        Assert.Equal("optimus_voiceos", record.ProjectId);
        Assert.Equal(ExecutionProvider.Codex, record.OutgoingProvider);
        Assert.Equal(ExecutionProvider.Claude, record.ReplacementProvider);

        // Assert that uncommitted work was preserved
        Assert.Equal(3, record.PreservedUncommittedFiles.Count);
        Assert.Contains("src/Optimus.Core/Audio/EchoCanceller.cs", record.PreservedUncommittedFiles);
        Assert.Contains("tests/Optimus.Core.Tests/EchoCancellerTests.cs", record.PreservedUncommittedFiles);

        // Assert that task context is updated with replacement provider and checkpoint
        TaskExecutionContext? updatedTask = coordinator.GetTask("task-dogfood-1");
        Assert.NotNull(updatedTask);
        Assert.Equal(ExecutionProvider.Claude, updatedTask.CurrentProvider);
        Assert.Equal(3, updatedTask.UncommittedFiles.Count);
        Assert.NotNull(updatedTask.LatestCheckpoint);
        Assert.Equal("Continue running integration tests for EchoCanceller", updatedTask.LatestCheckpoint.NextAction);

        // Assert that persistent leader conversation identity was preserved and transferred
        LeaderConversation? transferredConv = leaderRegistry.Get("optimus_voiceos");
        Assert.NotNull(transferredConv);
        Assert.Equal(originalConversationId, transferredConv.ConversationId); // Conversation ID preserved!
        Assert.Equal(ExecutionProvider.Claude, transferredConv.Provider);
    }

    [Fact]
    public void StopOutgoing_IsExecuted_BeforeReplacementProviderIsAssigned()
    {
        var coordinator = new ProviderContinuityCoordinator();

        coordinator.RegisterTask(
            taskId: "task-stop-check",
            projectId: "proj-1",
            role: ExecutionRole.Leader,
            initialProvider: ExecutionProvider.Codex
        );

        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 2),
            [ExecutionProvider.Claude] = new(RemainingPercent: 80)
        };

        bool wasStopped = false;
        ExecutionProvider? providerAtTimeOfStop = null;

        void StopOutgoing()
        {
            wasStopped = true;
            providerAtTimeOfStop = coordinator.GetTask("task-stop-check")?.CurrentProvider;
        }

        var checkpoint = new HandoffCheckpoint(nextAction: "Continue after stop");

        HandoffRecord record = coordinator.ExecuteHandoff(
            "task-stop-check",
            checkpoint,
            capacities,
            StopOutgoing
        );

        Assert.True(wasStopped);
        // At the moment of stop, the task was still on the outgoing provider!
        Assert.Equal(ExecutionProvider.Codex, providerAtTimeOfStop);
        Assert.Equal(ExecutionProvider.Claude, record.ReplacementProvider);
    }

    [Fact]
    public void ReturnLeadership_ReturnsToPreferredProvider_WhenCapacityBecomesAvailable()
    {
        var leaderRegistry = new ProjectLeaderConversationRegistry();
        var coordinator = new ProviderContinuityCoordinator(leaderRegistry);

        coordinator.RegisterTask(
            taskId: "task-leadership-return",
            projectId: "proj-return",
            role: ExecutionRole.Leader,
            initialProvider: ExecutionProvider.Codex
        );

        // First handoff from Codex -> Claude because Codex was at 5%
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 5),
            [ExecutionProvider.Claude] = new(RemainingPercent: 90)
        };

        coordinator.ExecuteHandoff(
            "task-leadership-return",
            new HandoffCheckpoint("Waiting for Codex quota refresh"),
            capacities
        );

        Assert.Equal(ExecutionProvider.Claude, coordinator.GetTask("task-leadership-return")?.CurrentProvider);

        // Now Codex quota has recovered to 80% (hourly/daily quota window reset)
        var recoveredCapacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 80),
            [ExecutionProvider.Claude] = new(RemainingPercent: 90)
        };

        bool returnStopped = false;
        bool returned = coordinator.TryReturnLeadership(
            "task-leadership-return",
            recoveredCapacities,
            stopOutgoingAction: () => returnStopped = true
        );

        Assert.True(returned);
        Assert.True(returnStopped);
        Assert.Equal(ExecutionProvider.Codex, coordinator.GetTask("task-leadership-return")?.CurrentProvider);

        // Verify handoff history has 2 records: Codex->Claude, then Claude->Codex
        IReadOnlyList<HandoffRecord> history = coordinator.GetHandoffHistory("task-leadership-return");
        Assert.Equal(2, history.Count);
        Assert.Equal(ExecutionProvider.Codex, history[0].OutgoingProvider);
        Assert.Equal(ExecutionProvider.Claude, history[0].ReplacementProvider);
        Assert.Equal(ExecutionProvider.Claude, history[1].OutgoingProvider);
        Assert.Equal(ExecutionProvider.Codex, history[1].ReplacementProvider);
    }
}
