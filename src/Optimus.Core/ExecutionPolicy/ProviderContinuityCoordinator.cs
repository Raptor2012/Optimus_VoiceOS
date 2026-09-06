namespace Optimus.Core.ExecutionPolicy;

using System;
using System.Collections.Generic;
using System.Linq;

public sealed record TaskExecutionContext(
    string TaskId,
    string ProjectId,
    ExecutionRole Role,
    ExecutionProvider CurrentProvider,
    IReadOnlyList<string> UncommittedFiles,
    HandoffCheckpoint? LatestCheckpoint = null,
    bool IsRunning = false
);

public interface IProviderContinuityCoordinator
{
    TaskExecutionContext RegisterTask(
        string taskId,
        string projectId,
        ExecutionRole role,
        ExecutionProvider initialProvider,
        IEnumerable<string>? initialUncommittedFiles = null);

    TaskExecutionContext? GetTask(string taskId);

    void UpdateUncommittedWork(string taskId, IEnumerable<string> uncommittedFiles);

    HandoffRecord ExecuteHandoff(
        string taskId,
        HandoffCheckpoint checkpoint,
        IReadOnlyDictionary<ExecutionProvider, ProviderCapacity> capacities,
        Action? stopOutgoingAction = null);

    bool TryReturnLeadership(
        string taskId,
        IReadOnlyDictionary<ExecutionProvider, ProviderCapacity> capacities,
        Action? stopOutgoingAction = null);

    IReadOnlyList<HandoffRecord> GetHandoffHistory(string taskId);
}

