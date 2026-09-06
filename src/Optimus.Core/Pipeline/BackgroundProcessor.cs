namespace Optimus.Core.Pipeline;

using System.Collections.Concurrent;

public sealed record BackgroundTaskInfo(Guid Id, string Name, DateTimeOffset QueuedAtUtc);

/// <summary>Runs non-interactive work away from the voice turn and narrates completion.</summary>
public sealed class BackgroundProcessor : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();
    private readonly Func<string, CancellationToken, Task>? _narrate;
    private readonly Action<string>? _log;
    private bool _disposed;

    public BackgroundProcessor(
        Func<string, CancellationToken, Task>? narrate = null,
        Action<string>? log = null)
    {
        _narrate = narrate;
        _log = log;
    }

    public IReadOnlyCollection<Guid> ActiveTaskIds => _tasks.Keys.ToArray();

    public event Action<BackgroundTaskInfo, string>? TaskCompleted;

    public BackgroundTaskInfo Queue(
        string name,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var info = new BackgroundTaskInfo(Guid.NewGuid(), name.Trim(), DateTimeOffset.UtcNow);
        Task task = RunAsync(info, operation, cancellationToken);
        _tasks[info.Id] = task;
        _ = task.ContinueWith(completed =>
        {
            _tasks.TryRemove(info.Id, out Task? _);
        }, TaskScheduler.Default);
        if (task.IsCompleted)
            _tasks.TryRemove(info.Id, out Task? _);
        return info;
    }

    public Task<BackgroundTaskInfo> QueueAsync(
        string name,
        Func<CancellationToken, Task<string>> operation,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Queue(name, operation, cancellationToken));

    private async Task RunAsync(BackgroundTaskInfo info, Func<CancellationToken, Task<string>> operation, CancellationToken cancellationToken)
    {
        try
        {
            string result = await operation(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result))
            {
                TaskCompleted?.Invoke(info, result);
                if (_narrate != null) await _narrate($"Background task {info.Name} completed. {result.Trim()}", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log?.Invoke($"[Background] {info.Name} was cancelled.");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Background] {info.Name} failed: {ex.Message}");
            if (_narrate != null)
                await _narrate($"Background task {info.Name} could not finish.", CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        Task[] tasks = _tasks.Values.ToArray();
        if (tasks.Length > 0) await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
