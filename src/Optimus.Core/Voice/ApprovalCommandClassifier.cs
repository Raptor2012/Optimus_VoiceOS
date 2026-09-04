using System.Text;

namespace Optimus.Core.Voice;

public enum ApprovalCommand
{
    Unknown,
    Affirmative,
    Redictate,
    Cancel,
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

    public static ApprovalCommand Classify(string? transcript)
    {
        string normalized = Normalize(transcript);

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

        return ApprovalCommand.Unknown;
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
