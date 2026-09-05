namespace Optimus.Core.Voice;

using System;
using System.Collections.Generic;
using System.Text;
using Optimus.Providers.Windows;

public enum WindowSelectionCommandType
{
    Unknown,
    Selected,
    Refresh,
    Repeat,
    Cancel,
}

public sealed record WindowSelectionResult(
    WindowSelectionCommandType Command,
    WindowCandidate? SelectedCandidate = null);

/// <summary>
/// Classifies spoken window selection commands without invoking a language model.
/// </summary>
public static class WindowSelectionCommandClassifier
{
    private static readonly HashSet<string> CancelCommands = new(StringComparer.Ordinal)
    {
        "cancel",
        "no",
        "stop",
        "abort",
        "scratch that",
        "never mind",
    };

    private static readonly HashSet<string> RefreshCommands = new(StringComparer.Ordinal)
    {
        "refresh windows",
        "refresh window",
        "refresh",
        "retry",
        "reload",
        "check again",
        "find windows",
        "recheck",
    };

    private static readonly HashSet<string> RepeatCommands = new(StringComparer.Ordinal)
    {
        "repeat options",
        "repeat option",
        "repeat",
        "say again",
        "options",
        "read options",
        "repeat windows",
        "what are the options",
    };

    private static readonly HashSet<string> SingleCandidateConfirmCommands = new(StringComparer.Ordinal)
    {
        "yes",
        "yeah",
        "yep",
        "confirm",
        "bind",
        "use that window",
        "use this window",
        "use window",
        "that window",
        "this window",
        "that one",
        "this one",
        "use that",
        "use this",
        "window one",
        "window 1",
        "first window",
        "one",
        "1",
        "first",
    };

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
    {
        ["1"] = 1,
        ["one"] = 1,
        ["first"] = 1,
        ["2"] = 2,
        ["two"] = 2,
        ["second"] = 2,
        ["3"] = 3,
        ["three"] = 3,
        ["third"] = 3,
        ["4"] = 4,
        ["four"] = 4,
        ["fourth"] = 4,
        ["5"] = 5,
        ["five"] = 5,
        ["fifth"] = 5,
        ["6"] = 6,
        ["six"] = 6,
        ["sixth"] = 6,
        ["7"] = 7,
        ["seven"] = 7,
        ["seventh"] = 7,
        ["8"] = 8,
        ["eight"] = 8,
        ["eighth"] = 8,
        ["9"] = 9,
        ["nine"] = 9,
        ["ninth"] = 9,
        ["10"] = 10,
        ["ten"] = 10,
        ["tenth"] = 10,
    };

    public static WindowSelectionResult Classify(string? transcript, IReadOnlyList<WindowCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        string normalized = Normalize(transcript);
        if (string.IsNullOrEmpty(normalized))
        {
            return new(WindowSelectionCommandType.Unknown);
        }

        if (CancelCommands.Contains(normalized))
        {
            return new(WindowSelectionCommandType.Cancel);
        }

        if (RefreshCommands.Contains(normalized))
        {
            return new(WindowSelectionCommandType.Refresh);
        }

        if (RepeatCommands.Contains(normalized))
        {
            return new(WindowSelectionCommandType.Repeat);
        }

        if (candidates.Count == 0)
        {
            return new(WindowSelectionCommandType.Unknown);
        }

        // Single candidate confirmation
        if (candidates.Count == 1 && SingleCandidateConfirmCommands.Contains(normalized))
        {
            return new(WindowSelectionCommandType.Selected, candidates[0]);
        }

        // Number selection (e.g. "window two", "two", "number 2", "second window", "use window 1")
        if (TryExtractNumber(normalized, out int number))
        {
            if (number >= 1 && number <= candidates.Count)
            {
                return new(WindowSelectionCommandType.Selected, candidates[number - 1]);
            }

            return new(WindowSelectionCommandType.Unknown);
        }

        // Title matching (exact or unique substring match)
        WindowCandidate? matched = MatchCandidateByTitle(normalized, candidates);
        if (matched != null)
        {
            return new(WindowSelectionCommandType.Selected, matched);
        }

        return new(WindowSelectionCommandType.Unknown);
    }

    private static bool TryExtractNumber(string normalized, out int number)
    {
        number = 0;

        string text = normalized;

        // Strip common leading phrases
        string[] prefixes =
        {
            "use window number ",
            "select window number ",
            "choose window number ",
            "window number ",
            "use window ",
            "select window ",
            "choose window ",
            "bind window ",
            "pick window ",
            "window ",
            "number ",
            "option ",
            "use ",
            "select ",
            "choose ",
        };

        foreach (string prefix in prefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        // Strip trailing "window" (e.g. "first window", "second window")
        if (text.EndsWith(" window", StringComparison.Ordinal))
        {
            text = text[..^" window".Length].Trim();
        }

        return NumberWords.TryGetValue(text, out number);
    }

    private static WindowCandidate? MatchCandidateByTitle(string normalized, IReadOnlyList<WindowCandidate> candidates)
    {
        var matches = new List<WindowCandidate>();

        foreach (WindowCandidate candidate in candidates)
        {
            string candidateTitleNorm = Normalize(candidate.Title);
            if (string.IsNullOrEmpty(candidateTitleNorm))
            {
                continue;
            }

            if (string.Equals(normalized, candidateTitleNorm, StringComparison.Ordinal) ||
                candidateTitleNorm.Contains(normalized, StringComparison.Ordinal) ||
                normalized.Contains(candidateTitleNorm, StringComparison.Ordinal))
            {
                matches.Add(candidate);
            }
        }

        // Return candidate only if unique match
        return matches.Count == 1 ? matches[0] : null;
    }

    internal static string Normalize(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return string.Empty;
        }

        var normalized = new StringBuilder(transcript.Length);
        bool needsSpace = false;

        foreach (Rune rune in transcript.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (needsSpace && normalized.Length > 0)
                {
                    normalized.Append(' ');
                }

                normalized.Append(Rune.ToLowerInvariant(rune));
                needsSpace = false;
            }
            else
            {
                needsSpace = true;
            }
        }

        return normalized.ToString();
    }
}
