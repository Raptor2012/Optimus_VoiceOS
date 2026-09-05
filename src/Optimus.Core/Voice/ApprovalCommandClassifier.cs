using System.Text;

namespace Optimus.Core.Voice;

public enum ApprovalCommand
{
    Unknown,
    Affirmative,
    Redictate,
    Cancel,
    UseOriginal,
}

/// <summary>
/// Classifies the deliberately small spoken-approval vocabulary without invoking a language model.
/// </summary>
public static class ApprovalCommandClassifier
{
    private static readonly HashSet<string> AffirmativeCommands = new(StringComparer.Ordinal)
    {
        "yes",
        "yeah",
        "yep",
        "confirm",
        "send",
        "send it",
        "go ahead",
        "do it",
    };

    private static readonly HashSet<string> RedictateCommands = new(StringComparer.Ordinal)
    {
        "redictate",
        "try again",
        "start over",
        "redo",
        "retry",
        "change that",
    };

    private static readonly HashSet<string> CancelCommands = new(StringComparer.Ordinal)
    {
        "cancel",
        "no",
        "stop",
        "abort",
        "scratch that",
        "never mind",
    };

    private static readonly HashSet<string> UseOriginalCommands = new(StringComparer.Ordinal)
    {
        "use original",
        "original",
        "use raw",
        "raw",
        "keep original",
        "revert to original",
        "revert",
    };

    /// <summary>Words that make an utterance a refusal, whatever else it contains.</summary>
    /// <remarks>
    /// Checked before anything else. "Do not send that" contains "send", and must never be
    /// allowed to approve a send on the strength of it.
    /// </remarks>
    /// <remarks>
    /// "don" appears because normalisation splits "don't" into "don" and "t"; matching only
    /// "dont" let "don't send that" approve a send.
    /// </remarks>
    private static readonly string[] RefusalWords = ["dont", "don", "not", "never", "wait", "hold"];

    private static readonly string[] CancelWords = ["cancel", "no", "stop", "abort", "scratch", "nevermind"];

    private static readonly string[] RedictateWords = ["redictate", "redo", "retry", "again", "over", "change"];

    private static readonly string[] UseOriginalWords = ["original", "raw", "revert"];

    /// <remarks>
    /// "sent" is included because the recogniser frequently returns it for a clipped "send", and
    /// no refusal contains it.
    /// </remarks>
    private static readonly string[] AffirmativeWords =
        ["yes", "yeah", "yep", "yup", "ok", "okay", "send", "sent", "confirm", "approve", "submit", "proceed"];

    /// <summary>Words that may precede the real reply without changing it.</summary>
    private static readonly string[] FillerWords =
        ["yes", "yeah", "yep", "yup", "ok", "okay", "sure", "alright", "please", "just", "now", "then", "and", "uh", "um", "so"];

    /// <summary>Fillers that are themselves an approval when nothing else follows.</summary>
    private static readonly string[] BareAffirmatives = ["yes", "yeah", "yep", "yup", "ok", "okay", "sure"];

    /// <summary>
    /// Classifies a spoken reply to the review prompt.
    /// </summary>
    /// <remarks>
    /// An exact phrase is matched first. Otherwise the reply is read as words, because people
    /// answer "Send this to Claude, or redictate?" by echoing the question rather than by
    /// reciting a keyword: "send this to Claude" and "yeah send it" both mean the same thing and
    /// neither is an exact phrase. A reply containing a refusal can never approve; the worst
    /// case is that it goes unrecognised and the question is asked again.
    /// </remarks>
    public static ApprovalCommand Classify(string? transcript)
    {
        string normalized = Normalize(transcript);

        if (normalized.Length == 0)
        {
            return ApprovalCommand.Unknown;
        }

        if (AffirmativeCommands.Contains(normalized))
        {
            return ApprovalCommand.Affirmative;
        }

        if (RedictateCommands.Contains(normalized))
        {
            return ApprovalCommand.Redictate;
        }

        if (CancelCommands.Contains(normalized))
        {
            return ApprovalCommand.Cancel;
        }

        if (UseOriginalCommands.Contains(normalized))
        {
            return ApprovalCommand.UseOriginal;
        }

        string[] words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Anything withholding approval settles the reply immediately. It may still cancel, but
        // it can never approve, whatever approval words it happens to contain.
        if (ContainsAny(words, RefusalWords))
        {
            return ContainsAny(words, CancelWords) ? ApprovalCommand.Cancel : ApprovalCommand.Unknown;
        }

        // Declining, redictating and reverting are all recoverable, so a matching word anywhere
        // in the reply is enough for them.
        if (ContainsAny(words, CancelWords))
        {
            return ApprovalCommand.Cancel;
        }

        if (ContainsAny(words, RedictateWords))
        {
            return ApprovalCommand.Redictate;
        }

        if (ContainsAny(words, UseOriginalWords))
        {
            return ApprovalCommand.UseOriginal;
        }

        // Approving is not recoverable, so it is not enough for an approval word to appear
        // somewhere: the reply has to lead with it. "Send this to Claude" and "yeah send it" do.
        // "Add a send button to the page" does not, and stays unrecognised.
        int head = 0;
        while (head < words.Length && Contains(FillerWords, words[head]))
        {
            head++;
        }

        if (head == words.Length)
        {
            return ContainsAny(words, BareAffirmatives) ? ApprovalCommand.Affirmative : ApprovalCommand.Unknown;
        }

        return Contains(AffirmativeWords, words[head]) ? ApprovalCommand.Affirmative : ApprovalCommand.Unknown;
    }

    private static bool ContainsAny(string[] words, string[] vocabulary)
    {
        foreach (string word in words)
        {
            if (Contains(vocabulary, word))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(string[] vocabulary, string word)
    {
        foreach (string candidate in vocabulary)
        {
            if (string.Equals(word, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string? transcript)
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
