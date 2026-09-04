namespace Optimus.Core.Narration;

using System;

/// <summary>
/// Represents an observed event from a coding agent or window.
/// </summary>
public sealed record NarrationEvent
{
    /// <summary>
    /// Gets the unique generation or run identifier.
    /// </summary>
    public string RunId { get; init; }

    /// <summary>
    /// Gets the type of event.
    /// </summary>
    public NarrationEventType Type { get; init; }

    /// <summary>
    /// Gets the visible or announced text.
    /// </summary>
    public string Text { get; init; }

    /// <summary>
    /// Gets the timestamp of the event.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Gets an optional tool or skill name associated with the event.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is an incremental streaming update that may be followed by more text.
    /// </summary>
    public bool IsStreamingFragment { get; init; }

    public NarrationEvent(
        string runId,
        NarrationEventType type,
        string text,
        DateTimeOffset timestamp = default,
        string? name = null,
        bool isStreamingFragment = false)
    {
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        Type = type;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Timestamp = timestamp == default ? DateTimeOffset.UtcNow : timestamp;
        Name = name;
        IsStreamingFragment = isStreamingFragment;
    }
}
