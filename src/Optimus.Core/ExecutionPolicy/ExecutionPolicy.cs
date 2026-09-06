namespace Optimus.Core.ExecutionPolicy;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

/// <summary>Routes roles in fixed priority order without silently falling back.</summary>
public static class RoleRouter
{
    private static readonly IReadOnlyDictionary<ExecutionRole, ExecutionProvider[]> Priorities =
        new Dictionary<ExecutionRole, ExecutionProvider[]>
        {
            [ExecutionRole.Leader] = [ExecutionProvider.Codex, ExecutionProvider.Claude, ExecutionProvider.Antigravity, ExecutionProvider.OpenCode],
            [ExecutionRole.Routine] = [ExecutionProvider.Antigravity, ExecutionProvider.OpenCode],
            [ExecutionRole.Difficult] = [ExecutionProvider.Claude, ExecutionProvider.Codex],
            [ExecutionRole.Review] = [ExecutionProvider.Codex, ExecutionProvider.Claude]
        };

    public static IReadOnlyList<ExecutionProvider> PriorityFor(ExecutionRole role) => Priorities[role];

    public static string? ModelFor(ExecutionRole role, ExecutionProvider provider) =>
        (role, provider) switch
        {
            (ExecutionRole.Routine, ExecutionProvider.Antigravity) => "Flash",
            (ExecutionRole.Difficult, ExecutionProvider.Claude) => "Opus",
            (ExecutionRole.Review, ExecutionProvider.Codex) => "review",
            _ => null
        };

    public static RoutingDecision Route(ExecutionRole role, IReadOnlyDictionary<ExecutionProvider, ProviderCapacity>? capacities = null)
    {
        capacities ??= new Dictionary<ExecutionProvider, ProviderCapacity>();
        ExecutionProvider[] considered = Priorities[role];
        foreach (ExecutionProvider provider in considered)
        {
            if (!capacities.TryGetValue(provider, out ProviderCapacity? capacity))
            {
                // A missing quota is unknown. It is not permission to skip this provider.
                return new RoutingDecision(role, provider, considered,
                    $"{provider} capacity is unknown; use it until the provider reports otherwise.");
            }

            if (capacity.CanStartNewWork)
            {
                return new RoutingDecision(role, provider, considered, $"{provider} is first eligible provider.");
            }
        }

        return new RoutingDecision(role, null, considered,
            "All eligible providers are unavailable, at their handoff threshold, or at their weekly limit; wait.");
    }
}

public sealed record QuotaHandoffDecision(
    bool Required,
    ExecutionProvider? Replacement,
    string Reason)
{
    public bool ShouldWait => Required && Replacement is null;
}

/// <summary>Chooses a role-eligible replacement only after the outgoing provider reaches 5%.</summary>
public static class QuotaHandoffPlanner
{
    public static QuotaHandoffDecision Plan(ExecutionRole role, ExecutionProvider outgoing,
        IReadOnlyDictionary<ExecutionProvider, ProviderCapacity>? capacities = null)
    {
        capacities ??= new Dictionary<ExecutionProvider, ProviderCapacity>();
        if (!capacities.TryGetValue(outgoing, out ProviderCapacity? capacity) || !CapacityPolicy.StopStartingNewWork(capacity))
            return new(false, null, "Outgoing capacity is above the handoff threshold or unknown.");

        var eligible = new Dictionary<ExecutionProvider, ProviderCapacity>(capacities);
        // Keep the outgoing entry explicitly blocked; removing it would make the required
        // "missing quota is unknown" rule select the provider we are trying to leave.
        eligible[outgoing] = new ProviderCapacity(0, 0, false);
        RoutingDecision route = RoleRouter.Route(role, eligible);
        return route.Provider is { } replacement
            ? new(true, replacement, $"Transfer at the safe boundary to {replacement}.")
            : new(true, null, "No eligible replacement is available; checkpoint and wait.");
    }
}

public sealed record PlanStartResult(bool Approved, IReadOnlyList<ScheduleDecision> Tasks, string Detail);

/// <summary>Connects the approval card to the bounded scheduler; no task starts before approval.</summary>
public sealed class ExecutionPolicyCoordinator
{
    private readonly PlanApprovalService _plans;
    private readonly ExecutionScheduler _scheduler;

    public ExecutionPolicyCoordinator(PlanApprovalService plans, ExecutionScheduler scheduler)
    {
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    }

