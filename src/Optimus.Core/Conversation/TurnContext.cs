namespace Optimus.Core.Conversation;

/// <summary>Immutable facts captured when one user utterance enters the coordinator.</summary>
public sealed record TurnContext
{
    public TurnContext(
        string originDevice,
        string transcript,
        string? currentObjective = null,
        IEnumerable<string>? relevantMemoryRefs = null,
        DateTimeOffset? timestamp = null,
        string? foregroundApp = null,
        Guid? turnId = null)
        : this(
            turnId ?? Guid.NewGuid(),
            originDevice,
            transcript,
            currentObjective,
            relevantMemoryRefs,
            timestamp ?? DateTimeOffset.UtcNow,
            foregroundApp)
    {
    }

    public TurnContext(
        Guid turnId,
        string originDevice,
        string transcript,
        string? currentObjective,
        IEnumerable<string>? relevantMemoryRefs,
        DateTimeOffset timestamp,
        string? foregroundApp = null)
    {
        if (turnId == Guid.Empty) throw new ArgumentException("A turn ID is required.", nameof(turnId));
        ArgumentException.ThrowIfNullOrWhiteSpace(originDevice);
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);

        TurnId = turnId;
        OriginDevice = originDevice.Trim();
        Transcript = transcript.Trim();
        CurrentObjective = string.IsNullOrWhiteSpace(currentObjective) ? null : currentObjective.Trim();
        RelevantMemoryRefs = (relevantMemoryRefs ?? Array.Empty<string>())
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Select(reference => reference.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Timestamp = timestamp;
        ForegroundApp = string.IsNullOrWhiteSpace(foregroundApp) ? null : foregroundApp.Trim();
    }

    public Guid TurnId { get; }

    public string OriginDevice { get; }

    public string Transcript { get; }

    public string? CurrentObjective { get; }

    public IReadOnlyList<string> RelevantMemoryRefs { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>The app visible while this turn was captured; it is browsing context only.</summary>
    public string? ForegroundApp { get; }
}
