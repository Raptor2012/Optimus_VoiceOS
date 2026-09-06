namespace Optimus.Core.ExecutionPolicy;

/// <summary>The small set of execution roles used by the personal-use orchestrator.</summary>
public enum ExecutionRole
{
    Leader,
    Routine,
    Difficult,
    Review
}

public enum ExecutionProvider
{
    Codex,
    Claude,
    Antigravity,
    OpenCode
}

/// <summary>
/// Current provider capacity. A null value is deliberately unknown, not zero.
/// </summary>
public sealed record ProviderCapacity(
    int? RemainingPercent = null,
    int? WeeklyRequestsRemaining = null,
    bool Available = true)
{
    public bool IsQuotaKnown => RemainingPercent.HasValue || WeeklyRequestsRemaining.HasValue;

    public bool IsBlockedByWeeklyLimit => WeeklyRequestsRemaining is <= 0;

    public bool IsAtHandoffThreshold => RemainingPercent is <= 5;

    public bool CanStartNewWork => Available && !IsBlockedByWeeklyLimit && !IsAtHandoffThreshold && RemainingPercent is not 0;
}

public sealed record RoutingDecision(
    ExecutionRole Role,
    ExecutionProvider? Provider,
    IReadOnlyList<ExecutionProvider> Considered,
    string Reason)
{
    public bool ShouldWait => Provider is null;

    public string? Model => Provider is null ? null : RoleRouter.ModelFor(Role, Provider.Value);
}

public sealed record PlanTaskCard
{
    public PlanTaskCard(string id, string title, string summary, IReadOnlyList<string> doneConditions,
        IReadOnlyList<string>? dependsOn = null, string? owner = null, ExecutionRole? role = null,
        IReadOnlyList<string>? ownershipKeys = null, ExecutionProvider? provider = null)
    {
        Id = id;
        Title = title;
        Summary = summary;
        DoneConditions = doneConditions;
        DependsOn = dependsOn ?? Array.Empty<string>();
        Owner = owner;
        Role = role;
        OwnershipKeys = ownershipKeys ?? Array.Empty<string>();
        Provider = provider;
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        ArgumentNullException.ThrowIfNull(doneConditions);
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public IReadOnlyList<string> DoneConditions { get; }
    public IReadOnlyList<string> DependsOn { get; }
    public string? Owner { get; }
    public ExecutionRole? Role { get; }
    public IReadOnlyList<string> OwnershipKeys { get; }
    public ExecutionProvider? Provider { get; }

    public bool IsExpanded { get; init; }
}

public enum PlanRevisionState
{
    PendingApproval,
    Approved,
    ChangesRequested,
    Superseded
}

/// <summary>A user-visible plan revision. Approval is tied to the revision number.</summary>
public sealed record ExecutionPlanRevision
{
    public ExecutionPlanRevision(string planId, int revision, string objective, string approachSummary,
        IReadOnlyList<PlanTaskCard> tasks, PlanRevisionState state, DateTimeOffset createdAtUtc, string? changeRequest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        ArgumentException.ThrowIfNullOrWhiteSpace(approachSummary);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        PlanId = planId;
        Revision = revision;
        Objective = objective;
        ApproachSummary = approachSummary;
        Tasks = tasks;
        State = state;
        CreatedAtUtc = createdAtUtc;
        ChangeRequest = changeRequest;
    }

    public string PlanId { get; }
    public int Revision { get; }
    public string Objective { get; }
    public string ApproachSummary { get; }
    public IReadOnlyList<PlanTaskCard> Tasks { get; }
    public PlanRevisionState State { get; init; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? ChangeRequest { get; init; }

    /// <summary>The fields needed to render the approval card.</summary>
    public string ApprovalSummary => $"{Objective}: {ApproachSummary}";
}

public sealed record ExecutionTask
{
    public ExecutionTask(string id, ExecutionRole role, IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? ownershipKeys = null, ExecutionProvider? provider = null)
    {
        Id = id;
        Role = role;
        DependsOn = dependsOn ?? Array.Empty<string>();
        OwnershipKeys = ownershipKeys ?? Array.Empty<string>();
        Provider = provider;
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
    }

    public string Id { get; }
    public ExecutionRole Role { get; }
    public IReadOnlyList<string> DependsOn { get; }
    public IReadOnlyList<string> OwnershipKeys { get; }
    public ExecutionProvider? Provider { get; }
}

public enum ScheduleStatus
{
    Started,
    WaitingForDependency,
    WaitingForOwnership,
    WorkerSlotsFull,
    AlreadyActive,
    Completed
}

public sealed record ScheduleDecision(ScheduleStatus Status, string TaskId, string Detail)
{
    public bool Started => Status == ScheduleStatus.Started;
}

public sealed record HandoffCheckpoint(
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Tests,
    IReadOnlyList<string> Blockers,
    string NextAction)
{
    public HandoffCheckpoint(string nextAction, IReadOnlyList<string>? changes = null,
        IReadOnlyList<string>? decisions = null, IReadOnlyList<string>? tests = null,
        IReadOnlyList<string>? blockers = null)
        : this(changes ?? Array.Empty<string>(), decisions ?? Array.Empty<string>(),
            tests ?? Array.Empty<string>(), blockers ?? Array.Empty<string>(), nextAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nextAction);
    }
}