    public PlanStartResult ApproveAndStart(string planId, int revision, IReadOnlyList<ExecutionTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (!_plans.Approve(planId, revision))
            return new(false, Array.Empty<ScheduleDecision>(), "Only the displayed pending revision can be approved.");

        List<ScheduleDecision> decisions = [];
        foreach (ExecutionTask task in tasks) decisions.Add(_scheduler.TryStartWorker(task));
        return new(true, decisions, "Approved revision started where dependencies, ownership, and slots permit.");
    }

    public bool RequestChanges(string planId, int revision, string feedback) =>
        _plans.RequestChanges(planId, revision, feedback);
}

/// <summary>Applies the 5% checkpoint rule and deliberately treats absent quota as unknown.</summary>
public static class CapacityPolicy
{
    public const int HandoffThresholdPercent = 5;

    public static bool StopStartingNewWork(ProviderCapacity capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        return capacity.IsAtHandoffThreshold;
    }

    public static bool MayReturnLeadership(ProviderCapacity capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        return capacity.Available && !capacity.IsBlockedByWeeklyLimit && !capacity.IsAtHandoffThreshold && capacity.RemainingPercent is not 0;
    }
}

/// <summary>Owns immutable plan revisions and makes stale approvals harmless.</summary>
public sealed class PlanApprovalService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ExecutionPlanRevision>> _plans = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public PlanApprovalService(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public ExecutionPlanRevision Create(string objective, string approachSummary, IReadOnlyList<PlanTaskCard> tasks, string? planId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        ArgumentException.ThrowIfNullOrWhiteSpace(approachSummary);
        ArgumentNullException.ThrowIfNull(tasks);
        string id = string.IsNullOrWhiteSpace(planId) ? Guid.NewGuid().ToString("N") : planId;
        var revision = NewRevision(id, 1, objective, approachSummary, tasks, PlanRevisionState.PendingApproval);
        lock (_gate) _plans.Add(id, [revision]);
        return revision;
    }

    public ExecutionPlanRevision? Get(string planId, int? revision = null)
    {
        lock (_gate)
        {
            if (!_plans.TryGetValue(planId, out List<ExecutionPlanRevision>? revisions)) return null;
            return revision.HasValue
                ? revisions.FirstOrDefault(item => item.Revision == revision.Value)
                : revisions[^1];
        }
    }

    public IReadOnlyList<ExecutionPlanRevision> History(string planId)
    {
        lock (_gate)
        {
            return _plans.TryGetValue(planId, out List<ExecutionPlanRevision>? revisions)
                ? revisions.ToArray()
                : Array.Empty<ExecutionPlanRevision>();
        }
    }

    public bool Approve(string planId, int revision)
    {
        lock (_gate)
        {
            ExecutionPlanRevision? current = Current(planId);
            if (current is null || current.Revision != revision || current.State != PlanRevisionState.PendingApproval) return false;
            Replace(planId, current with { State = PlanRevisionState.Approved });
            return true;
        }
    }

    public bool RequestChanges(string planId, int revision, string feedback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback);
        lock (_gate)
        {
            ExecutionPlanRevision? current = Current(planId);
            if (current is null || current.Revision != revision || current.State != PlanRevisionState.PendingApproval) return false;
            Replace(planId, current with { State = PlanRevisionState.ChangesRequested, ChangeRequest = feedback });
            return true;
        }
    }

    /// <summary>Every edit creates a new pending revision, including edits to an approved plan.</summary>
    public ExecutionPlanRevision Edit(string planId, int basedOnRevision, string objective,
        string approachSummary, IReadOnlyList<PlanTaskCard> tasks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        ArgumentException.ThrowIfNullOrWhiteSpace(approachSummary);
        ArgumentNullException.ThrowIfNull(tasks);
        lock (_gate)
        {
            ExecutionPlanRevision? current = Current(planId);
            if (current is null || current.Revision != basedOnRevision)
                throw new InvalidOperationException("The plan revision is stale or does not exist.");
            Replace(planId, current with { State = PlanRevisionState.Superseded });
            var revision = NewRevision(planId, current.Revision + 1, objective, approachSummary, tasks, PlanRevisionState.PendingApproval);
            _plans[planId].Add(revision);
            return revision;
        }
    }

    public bool IsApproved(string planId, int revision) => Get(planId, revision)?.State == PlanRevisionState.Approved;

    private ExecutionPlanRevision NewRevision(string planId, int revision, string objective, string approach,
        IReadOnlyList<PlanTaskCard> tasks, PlanRevisionState state) =>
        new(planId, revision, objective, approach, tasks.ToArray(), state, _clock());

    private ExecutionPlanRevision? Current(string planId) =>
        _plans.TryGetValue(planId, out List<ExecutionPlanRevision>? revisions) ? revisions[^1] : null;

    private void Replace(string planId, ExecutionPlanRevision replacement)
    {
        List<ExecutionPlanRevision> revisions = _plans[planId];
        revisions[^1] = replacement;
    }
}

