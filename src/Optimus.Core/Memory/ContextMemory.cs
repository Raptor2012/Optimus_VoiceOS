namespace Optimus.Core.Memory;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Optimus.Core.Conversation;

/// <summary>
/// In-memory sliding window of recent conversation turns for the conversation coordinator.
/// Holds the last 20 turns (by default) and provides keyword-overlap search to supply
/// relevant conversational context for new objectives.
/// </summary>
public sealed class ContextMemory
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "above", "after", "again", "against", "all", "am", "an", "and", "any", "are", "as", "at",
        "be", "because", "been", "before", "being", "below", "between", "both", "but", "by",
        "can", "could", "did", "do", "does", "doing", "down", "during",
        "each", "few", "for", "from", "further",
        "had", "has", "have", "having", "he", "her", "here", "hers", "herself", "him", "himself", "his", "how",
        "i", "if", "in", "into", "is", "it", "its", "itself",
        "just", "me", "more", "most", "my", "myself",
        "no", "nor", "not", "now",
        "of", "off", "on", "once", "only", "or", "other", "our", "ours", "ourselves", "out", "over", "own",
        "same", "she", "should", "so", "some", "such",
        "than", "that", "the", "their", "theirs", "them", "themselves", "then", "there", "these", "they", "this", "those", "through", "to", "too",
        "under", "until", "up", "very",
        "was", "we", "were", "what", "when", "where", "which", "while", "who", "whom", "why", "with", "would",
        "you", "your", "yours", "yourself", "yourselves"
    };

    private static readonly Regex TokenRegex = new(@"\b[\w-]+\b", RegexOptions.Compiled);

    private readonly int _maxTurns;
    private readonly List<ConversationTurnRecord> _turns = new();
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ContextMemory"/> class.
    /// </summary>
    /// <param name="maxTurns">Maximum number of recent turns to retain (default is 20).</param>
    public ContextMemory(int maxTurns = 20)
    {
        _maxTurns = maxTurns > 0 ? maxTurns : 20;
    }

    /// <summary>
    /// Gets the maximum capacity of the sliding window.
    /// </summary>
    public int MaxTurns => _maxTurns;

    /// <summary>
    /// Gets the current number of turns held in memory.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _turns.Count;
            }
        }
    }

    /// <summary>
    /// Gets an immutable snapshot of current turns in chronological order.
    /// </summary>
    public IReadOnlyList<ConversationTurnRecord> Turns
    {
        get
        {
            lock (_lock)
            {
                return _turns.ToArray();
            }
        }
    }

    /// <summary>
    /// Adds a turn record to the sliding window, evicting the oldest turn if capacity is exceeded.
    /// </summary>
    public void AddTurn(ConversationTurnRecord turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        lock (_lock)
        {
            _turns.Add(turn);
            while (_turns.Count > _maxTurns)
            {
                _turns.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Convenience helper to construct and add a turn record.
    /// </summary>
    public void AddTurn(
        string turnId,
        string transcript,
        string? objective = null,
        string? response = null,
        string device = "pc",
        string? toolCallsJson = null,
        DateTimeOffset? timestamp = null)
    {
        AddTurn(new ConversationTurnRecord(
            0,
            turnId,
            device,
            transcript,
            objective,
            response,
            toolCallsJson,
            timestamp ?? DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Adds a <see cref="TurnContext"/> to the sliding window with an optional assistant response.
    /// </summary>
    public void AddTurn(TurnContext turn, string? response = null, string? toolCallsJson = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        AddTurn(new ConversationTurnRecord(
            0,
            turn.TurnId.ToString(),
            turn.OriginDevice,
            turn.Transcript,
            turn.CurrentObjective,
            response,
            toolCallsJson,
            turn.Timestamp));
    }

    /// <summary>
    /// Updates the response text for an existing turn in memory.
    /// </summary>
    public bool UpdateResponse(string turnId, string response)
    {
        if (string.IsNullOrWhiteSpace(turnId)) return false;

        lock (_lock)
        {
            for (int i = _turns.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_turns[i].TurnId, turnId, StringComparison.OrdinalIgnoreCase))
                {
                    _turns[i] = _turns[i] with { Response = response };
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Retrieves recent turns up to the specified count, newest first or chronologically.
    /// </summary>
    public IReadOnlyList<ConversationTurnRecord> GetRecentTurns(int? count = null)
    {
        lock (_lock)
        {
            if (count == null || count >= _turns.Count)
            {
                return _turns.ToArray();
            }

            int take = Math.Max(0, count.Value);
            return _turns.Skip(_turns.Count - take).ToArray();
        }
    }

    /// <summary>
    /// Searches recent turns by keyword overlap with the specified objective or query.
    /// Returns matching turns sorted by relevance (overlap score) descending.
    /// </summary>
    /// <param name="objective">The current goal, question, or user query.</param>
    /// <param name="maxResults">Maximum number of relevant turns to return.</param>
    public IReadOnlyList<ConversationTurnRecord> GetRelevantContext(string objective, int maxResults = 5)
    {
        if (string.IsNullOrWhiteSpace(objective) || maxResults <= 0)
        {
            return Array.Empty<ConversationTurnRecord>();
        }

        HashSet<string> queryKeywords = ExtractKeywords(objective);
        if (queryKeywords.Count == 0)
        {
            return Array.Empty<ConversationTurnRecord>();
        }

        List<ConversationTurnRecord> snapshot;
        lock (_lock)
        {
            snapshot = new List<ConversationTurnRecord>(_turns);
        }

        var scored = new List<(ConversationTurnRecord Turn, int Score)>();
        foreach (ConversationTurnRecord turn in snapshot)
        {
            HashSet<string> turnKeywords = ExtractKeywords(
                $"{turn.Objective} {turn.Transcript} {turn.Response}");

            int overlap = 0;
            foreach (string keyword in queryKeywords)
            {
                if (turnKeywords.Contains(keyword))
                {
                    overlap++;
                }
            }

            if (overlap > 0)
            {
                scored.Add((turn, overlap));
            }
        }

        return scored
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Turn.Timestamp)
            .Take(maxResults)
            .Select(item => item.Turn)
            .ToArray();
    }

    /// <summary>
    /// Formats the relevant turns into a human/model-readable context block.
    /// </summary>
    public string FormatRelevantContext(string objective, int maxResults = 5)
    {
        IReadOnlyList<ConversationTurnRecord> turns = GetRelevantContext(objective, maxResults);
        if (turns.Count == 0) return string.Empty;

        return string.Join("\n---\n", turns.Select(turn =>
            $"Turn [{turn.TurnId}]: {turn.Transcript}\n" +
            (string.IsNullOrWhiteSpace(turn.Objective) ? "" : $"Objective: {turn.Objective}\n") +
            (string.IsNullOrWhiteSpace(turn.Response) ? "" : $"Response: {turn.Response}\n")).Select(s => s.TrimEnd()));
    }

    /// <summary>
    /// Clears all turns from memory.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _turns.Clear();
        }
    }

    private static HashSet<string> ExtractKeywords(string text)
    {
        var words = TokenRegex.Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length > 1)
            .ToList();

        var nonStop = words.Where(w => !StopWords.Contains(w)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (nonStop.Count > 0)
        {
            return nonStop;
        }

        return words.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
