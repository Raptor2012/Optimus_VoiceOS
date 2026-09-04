namespace Optimus.Core.Narration;

using System;

/// <summary>
/// A phrase, sentence, or event marker ready to be synthesized and spoken by the TTS engine.
/// </summary>
public sealed record SpeakableItem
{
    /// <summary>
    /// Gets the run identifier that produced this speakable item.
    /// </summary>
    public string RunId { get; init; }

    /// <summary>
    /// Gets the original event type that generated this item.
    /// </summary>
    public NarrationEventType SourceType { get; init; }

    /// <summary>
    /// Gets the speakable text string.
    /// </summary>
    public string Text { get; init; }

    /// <summary>
    /// Gets the strictly monotonic sequence number within the scheduler's lifetime.
    /// </summary>
    public long SequenceNumber { get; init; }

    /// <summary>
    /// Gets the timestamp when this speakable item was scheduled.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    public SpeakableItem(
        string runId,
        NarrationEventType sourceType,
        string text,
        long sequenceNumber,
        DateTimeOffset timestamp = default)
    {
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        SourceType = sourceType;
        Text = text ?? throw new ArgumentNullException(nameof(text));
        SequenceNumber = sequenceNumber;
        Timestamp = timestamp == default ? DateTimeOffset.UtcNow : timestamp;
    }
}