/// <summary>Three parallel workers and one independent coordination turn.</summary>
public sealed class ExecutionScheduler
{
    public const int MaxWorkerSlots = 3;
    private readonly object _gate = new();
    private readonly Dictionary<string, ExecutionTask> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private bool _coordinationTurnActive;

    public int ActiveWorkerCount { get { lock (_gate) return _active.Count; } }
    public bool CoordinationTurnActive { get { lock (_gate) return _coordinationTurnActive; } }

    public ScheduleDecision TryStartWorker(ExecutionTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (_completed.Contains(task.Id)) return new(ScheduleStatus.Completed, task.Id, "Task already completed.");
            if (_active.ContainsKey(task.Id)) return new(ScheduleStatus.AlreadyActive, task.Id, "Task is already running.");
            if (_active.Count >= MaxWorkerSlots) return new(ScheduleStatus.WorkerSlotsFull, task.Id, "All three worker slots are active.");
            if (task.DependsOn.Any(dependency => !_completed.Contains(dependency)))
                return new(ScheduleStatus.WaitingForDependency, task.Id, "A dependency has not completed.");
            if (_active.Values.Any(active => Overlaps(active.OwnershipKeys, task.OwnershipKeys)))
                return new(ScheduleStatus.WaitingForOwnership, task.Id, "Another active task owns an overlapping area.");
            _active.Add(task.Id, task);
            return new(ScheduleStatus.Started, task.Id, "Worker slot reserved.");
        }
    }

    public bool CompleteWorker(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        lock (_gate)
        {
            if (!_active.Remove(taskId)) return false;
            _completed.Add(taskId);
            return true;
        }
    }

    public ScheduleDecision TryStartCoordinationTurn(string turnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        lock (_gate)
        {
            if (_coordinationTurnActive) return new(ScheduleStatus.AlreadyActive, turnId, "A coordination turn is already active.");
            _coordinationTurnActive = true;
            return new(ScheduleStatus.Started, turnId, "Coordination turn reserved.");
        }
    }

    public bool CompleteCoordinationTurn()
    {
        lock (_gate)
        {
            if (!_coordinationTurnActive) return false;
            _coordinationTurnActive = false;
            return true;
        }
    }

    public bool IsCompleted(string taskId) { lock (_gate) return _completed.Contains(taskId); }

    private static bool Overlaps(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Any(key => right.Contains(key, StringComparer.OrdinalIgnoreCase));
}

/// <summary>Stable project leader conversation identities survive provider handoffs.</summary>
public sealed record LeaderConversation(string ProjectId, string ConversationId, ExecutionProvider Provider);

public sealed class ProjectLeaderConversationRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LeaderConversation> _conversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string> _conversationIdFactory;

    public ProjectLeaderConversationRegistry(Func<string, string>? conversationIdFactory = null) =>
        _conversationIdFactory = conversationIdFactory ?? (_ => Guid.NewGuid().ToString("N"));

    public LeaderConversation GetOrCreate(string projectId, ExecutionProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_gate)
        {
            if (_conversations.TryGetValue(projectId, out LeaderConversation? existing)) return existing;
            var created = new LeaderConversation(projectId, _conversationIdFactory(projectId), provider);
            _conversations.Add(projectId, created);
            return created;
        }
    }

    public LeaderConversation? Get(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_gate)
        {
            return _conversations.TryGetValue(projectId, out LeaderConversation? existing) ? existing : null;
        }
    }

    public LeaderConversation Transfer(string projectId, ExecutionProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_gate)
        {
            LeaderConversation current = GetOrCreate(projectId, provider);
            var transferred = current with { Provider = provider };
            _conversations[projectId] = transferred;
            return transferred;
        }
    }
}
