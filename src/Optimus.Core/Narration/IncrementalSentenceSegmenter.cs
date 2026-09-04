namespace Optimus.Core.Narration;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Handles incremental streaming text ingestion, deduplication of identical or overlapping text,
/// and sentence/phrase segmentation for voice narration.
/// </summary>
public sealed class IncrementalSentenceSegmenter
{
    private static readonly string[] CommonAbbreviations =
    [
        "eg",
        "e.g",
        "ie",
        "i.e",
        "vs",
        "v",
        "etc",
        "dr",
        "mr",
        "mrs",
        "ms",
        "prof",
        "sr",
        "jr"
    ];

    private readonly StringBuilder _pendingBuffer = new();
    private string _lastFullText = string.Empty;

    /// <summary>
    /// Ingests a new visible text snapshot for the current stream.
    /// </summary>
    /// <param name="rawText">The visible text snapshot.</param>
    /// <param name="isFinal">Whether this update concludes the stream.</param>
    /// <returns>A list of newly completed speakable phrases or sentences.</returns>
    public IReadOnlyList<string> Ingest(string rawText, bool isFinal)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            if (isFinal)
            {
                return FlushRemaining();
            }

            return Array.Empty<string>();
        }

        // Deduplication: exact match
        if (string.Equals(rawText, _lastFullText, StringComparison.Ordinal))
        {
            if (isFinal)
            {
                return FlushRemaining();
            }

            return Array.Empty<string>();
        }

        string delta;

        if (_lastFullText.Length > 0 && rawText.StartsWith(_lastFullText, StringComparison.Ordinal))
        {
            // Simple append delta
            delta = rawText[_lastFullText.Length..];
            _lastFullText = rawText;
        }
        else if (_lastFullText.Length > 0)
        {
            int overlap = FindOverlapLength(_lastFullText, rawText);
            if (overlap > 0)
            {
                delta = rawText[overlap..];
                _lastFullText = rawText;
            }
            else
            {
                // New distinct text stream: flush any pending fragment from the previous stream
                var flushed = FlushRemaining();
                _pendingBuffer.Clear();
                _pendingBuffer.Append(rawText);
                _lastFullText = rawText;

                var extracted = ExtractSentences(isFinal);
                if (flushed.Length > 0)
                {
                    var combined = new List<string>(flushed.Length + extracted.Count);
                    combined.AddRange(flushed);
                    combined.AddRange(extracted);
                    return combined;
                }

                return extracted;
            }
        }
        else
        {
            delta = rawText;
            _lastFullText = rawText;
        }

        _pendingBuffer.Append(delta);
        return ExtractSentences(isFinal);
    }

    /// <summary>
    /// Clears all internal buffers and state.
    /// </summary>
    public void Reset()
    {
        _pendingBuffer.Clear();
        _lastFullText = string.Empty;
    }

    private string[] FlushRemaining()
    {
        string remaining = _pendingBuffer.ToString().Trim();
        _pendingBuffer.Clear();

        if (remaining.Length > 0)
        {
            return [remaining];
        }

        return Array.Empty<string>();
    }

    private List<string> ExtractSentences(bool isFinal)
    {
        var sentences = new List<string>();
        string current = _pendingBuffer.ToString();
        int consumedIndex = 0;
        int i = 0;

        while (i < current.Length)
        {
            char c = current[i];

            if (c == '[' && i + 1 < current.Length)
            {
                // Possible typed marker like [Code block: 5 lines] or [Log output: 3 lines]
                int closingBracket = current.IndexOf(']', i + 1);
                if (closingBracket != -1)
                {
                    // If there was preceding text, check if it forms a sentence
                    string preceding = current[consumedIndex..i].Trim();
                    if (preceding.Length > 0)
                    {
                        sentences.Add(preceding);
                    }

                    // Emit the marker as its own complete speakable chunk
                    string marker = current[i..(closingBracket + 1)].Trim();
                    sentences.Add(marker);

                    consumedIndex = closingBracket + 1;
                    i = consumedIndex;
                    continue;
                }
            }

            if (IsSentenceDelimiter(current, i))
            {
                int sentenceEnd = i + 1;
                string candidate = current[consumedIndex..sentenceEnd].Trim();

                if (candidate.Length > 0 && !IsAbbreviation(candidate))
                {
                    sentences.Add(candidate);
                    consumedIndex = sentenceEnd;
                }
            }

            i++;
        }

        if (consumedIndex > 0)
        {
            _pendingBuffer.Remove(0, consumedIndex);
        }

        if (isFinal)
        {
            string remaining = _pendingBuffer.ToString().Trim();
            _pendingBuffer.Clear();
            if (remaining.Length > 0)
            {
                sentences.Add(remaining);
            }
        }

        return sentences;
    }

    private static bool IsSentenceDelimiter(string text, int index)
    {
        char c = text[index];

        if (c is '\n' or '\r' or '!' or '?' or ';')
        {
            return true;
        }

        if (c == '.')
        {
            // Check for ellipsis "..."
            if (index + 2 < text.Length && text[index + 1] == '.' && text[index + 2] == '.')
            {
                return false;
            }
            if (index > 0 && text[index - 1] == '.')
            {
                // Part of ellipsis
                if (index + 1 >= text.Length || text[index + 1] != '.')
                {
                    return true;
                }
                return false;
            }

            // Must not be immediately followed by a digit (e.g. 3.14 or v2.0)
            if (index + 1 < text.Length && char.IsDigit(text[index + 1]))
            {
                return false;
            }

            // Must be followed by whitespace or end of string
            if (index + 1 >= text.Length || char.IsWhiteSpace(text[index + 1]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAbbreviation(string candidate)
    {
        // Check if the candidate ends with a known abbreviation
        int lastSpace = candidate.LastIndexOf(' ');
        string lastWord = lastSpace == -1 ? candidate : candidate[(lastSpace + 1)..];
        string clean = lastWord.TrimEnd('.', ',', '!', '?').ToLowerInvariant();

        foreach (string abbr in CommonAbbreviations)
        {
            if (string.Equals(clean, abbr, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindOverlapLength(string previous, string next)
    {
        int maxOverlap = Math.Min(previous.Length, next.Length);
        for (int len = maxOverlap; len >= 5; len--)
        {
            if (previous.EndsWith(next[..len], StringComparison.Ordinal))
            {
                return len;
            }
        }

        return 0;
    }
}
