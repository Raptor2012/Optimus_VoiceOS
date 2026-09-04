namespace Optimus.Inference;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Splits a spoken message into short segments so playback can start on the first one.
/// </summary>
/// <remarks>
/// Nothing here rewrites the text. Segments are concatenated back to exactly the input, because
/// the widget speaks the draft the user is looking at and a segmenter that "tidied" punctuation
/// would make the spoken and displayed text disagree.
/// <para>
/// Splitting is on sentence enders, then on clause commas once a segment is already long enough
/// to be worth starting. A code identifier like <c>api.ts</c> or a decimal must not end a
/// segment, so a period only counts when what follows looks like a new sentence.
/// </para>
/// </remarks>
public static class SpeechSegmenter
{
    /// <summary>Below this, a further split costs more in gaps than it saves in latency.</summary>
    public const int MinSegmentChars = 24;

    /// <summary>
    /// The first segment may break earlier, because time-to-first-audio scales with how much
    /// text the engine has to synthesize before it emits anything. Starting on a shorter opening
    /// phrase gets sound out sooner; the rest is already queued by the time it finishes, so the
    /// earlier break costs nothing audible.
    /// </summary>
    public const int FirstSegmentMinChars = 14;

    /// <summary>Past this, split at the next reasonable boundary even mid-sentence.</summary>
    public const int MaxSegmentChars = 240;

    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var segments = new List<string>();
        var current = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            current.Append(c);

            int minimum = segments.Count == 0 ? FirstSegmentMinChars : MinSegmentChars;

            bool boundary = c switch
            {
                '.' or '!' or '?' => EndsSentence(text, i),
                ',' or ';' or ':' => current.Length >= minimum,
                _ => false
            };

            if (!boundary && current.Length >= MaxSegmentChars && c == ' ')
            {
                boundary = true;
            }

            if (boundary && current.Length >= minimum)
            {
                segments.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }

        // A trailing fragment shorter than one word is not worth its own synthesis call.
        if (segments.Count > 1 && segments[^1].Trim().Length < 3)
        {
            string tail = segments[^1];
            segments.RemoveAt(segments.Count - 1);
            segments[^1] += tail;
        }

        return segments;
    }

    /// <summary>
    /// True when a period really terminates a sentence rather than sitting inside a filename,
    /// a decimal, an abbreviation, or a namespaced identifier.
    /// </summary>
    private static bool EndsSentence(string text, int index)
    {
        char c = text[index];

        if (c is '!' or '?')
        {
            return true;
        }

        // Nothing after it: end of message.
        if (index + 1 >= text.Length)
        {
            return true;
        }

        // "api.ts", "config.yaml", "3.5", "Optimus.Shell" — no space means it is inside a token.
        if (!char.IsWhiteSpace(text[index + 1]))
        {
            return false;
        }

        // A single letter before the period is usually an initial, not a sentence end.
        if (index >= 1 && char.IsLetter(text[index - 1]) && (index < 2 || !char.IsLetter(text[index - 2])))
        {
            return false;
        }

        // Look at the next non-space character: a sentence normally restarts with a capital,
        // a digit, or a quote.
        for (int j = index + 1; j < text.Length; j++)
        {
            if (char.IsWhiteSpace(text[j]))
            {
                continue;
            }

            return char.IsUpper(text[j]) || char.IsDigit(text[j]) || text[j] is '"' or '\'' or '“';
        }

        return true;
    }
}
