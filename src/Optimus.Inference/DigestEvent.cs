namespace Optimus.Inference;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

/// <summary>
/// A bounded fact extracted from Agent Orchestrator conversation or task events.
/// </summary>
public sealed record DigestFact(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("sessionOrTaskId")] string? SessionOrTaskId,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("isDecision")] bool IsDecision = false,
    [property: JsonPropertyName("isMeaningful")] bool IsMeaningful = false,
    [property: JsonPropertyName("options")] IReadOnlyList<string>? Options = null,
    [property: JsonPropertyName("rawPayload")] string? RawPayload = null
);

/// <summary>
/// A concise digest of agent activity produced by Gemma LLM summarization.
/// Contains source event IDs, project/task refs, headline, spoken summary, and decision-required flag.
/// </summary>
public sealed record DigestEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("projectName")] string? ProjectName,
    [property: JsonPropertyName("taskRef")] string? TaskRef,
    [property: JsonPropertyName("headline")] string Headline,
    [property: JsonPropertyName("spokenSummary")] string SpokenSummary,
    [property: JsonPropertyName("requiresDecision")] bool RequiresDecision,
    [property: JsonPropertyName("sourceEventIds")] IReadOnlyList<string> SourceEventIds,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("detailedSummary")] string? DetailedSummary = null,
    [property: JsonPropertyName("originalResponse")] string? OriginalResponse = null,
    [property: JsonPropertyName("decisionOptions")] IReadOnlyList<string>? DecisionOptions = null,
    [property: JsonPropertyName("sourceEvents")] IReadOnlyList<DigestFact>? SourceEvents = null,
    [property: JsonPropertyName("isMeaningful")] bool IsMeaningful = true
)
{
    /// <summary>
    /// Creates a safe fallback digest when LLM summarization fails or is unavailable.
    /// Crucially retains all raw source events, event IDs, and original responses.
    /// </summary>
    public static DigestEvent CreateFallback(
        string? projectId,
        string? projectName,
        string? taskRef,
        IReadOnlyList<DigestFact> facts,
        string? errorReason = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var ids = facts
            .Select(f => f.EventId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        bool requiresDecision = facts.Any(f => f.IsDecision);
        var decisionFact = facts.FirstOrDefault(f => f.IsDecision);
        var options = decisionFact?.Options ?? Array.Empty<string>();

        string targetName = projectName ?? taskRef ?? "Session";
        string headline = decisionFact != null
            ? $"Decision required: {targetName}"
            : $"{targetName}: {facts.Count} update{(facts.Count == 1 ? "" : "s")}";

        var meaningfulFacts = facts.Where(f => f.IsMeaningful || f.IsDecision).ToList();
        var primaryFact = (meaningfulFacts.Count > 0 ? meaningfulFacts[^1] : null) ?? (facts.Count > 0 ? facts[^1] : null);

        string spoken = primaryFact != null
            ? (requiresDecision ? $"Decision required on {targetName}. {primaryFact.Content}" : primaryFact.Content)
            : (requiresDecision ? "An agent requires your decision." : "New updates available.");

        string? original = string.Join("\n\n", facts
            .Where(f => !string.IsNullOrWhiteSpace(f.Content))
            .Select(f => f.Content));

        return new DigestEvent(
            Id: $"dig-{Guid.NewGuid():N}",
            ProjectId: projectId,
            ProjectName: projectName,
            TaskRef: taskRef,
            Headline: headline,
            SpokenSummary: spoken,
            RequiresDecision: requiresDecision,
            SourceEventIds: ids,
            CreatedAt: DateTimeOffset.UtcNow,
            DetailedSummary: $"Retained {facts.Count} raw source events{(errorReason != null ? $" (fallback: {errorReason})" : "")}.",
            OriginalResponse: string.IsNullOrWhiteSpace(original) ? null : original,
            DecisionOptions: options,
            SourceEvents: facts,
            IsMeaningful: requiresDecision || meaningfulFacts.Count > 0
        );
    }
}
