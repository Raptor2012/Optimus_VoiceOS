namespace Optimus.Core.Narration;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Utility for detecting and collapsing repetitive log outputs and long code blocks
/// into concise typed event markers suitable for speech synthesis.
/// </summary>
public static partial class CodeAndLogCollapser
{
    private static readonly string[] LogPrefixes =
    [
        "info:",
        "debug:",
        "trace:",
        "warn:",
        "warning:",
        "error:",
        "[info]",
        "[debug]",
        "[trace]",
        "[warn]",
        "[warning]",
        "[error]",
        "-->",
        "==>",
        "[+]",
        "[-]",
        "[*]"
    ];

    /// <summary>
    /// Collapses fenced code blocks and repetitive log lines in the input text.
    /// </summary>
    /// <param name="input">The raw text to collapse.</param>
    /// <returns>Text with code blocks and repetitive logs replaced by typed markers.</returns>
    public static string Collapse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        // 1. Collapse fenced code blocks
        string codeCollapsed = CollapseFencedCodeBlocks(input);

        // 2. Collapse repetitive lines and log runs
        return CollapseRepetitiveLogs(codeCollapsed);
    }

    private static string CollapseFencedCodeBlocks(string text)
    {
        var lines = SplitLines(text);
        var result = new List<string>(lines.Count);
        int i = 0;

        while (i < lines.Count)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                string fenceType = trimmed[..3];
                string language = trimmed[3..].Trim();
                int startIndex = i + 1;
                int endIndex = -1;

                for (int j = startIndex; j < lines.Count; j++)
                {
                    if (lines[j].Trim().StartsWith(fenceType, StringComparison.Ordinal))
                    {
                        endIndex = j;
                        break;
                    }
                }

                if (endIndex != -1)
                {
                    int lineCount = endIndex - startIndex;
                    string marker = FormatCodeMarker(lineCount, language);
                    result.Add(marker);
                    i = endIndex + 1;
                    continue;
                }
                else
                {
                    // Unclosed code fence at end of stream
                    int lineCount = lines.Count - startIndex;
                    string marker = FormatCodeMarker(lineCount, language);
                    result.Add(marker);
                    break;
                }
            }

            result.Add(line);
            i++;
        }

        return string.Join("\n", result);
    }

    private static string FormatCodeMarker(int lineCount, string language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return lineCount switch
            {
                0 => "[Empty code block]",
                1 => "[Code block: 1 line]",
                _ => $"[Code block: {lineCount} lines]"
            };
        }

        return lineCount switch
        {
            0 => $"[Empty code block: {language}]",
            1 => $"[Code block: 1 line of {language}]",
            _ => $"[Code block: {lineCount} lines of {language}]"
        };
    }

    private static string CollapseRepetitiveLogs(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count == 0)
        {
            return text;
        }

        var result = new List<string>(lines.Count);
        int i = 0;

        while (i < lines.Count)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Check A: Repeated identical lines (3 or more)
            int repeatCount = 1;
            while (i + repeatCount < lines.Count && string.Equals(lines[i + repeatCount].Trim(), trimmed, StringComparison.Ordinal))
            {
                repeatCount++;
            }

            if (repeatCount >= 3)
            {
                string shortLine = trimmed.Length > 40 ? string.Concat(trimmed.AsSpan(0, 37), "...") : trimmed;
                result.Add($"[Repeated: '{shortLine}' ({repeatCount} times)]");
                i += repeatCount;
                continue;
            }

            // Check B: Run of log / build lines (3 or more consecutive log lines)
            if (IsLogLine(trimmed))
            {
                int logRun = 1;
                bool isStackTrace = IsStackTraceLine(trimmed);

                while (i + logRun < lines.Count && IsLogLine(lines[i + logRun].Trim()))
                {
                    if (IsStackTraceLine(lines[i + logRun].Trim()))
                    {
                        isStackTrace = true;
                    }
                    logRun++;
                }

                if (logRun >= 3)
                {
                    string marker = isStackTrace
                        ? $"[Stack trace: {logRun} lines]"
                        : $"[Log output: {logRun} lines]";
                    result.Add(marker);
                    i += logRun;
                    continue;
                }
            }

            result.Add(line);
            i++;
        }

        return string.Join("\n", result);
    }

    private static bool IsLogLine(string trimmed)
    {
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (IsStackTraceLine(trimmed))
        {
            return true;
        }

        // Check common timestamp patterns like [12:34:56] or 2026-09-04
        if (trimmed.StartsWith('[') && trimmed.Contains(']'))
        {
            return true;
        }

        foreach (string prefix in LogPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStackTraceLine(string trimmed)
    {
        return trimmed.StartsWith("at ", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                lines.Add(text[start..i]);
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                start = i + 1;
            }
            else if (text[i] == '\n')
            {
                lines.Add(text[start..i]);
                start = i + 1;
            }
        }

        if (start <= text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }
}