public sealed class ProviderContinuityCoordinator : IProviderContinuityCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskExecutionContext> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HandoffRecord>> _handoffs = new(StringComparer.Ordinal);
    private readonly ProjectLeaderConversationRegistry _leaderRegistry;
    private readonly Func<DateTimeOffset> _clock;

    public ProviderContinuityCoordinator(
        ProjectLeaderConversationRegistry? leaderRegistry = null,
        Func<DateTimeOffset>? clock = null)
    {
        _leaderRegistry = leaderRegistry ?? new ProjectLeaderConversationRegistry();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public TaskExecutionContext RegisterTask(
        string taskId,
        string projectId,
        ExecutionRole role,
        ExecutionProvider initialProvider,
        IEnumerable<string>? initialUncommittedFiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var uncommitted = initialUncommittedFiles?.ToList() ?? new List<string>();
        var context = new TaskExecutionContext(
            TaskId: taskId,
            ProjectId: projectId,
            Role: role,
            CurrentProvider: initialProvider,
            UncommittedFiles: uncommitted.AsReadOnly(),
            LatestCheckpoint: null,
            IsRunning: true
        );

        lock (_gate)
        {
            _tasks[taskId] = context;
            _handoffs[taskId] = new List<HandoffRecord>();
            if (role == ExecutionRole.Leader)
            {
                _leaderRegistry.GetOrCreate(projectId, initialProvider);
            }
        }

        return context;
    }

    public TaskExecutionContext? GetTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        lock (_gate)
        {
            return _tasks.TryGetValue(taskId, out TaskExecutionContext? task) ? task : null;
        }
    }

    public void UpdateUncommittedWork(string taskId, IEnumerable<string> uncommittedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(uncommittedFiles);

        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out TaskExecutionContext? task))
            {
                throw new KeyNotFoundException($"Task '{taskId}' is not registered.");
            }

            _tasks[taskId] = task with
            {
                UncommittedFiles = uncommittedFiles.ToList().AsReadOnly()
            };
        }
    }

    /// <summary>
    /// Executes provider handoff: forcefully stops outgoing writes, creates checkpoint,
    /// preserves task identity and uncommitted files, and transfers to next eligible provider.
    /// </summary>
    public HandoffRecord ExecuteHandoff(
        string taskId,
        HandoffCheckpoint checkpoint,
        IReadOnlyDictionary<ExecutionProvider, ProviderCapacity> capacities,
        Action? stopOutgoingAction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(capacities);

        TaskExecutionContext current;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out TaskExecutionContext? task))
            {
                throw new KeyNotFoundException($"Task '{taskId}' is not registered.");
            }
            current = task;
        }

        // 1. Stop outgoing before replacement writes!
        stopOutgoingAction?.Invoke();

        // 2. Select replacement provider using role router priority
        // Mask out the outgoing provider from consideration if it triggered handoff
        var routingCapacities = new Dictionary<ExecutionProvider, ProviderCapacity>(capacities);
        if (routingCapacities.TryGetValue(current.CurrentProvider, out var outgoingCap))
        {
            // Ensure outgoing provider cannot be re-chosen during this handoff
            routingCapacities[current.CurrentProvider] = outgoingCap with { Available = false };
        }

        RoutingDecision decision = RoleRouter.Route(current.Role, routingCapacities);
        if (decision.Provider == null)
        {
            throw new InvalidOperationException(
                $"No eligible replacement provider found for role {current.Role}. {decision.Reason}");
        }

        ExecutionProvider replacement = decision.Provider.Value;

        // 3. Preserve task identity and uncommitted work across handoff
        var record = new HandoffRecord(
            TaskId: current.TaskId,
            ProjectId: current.ProjectId,
            OutgoingProvider: current.CurrentProvider,
            ReplacementProvider: replacement,
            Checkpoint: checkpoint,
            TimestampUtc: _clock(),
            PreservedUncommittedFiles: current.UncommittedFiles
        );

        lock (_gate)
        {
            _tasks[taskId] = current with
            {
                CurrentProvider = replacement,
                LatestCheckpoint = checkpoint,
                IsRunning = true
            };

            _handoffs[taskId].Add(record);

            if (current.Role == ExecutionRole.Leader)
            {
                _leaderRegistry.Transfer(current.ProjectId, replacement);
            }
        }

        return record;
    }

    /// <summary>
    /// Checks if a higher-priority leader provider has recovered capacity and returns leadership.
    /// </summary>
    public bool TryReturnLeadership(
        string taskId,
        IReadOnlyDictionary<ExecutionProvider, ProviderCapacity> capacities,
        Action? stopOutgoingAction = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(capacities);

        TaskExecutionContext current;
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out TaskExecutionContext? task)) return false;
            current = task;
        }

        IReadOnlyList<ExecutionProvider> priority = RoleRouter.PriorityFor(current.Role);
        int currentRank = -1;
        for (int i = 0; i < priority.Count; i++)
        {
            if (priority[i] == current.CurrentProvider)
            {
                currentRank = i;
                break;
            }
        }

        if (currentRank <= 0)
        {
            // Already at top priority provider
            return false;
        }

        // Check if any higher priority provider can resume leadership
        for (int i = 0; i < currentRank; i++)
        {
            ExecutionProvider candidate = priority[i];
            if (capacities.TryGetValue(candidate, out ProviderCapacity? cap) && CapacityPolicy.MayReturnLeadership(cap))
            {
                // Leadership can return!
                stopOutgoingAction?.Invoke();

                var checkpoint = current.LatestCheckpoint ?? new HandoffCheckpoint(
                    nextAction: $"Returning leadership from {current.CurrentProvider} to {candidate}.",
                    changes: current.UncommittedFiles
                );

                var record = new HandoffRecord(
                    TaskId: current.TaskId,
                    ProjectId: current.ProjectId,
                    OutgoingProvider: current.CurrentProvider,
                    ReplacementProvider: candidate,
                    Checkpoint: checkpoint,
                    TimestampUtc: _clock(),
                    PreservedUncommittedFiles: current.UncommittedFiles
                );

                lock (_gate)
                {
                    _tasks[taskId] = current with
                    {
                        CurrentProvider = candidate,
                        LatestCheckpoint = checkpoint
                    };

                    _handoffs[taskId].Add(record);

                    if (current.Role == ExecutionRole.Leader)
                    {
                        _leaderRegistry.Transfer(current.ProjectId, candidate);
                    }
                }

                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<HandoffRecord> GetHandoffHistory(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        lock (_gate)
        {
            return _handoffs.TryGetValue(taskId, out List<HandoffRecord>? list)
                ? list.ToArray()
                : Array.Empty<HandoffRecord>();
        }
    }
}
