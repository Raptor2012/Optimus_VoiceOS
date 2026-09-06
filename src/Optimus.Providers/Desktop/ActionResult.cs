namespace Optimus.Providers.Desktop;

using System;

/// <summary>
/// Execution status for a desktop tool action.
/// </summary>
public enum ActionStatus
{
    Completed,
    Paused,
    Stopped,
    Unresolved
}

/// <summary>
/// Result of executing a desktop observation or action with status, details, and observed result.
/// </summary>
public sealed record ActionResult
{
    public ActionStatus Status { get; init; }

    public string Detail { get; init; } = string.Empty;

    public string? ObservedResult { get; init; }

    public bool Success => Status == ActionStatus.Completed;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public TimeSpan Duration { get; init; } = TimeSpan.Zero;

    public object? OutputData { get; init; }

    public static ActionResult Completed(string detail, string? observedResult = null, object? outputData = null, TimeSpan? duration = null) =>
        new()
        {
            Status = ActionStatus.Completed,
            Detail = detail,
            ObservedResult = observedResult,
            OutputData = outputData,
            Duration = duration ?? TimeSpan.Zero
        };

    public static ActionResult Paused(string detail, string? observedResult = null, TimeSpan? duration = null) =>
        new()
        {
            Status = ActionStatus.Paused,
            Detail = detail,
            ObservedResult = observedResult,
            Duration = duration ?? TimeSpan.Zero
        };

    public static ActionResult Stopped(string detail, string? observedResult = null, TimeSpan? duration = null) =>
        new()
        {
            Status = ActionStatus.Stopped,
            Detail = detail,
            ObservedResult = observedResult,
            Duration = duration ?? TimeSpan.Zero
        };

    public static ActionResult Unresolved(string detail, string? observedResult = null, TimeSpan? duration = null) =>
        new()
        {
            Status = ActionStatus.Unresolved,
            Detail = detail,
            ObservedResult = observedResult,
            Duration = duration ?? TimeSpan.Zero
        };
}
