namespace Optimus.Core.Tests;

using Optimus.Core.ExecutionPolicy;
using Xunit;

public sealed class ExecutionPolicyTests
{
    [Theory]
    [InlineData(ExecutionRole.Leader, ExecutionProvider.Codex)]
    [InlineData(ExecutionRole.Routine, ExecutionProvider.Antigravity)]
    [InlineData(ExecutionRole.Difficult, ExecutionProvider.Claude)]
    [InlineData(ExecutionRole.Review, ExecutionProvider.Codex)]
    public void RoleRouter_UsesRequiredPriority(ExecutionRole role, ExecutionProvider expected)
    {
        RoutingDecision decision = RoleRouter.Route(role, new Dictionary<ExecutionProvider, ProviderCapacity>());
        Assert.Equal(expected, decision.Provider);
    }

    [Fact]
    public void RoleRouter_SelectsTheNamedRoleModel()
    {
        Assert.Equal("Flash", RoleRouter.Route(ExecutionRole.Routine, new Dictionary<ExecutionProvider, ProviderCapacity>()).Model);
        Assert.Equal("Opus", RoleRouter.Route(ExecutionRole.Difficult, new Dictionary<ExecutionProvider, ProviderCapacity>()).Model);
        Assert.Equal("review", RoleRouter.Route(ExecutionRole.Review, new Dictionary<ExecutionProvider, ProviderCapacity>()).Model);
    }

    [Fact]
    public void RoleRouter_TreatsMissingQuotaAsUnknownRatherThanExhausted()
    {
        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Routine,
            new Dictionary<ExecutionProvider, ProviderCapacity>
            {
                [ExecutionProvider.Antigravity] = new(5),
                // OpenCode intentionally has no quota report.
            });

