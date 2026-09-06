#pragma warning disable CA1861

namespace Optimus.Core.Tests.ExecutionPolicy;

using System;
using System.Collections.Generic;
using Optimus.Core.ExecutionPolicy;
using Xunit;

public sealed class ExecutionPolicyTests
{
    // =========================================================================
    // Criterion 4: Verify full leader fallback order with deterministic fixtures
    // =========================================================================
    [Fact]
    public void Leader_RoutesToCodex_WhenAllProvidersAvailable()
    {
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 80),
            [ExecutionProvider.Claude] = new(RemainingPercent: 90),
            [ExecutionProvider.Antigravity] = new(RemainingPercent: 100),
            [ExecutionProvider.OpenCode] = new(RemainingPercent: 50)
        };

        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Leader, capacities);

        Assert.Equal(ExecutionProvider.Codex, decision.Provider);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public void Leader_FallsBackToClaude_WhenCodexExhausted()
    {
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 0),
            [ExecutionProvider.Claude] = new(RemainingPercent: 80),
            [ExecutionProvider.Antigravity] = new(RemainingPercent: 100),
            [ExecutionProvider.OpenCode] = new(RemainingPercent: 50)
        };

        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Leader, capacities);

        Assert.Equal(ExecutionProvider.Claude, decision.Provider);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public void Leader_FallsBackToAntigravity_WhenCodexAndClaudeExhausted()
    {
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 5), // at handoff threshold
            [ExecutionProvider.Claude] = new(RemainingPercent: 0),
            [ExecutionProvider.Antigravity] = new(RemainingPercent: 75),
            [ExecutionProvider.OpenCode] = new(RemainingPercent: 50)
        };

        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Leader, capacities);

        Assert.Equal(ExecutionProvider.Antigravity, decision.Provider);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public void Leader_FallsBackToOpenCode_WhenCodexClaudeAntigravityExhausted()
    {
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 0),
            [ExecutionProvider.Claude] = new(RemainingPercent: 2), // <= 5%
            [ExecutionProvider.Antigravity] = new(Available: false),
            [ExecutionProvider.OpenCode] = new(RemainingPercent: 60)
        };

        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Leader, capacities);

        Assert.Equal(ExecutionProvider.OpenCode, decision.Provider);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public void Leader_Waits_WhenAllEligibleProvidersExhausted()
    {
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 0),
            [ExecutionProvider.Claude] = new(RemainingPercent: 3),
            [ExecutionProvider.Antigravity] = new(Available: false),
            [ExecutionProvider.OpenCode] = new(WeeklyRequestsRemaining: 0)
        };

        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Leader, capacities);

        Assert.Null(decision.Provider);
        Assert.True(decision.ShouldWait);
    }

    // =========================================================================
    // Criterion 5: Test 5 percent checkpointing, hard exhaustion, weekly limits, and unknown quota
    // =========================================================================
    [Theory]
    [InlineData(5, false, true)]   // at 5% threshold: cannot start new work, triggers handoff
    [InlineData(4, false, true)]   // below 5%: cannot start new work, triggers handoff
    [InlineData(0, false, true)]   // 0% hard exhaustion: cannot start new work
    [InlineData(6, true, false)]   // 6%: above threshold, can start new work
    [InlineData(100, true, false)] // 100%: can start new work
    public void FivePercentCheckpointing_And_HardExhaustion(int remaining, bool canStart, bool isAtHandoff)
    {
        var capacity = new ProviderCapacity(RemainingPercent: remaining);

        Assert.Equal(isAtHandoff, capacity.IsAtHandoffThreshold);
        Assert.Equal(canStart, capacity.CanStartNewWork);
        Assert.Equal(isAtHandoff, CapacityPolicy.StopStartingNewWork(capacity));
    }

    [Fact]
    public void WeeklyLimit_BlocksStartingNewWork()
    {
        var capacityZero = new ProviderCapacity(RemainingPercent: 90, WeeklyRequestsRemaining: 0);
        var capacityNegative = new ProviderCapacity(RemainingPercent: 90, WeeklyRequestsRemaining: -1);
        var capacityPositive = new ProviderCapacity(RemainingPercent: 90, WeeklyRequestsRemaining: 15);

        Assert.True(capacityZero.IsBlockedByWeeklyLimit);
        Assert.False(capacityZero.CanStartNewWork);

        Assert.True(capacityNegative.IsBlockedByWeeklyLimit);
        Assert.False(capacityNegative.CanStartNewWork);

        Assert.False(capacityPositive.IsBlockedByWeeklyLimit);
        Assert.True(capacityPositive.CanStartNewWork);
    }

    [Fact]
    public void UnknownQuota_IsNotSkipped_AndTreatedAsAvailableUntilReportedOtherwise()
    {
        var capacityUnknown = new ProviderCapacity(RemainingPercent: null, WeeklyRequestsRemaining: null);

        Assert.False(capacityUnknown.IsQuotaKnown);
        Assert.True(capacityUnknown.CanStartNewWork);

        // In RoleRouter, a missing provider entry is also treated as unknown quota and chosen
        var emptyCapacities = new Dictionary<ExecutionProvider, ProviderCapacity>();
        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Leader, emptyCapacities);

        Assert.Equal(ExecutionProvider.Codex, decision.Provider);
        Assert.Contains("unknown", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(decision.ShouldWait);
    }

    // =========================================================================
    // Criterion 6: Confirm routine work waits when Antigravity and OpenCode unavailable
    // =========================================================================
    [Fact]
    public void RoutineWork_RoutesToAntigravity_WhenAvailable()
    {
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Antigravity] = new(RemainingPercent: 80),
            [ExecutionProvider.OpenCode] = new(RemainingPercent: 80)
        };

        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Routine, capacities);

        Assert.Equal(ExecutionProvider.Antigravity, decision.Provider);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public void RoutineWork_FallsBackToOpenCode_WhenAntigravityUnavailable()
    {
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Antigravity] = new(Available: false),
            [ExecutionProvider.OpenCode] = new(RemainingPercent: 80)
        };

        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Routine, capacities);

        Assert.Equal(ExecutionProvider.OpenCode, decision.Provider);
        Assert.False(decision.ShouldWait);
    }

    [Fact]
    public void RoutineWork_Waits_WhenAntigravityAndOpenCodeUnavailable()
    {
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Antigravity] = new(RemainingPercent: 4), // at/below handoff threshold
            [ExecutionProvider.OpenCode] = new(Available: false)
        };

        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Routine, capacities);

        Assert.Null(decision.Provider);
        Assert.True(decision.ShouldWait);
        Assert.Contains("wait", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // Difficult and Review role routing verification
    // =========================================================================
    [Fact]
    public void DifficultWork_RoutesClaude_ThenCodex_ThenWaits()
    {
        var clAvailable = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Claude] = new(RemainingPercent: 50),
            [ExecutionProvider.Codex] = new(RemainingPercent: 50)
        };
        var clExhausted = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Claude] = new(RemainingPercent: 0),
            [ExecutionProvider.Codex] = new(RemainingPercent: 50)
        };
        var bothExhausted = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Claude] = new(RemainingPercent: 0),
            [ExecutionProvider.Codex] = new(RemainingPercent: 0)
        };

        Assert.Equal(ExecutionProvider.Claude, RoleRouter.Route(ExecutionRole.Difficult, clAvailable).Provider);
        Assert.Equal(ExecutionProvider.Codex, RoleRouter.Route(ExecutionRole.Difficult, clExhausted).Provider);
        Assert.Null(RoleRouter.Route(ExecutionRole.Difficult, bothExhausted).Provider);
    }

    [Fact]
    public void ReviewWork_RoutesCodex_ThenClaude_ThenWaits()
    {
        var codexAvailable = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 50),
            [ExecutionProvider.Claude] = new(RemainingPercent: 50)
        };
        var codexExhausted = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 0),
            [ExecutionProvider.Claude] = new(RemainingPercent: 50)
        };
        var bothExhausted = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Codex] = new(RemainingPercent: 0),
            [ExecutionProvider.Claude] = new(RemainingPercent: 0)
        };

        Assert.Equal(ExecutionProvider.Codex, RoleRouter.Route(ExecutionRole.Review, codexAvailable).Provider);
        Assert.Equal(ExecutionProvider.Claude, RoleRouter.Route(ExecutionRole.Review, codexExhausted).Provider);
        Assert.Null(RoleRouter.Route(ExecutionRole.Review, bothExhausted).Provider);
    }

    // =========================================================================
    // Criterion 2: Verify three independent workers run and additional work waits
    // =========================================================================
    [Fact]
    public void ThreeWorkersRun_And_FourthWorkerWaits()
    {
        var scheduler = new ExecutionScheduler();

        var task1 = new ExecutionTask("task-1", ExecutionRole.Routine);
        var task2 = new ExecutionTask("task-2", ExecutionRole.Routine);
        var task3 = new ExecutionTask("task-3", ExecutionRole.Routine);
        var task4 = new ExecutionTask("task-4", ExecutionRole.Routine);

        ScheduleDecision d1 = scheduler.TryStartWorker(task1);
        ScheduleDecision d2 = scheduler.TryStartWorker(task2);
        ScheduleDecision d3 = scheduler.TryStartWorker(task3);

        Assert.True(d1.Started);
        Assert.True(d2.Started);
        Assert.True(d3.Started);
        Assert.Equal(3, scheduler.ActiveWorkerCount);

        // Fourth worker must wait because MaxWorkerSlots == 3
        ScheduleDecision d4 = scheduler.TryStartWorker(task4);
        Assert.False(d4.Started);
        Assert.Equal(ScheduleStatus.WorkerSlotsFull, d4.Status);
        Assert.Equal(3, scheduler.ActiveWorkerCount);

        // Complete worker 2 -> slot opens, task 4 can now start
        bool completed = scheduler.CompleteWorker("task-2");
        Assert.True(completed);
        Assert.Equal(2, scheduler.ActiveWorkerCount);

        ScheduleDecision d4Retry = scheduler.TryStartWorker(task4);
        Assert.True(d4Retry.Started);
        Assert.Equal(3, scheduler.ActiveWorkerCount);
    }

    [Fact]
    public void Worker_WaitsForDependency_UntilDependencyCompletes()
    {
        var scheduler = new ExecutionScheduler();

        var task1 = new ExecutionTask("task-1", ExecutionRole.Routine);
        var task2 = new ExecutionTask("task-2", ExecutionRole.Routine, dependsOn: new[] { "task-1" });

        scheduler.TryStartWorker(task1);

        // Task 2 depends on task 1, which is not completed yet
        ScheduleDecision d2 = scheduler.TryStartWorker(task2);
        Assert.False(d2.Started);
        Assert.Equal(ScheduleStatus.WaitingForDependency, d2.Status);

        // Complete task 1
        scheduler.CompleteWorker("task-1");

        // Now task 2 can start
        ScheduleDecision d2After = scheduler.TryStartWorker(task2);
        Assert.True(d2After.Started);
    }

    [Fact]
    public void Worker_WaitsForOverlappingOwnership_UntilPriorTaskCompletes()
    {
        var scheduler = new ExecutionScheduler();

        var taskA = new ExecutionTask("task-A", ExecutionRole.Routine, ownershipKeys: new[] { "src/Optimus.Core/Audio" });
        var taskB = new ExecutionTask("task-B", ExecutionRole.Routine, ownershipKeys: new[] { "src/Optimus.Core/Audio" });

        scheduler.TryStartWorker(taskA);

        // Task B touches the same area -> serialized
        ScheduleDecision dB = scheduler.TryStartWorker(taskB);
        Assert.False(dB.Started);
        Assert.Equal(ScheduleStatus.WaitingForOwnership, dB.Status);

        scheduler.CompleteWorker("task-A");

        ScheduleDecision dBAfter = scheduler.TryStartWorker(taskB);
        Assert.True(dBAfter.Started);
    }

    [Fact]
    public void CoordinationTurn_RunsAlongsideThreeWorkers_AndSecondTurnWaits()
    {
        var scheduler = new ExecutionScheduler();

        // 3 workers run
        scheduler.TryStartWorker(new ExecutionTask("w1", ExecutionRole.Routine));
        scheduler.TryStartWorker(new ExecutionTask("w2", ExecutionRole.Routine));
        scheduler.TryStartWorker(new ExecutionTask("w3", ExecutionRole.Routine));
        Assert.Equal(3, scheduler.ActiveWorkerCount);

        // 1 coordination turn starts
        ScheduleDecision coord1 = scheduler.TryStartCoordinationTurn("coord-1");
        Assert.True(coord1.Started);
        Assert.True(scheduler.CoordinationTurnActive);

        // Second coordination turn must wait
        ScheduleDecision coord2 = scheduler.TryStartCoordinationTurn("coord-2");
        Assert.False(coord2.Started);
        Assert.Equal(ScheduleStatus.AlreadyActive, coord2.Status);

        // Complete coordination turn
        scheduler.CompleteCoordinationTurn();
        Assert.False(scheduler.CoordinationTurnActive);

        ScheduleDecision coord2After = scheduler.TryStartCoordinationTurn("coord-2");
        Assert.True(coord2After.Started);
    }

    // =========================================================================
    // Plan Approval Lifecycle Verification
    // =========================================================================
    [Fact]
    public void PlanApproval_Presentation_And_RevisionLifecycle()
    {
        var service = new PlanApprovalService();

        var tasks = new List<PlanTaskCard>
        {
            new("t1", "Setup Project", "Init git and baseline", new[] { "build passes", "test passes" }),
            new("t2", "Build Features", "Add components", new[] { "components render" }, dependsOn: new[] { "t1" })
        };

        ExecutionPlanRevision rev1 = service.Create(
            objective: "Ship Dogfood Slice",
            approachSummary: "Incremental personal MVP",
            tasks: tasks,
            planId: "plan-dogfood"
        );

        Assert.Equal(1, rev1.Revision);
        Assert.Equal(PlanRevisionState.PendingApproval, rev1.State);
        Assert.False(service.IsApproved("plan-dogfood", 1));
        Assert.Equal(2, rev1.Tasks.Count);

        // Approve revision 1
        bool approved = service.Approve("plan-dogfood", 1);
        Assert.True(approved);
        Assert.True(service.IsApproved("plan-dogfood", 1));

        // Editing an approved plan creates revision 2, which requires fresh approval
        var updatedTasks = new List<PlanTaskCard>(tasks)
        {
            new("t3", "Add Telemetry", "Emit basic metrics", new[] { "metrics logged" })
        };

        ExecutionPlanRevision rev2 = service.Edit("plan-dogfood", 1, "Ship Dogfood Slice v2", "Expanded scope", updatedTasks);
        Assert.Equal(2, rev2.Revision);
        Assert.Equal(PlanRevisionState.PendingApproval, rev2.State);
        Assert.False(service.IsApproved("plan-dogfood", 2));

        // Attempting to approve stale revision 1 fails
        Assert.False(service.Approve("plan-dogfood", 1));

        // Request changes on revision 2
        bool changesReq = service.RequestChanges("plan-dogfood", 2, "Exclude unnecessary telemetry for MVP");
        Assert.True(changesReq);
        Assert.Equal(PlanRevisionState.ChangesRequested, service.Get("plan-dogfood", 2)?.State);
    }
}