        Assert.Equal(ExecutionProvider.OpenCode, decision.Provider);
        Assert.Contains("unknown", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExecutionProvider.Codex, RoleRouter.Route(ExecutionRole.Leader).Provider);
    }

    [Fact]
    public void RoleRouter_RespectsWeeklyLimitAndHandoffThreshold()
    {
        RoutingDecision decision = RoleRouter.Route(ExecutionRole.Leader,
            new Dictionary<ExecutionProvider, ProviderCapacity>
            {
                [ExecutionProvider.Codex] = new(90, 0),
                [ExecutionProvider.Claude] = new(5, 10),
                [ExecutionProvider.Antigravity] = new(90, 10),
                [ExecutionProvider.OpenCode] = new(90, 10)
            });

        Assert.Equal(ExecutionProvider.Antigravity, decision.Provider);
    }

    [Fact]
    public void PlanApproval_IsBoundToRevision_AndEditNeedsNewApproval()
    {
        var service = new PlanApprovalService(() => DateTimeOffset.UnixEpoch);
        ExecutionPlanRevision first = service.Create("Ship", "Implement and test", [
            new PlanTaskCard("one", "Implement", "Write code", ["Build passes"])
        ], "plan-1");

        Assert.False(service.Approve("plan-1", 2));
        Assert.True(service.Approve("plan-1", first.Revision));
        Assert.True(service.IsApproved("plan-1", 1));

        ExecutionPlanRevision second = service.Edit("plan-1", 1, "Ship revised", "Implement, test, dogfood", first.Tasks);
        Assert.Equal(2, second.Revision);
        Assert.Equal(PlanRevisionState.PendingApproval, second.State);
        Assert.False(service.IsApproved("plan-1", 2));
        Assert.Equal(PlanRevisionState.Superseded, service.Get("plan-1", 1)!.State);
    }

    [Fact]
    public void Scheduler_UsesThreeSlots_SerializesDependenciesAndOwnership()
    {
        var scheduler = new ExecutionScheduler();
        Assert.True(scheduler.TryStartWorker(new("a", ExecutionRole.Routine, ownershipKeys: ["audio"])).Started);
        Assert.True(scheduler.TryStartWorker(new("b", ExecutionRole.Routine, ownershipKeys: ["phone"])).Started);
        Assert.True(scheduler.TryStartWorker(new("c", ExecutionRole.Routine, ownershipKeys: ["ui"])).Started);
        Assert.Equal(ScheduleStatus.WorkerSlotsFull, scheduler.TryStartWorker(new("d", ExecutionRole.Routine)).Status);
        scheduler.CompleteWorker("a");
        Assert.Equal(ScheduleStatus.WaitingForDependency,
            scheduler.TryStartWorker(new("e", ExecutionRole.Routine, dependsOn: ["missing"])).Status);
        Assert.Equal(ScheduleStatus.WaitingForOwnership,
            scheduler.TryStartWorker(new("f", ExecutionRole.Routine, ownershipKeys: ["phone"])).Status);
        Assert.Equal(ScheduleStatus.Completed, scheduler.TryStartWorker(new("a", ExecutionRole.Routine)).Status);
    }

    [Fact]
    public void Scheduler_AllowsOnlyOneCoordinationTurn()
    {
        var scheduler = new ExecutionScheduler();
        Assert.True(scheduler.TryStartCoordinationTurn("turn-1").Started);
        Assert.Equal(ScheduleStatus.AlreadyActive, scheduler.TryStartCoordinationTurn("turn-2").Status);
        Assert.True(scheduler.CompleteCoordinationTurn());
        Assert.True(scheduler.TryStartCoordinationTurn("turn-2").Started);
    }

    [Fact]
    public void LeaderConversation_IdentitySurvivesProviderTransfer()
    {
        var registry = new ProjectLeaderConversationRegistry(project => $"conversation:{project}");
        LeaderConversation first = registry.GetOrCreate("project", ExecutionProvider.Codex);
        LeaderConversation transferred = registry.Transfer("project", ExecutionProvider.Claude);

        Assert.Equal(first.ConversationId, transferred.ConversationId);
        Assert.Equal(ExecutionProvider.Claude, transferred.Provider);
    }

    [Fact]
    public void CapacityPolicy_OnlyHandsOffWhenKnownThresholdIsReached()
    {
        Assert.True(CapacityPolicy.StopStartingNewWork(new(5)));
        Assert.False(CapacityPolicy.StopStartingNewWork(new()));
        Assert.True(CapacityPolicy.MayReturnLeadership(new(50, 10)));
        Assert.False(CapacityPolicy.MayReturnLeadership(new(50, 0)));
    }

    [Fact]
    public void HandoffPlanner_TransfersOnlyAtThresholdAndChoosesNextEligibleRoleProvider()
    {
        var capacities = new Dictionary<ExecutionProvider, ProviderCapacity>
        {
            [ExecutionProvider.Claude] = new(5, 10),
            [ExecutionProvider.Codex] = new(80, 10),
            [ExecutionProvider.Antigravity] = new(80, 10)
        };

        QuotaHandoffDecision handoff = QuotaHandoffPlanner.Plan(ExecutionRole.Difficult, ExecutionProvider.Claude, capacities);
        Assert.True(handoff.Required);
        Assert.Equal(ExecutionProvider.Codex, handoff.Replacement);
        Assert.False(QuotaHandoffPlanner.Plan(ExecutionRole.Difficult, ExecutionProvider.Claude,
            new Dictionary<ExecutionProvider, ProviderCapacity> { [ExecutionProvider.Claude] = new(50, 10) }).Required);
    }

    [Fact]
    public void Coordinator_DoesNotStartTasksUntilTheDisplayedRevisionIsApproved()
    {
        var plans = new PlanApprovalService(() => DateTimeOffset.UnixEpoch);
        ExecutionPlanRevision plan = plans.Create("Objective", "Approach", [
            new PlanTaskCard("task", "Task", "Summary", ["Done"])
        ], "plan");
        var scheduler = new ExecutionScheduler();
        var coordinator = new ExecutionPolicyCoordinator(plans, scheduler);

        PlanStartResult stale = coordinator.ApproveAndStart("plan", plan.Revision + 1, [new("task", ExecutionRole.Routine)]);
        Assert.False(stale.Approved);
        Assert.Equal(0, scheduler.ActiveWorkerCount);

        PlanStartResult started = coordinator.ApproveAndStart("plan", plan.Revision, [new("task", ExecutionRole.Routine)]);
        Assert.True(started.Approved);
        Assert.Equal(ScheduleStatus.Started, Assert.Single(started.Tasks).Status);
    }

    [Fact]
    public async Task Handoff_StopsOutgoingBeforeWritingCheckpoint()
    {
        var events = new List<string>();
        var outgoing = new FakeProviderTurn(ExecutionProvider.Codex, events, "stop");
        var replacement = new FakeProviderTurn(ExecutionProvider.Claude, events, "write");

        ProviderHandoffResult result = await ProviderContinuityManager.HandoffAsync(
            outgoing, replacement, new HandoffCheckpoint("Run tests", changes: ["file.cs"]));

        Assert.True(result.Succeeded);
        Assert.Equal(["stop", "write"], events);
        Assert.Contains("nextAction", replacement.LastWrite, StringComparison.Ordinal);
    }

    private sealed class FakeProviderTurn(ExecutionProvider provider, List<string> events, string eventName) : IProviderTurn
    {
        public ExecutionProvider Provider { get; } = provider;
        public string LastWrite { get; private set; } = string.Empty;
        public Task StopAsync(CancellationToken cancellationToken = default) { events.Add(eventName); return Task.CompletedTask; }
        public Task WriteAsync(string text, CancellationToken cancellationToken = default) { LastWrite = text; events.Add(eventName); return Task.CompletedTask; }
    }
}
