namespace Optimus.Providers.Windows;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>The coarse kind of text visibly exposed by a coding-agent window.</summary>
public enum VisibleAgentActivity
{
    VisibleText,
    Progress,
    ToolOrSkill,
    FinalResponseCandidate
}

/// <summary>A new piece of text read from the exact window the user bound.</summary>
public sealed record VisibleAgentUpdate(
    string Text,
    VisibleAgentActivity Activity,
    DateTimeOffset ObservedAtUtc);

/// <summary>A visible UI Automation text node. Key remains stable across rerenders.</summary>
public sealed record VisibleTextNode(
    string Key,
    string Text,
    string Name = "",
    string AutomationId = "",
    string ControlType = "");

/// <summary>Reads visible text below one top-level HWND.</summary>
public interface IWindowTextSource
{
    IReadOnlyList<VisibleTextNode> Read(IntPtr hwnd);
}

/// <summary>
/// Observes only one explicitly bound Windows window and emits text added since the last poll.
/// It reports visible UI text only; it cannot access a model's private reasoning.
/// </summary>
public sealed class AgentWindowObserver
{
    private readonly WindowCandidate _window;
    private readonly IWindowTextSource _textSource;
    private readonly Func<WindowCandidate, bool> _isWindowValid;
    private readonly Dictionary<string, string> _previousTextByNode = new(StringComparer.Ordinal);
    private HashSet<string> _previousSnapshotTexts = new(StringComparer.Ordinal);

    public AgentWindowObserver(WindowCandidate window)
        : this(window, new UiaWindowTextSource(), WindowFinder.IsStillValid)
    {
    }

    public AgentWindowObserver(
        WindowCandidate window,
        IWindowTextSource textSource,
        Func<WindowCandidate, bool>? isWindowValid = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _textSource = textSource ?? throw new ArgumentNullException(nameof(textSource));
        _isWindowValid = isWindowValid ?? WindowFinder.IsStillValid;
    }

    public WindowCandidate Window => _window;

    /// <summary>Reads one snapshot. Repeated UI rerenders produce no duplicate updates.</summary>
    public IReadOnlyList<VisibleAgentUpdate> Poll()
    {
        if (!_isWindowValid(_window))
        {
            return Array.Empty<VisibleAgentUpdate>();
        }

        IReadOnlyList<VisibleTextNode> nodes = _textSource.Read(_window.Hwnd);
        var updates = new List<VisibleAgentUpdate>();
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);
        var liveTexts = new HashSet<string>(StringComparer.Ordinal);

        foreach (VisibleTextNode node in nodes)
        {
            string text = Normalize(node.Text);
            if (text.Length == 0 || !liveKeys.Add(node.Key))
            {
                continue;
            }

            liveTexts.Add(text);

            _previousTextByNode.TryGetValue(node.Key, out string? previous);
            _previousTextByNode[node.Key] = text;

            if (string.Equals(previous, text, StringComparison.Ordinal) ||
                _previousSnapshotTexts.Contains(text))
            {
                continue;
            }

            // Coding apps often rerender the same assistant node while streaming. Emit only its
            // appended suffix so narration does not repeat the response from the beginning. If
            // the app replaced the UIA element, recover from the longest previous visible prefix.
            previous ??= _previousSnapshotTexts
                .Where(oldText => text.StartsWith(oldText, StringComparison.Ordinal))
                .OrderByDescending(oldText => oldText.Length)
                .FirstOrDefault();
            string added = previous != null && text.StartsWith(previous, StringComparison.Ordinal)
                ? text[previous.Length..].Trim()
                : text;

            if (added.Length > 0)
            {
                updates.Add(new VisibleAgentUpdate(
                    added,
                    Classify(node, text),
                    DateTimeOffset.UtcNow));
            }
        }

        foreach (string staleKey in _previousTextByNode.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
        {
            _previousTextByNode.Remove(staleKey);
        }

        _previousSnapshotTexts = liveTexts;

        return updates;
    }

    /// <summary>Polls until cancellation. A closed/replaced bound window ends observation.</summary>
    public async IAsyncEnumerable<VisibleAgentUpdate> ObserveAsync(
        TimeSpan pollInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);

        while (!cancellationToken.IsCancellationRequested && _isWindowValid(_window))
        {
            foreach (VisibleAgentUpdate update in Poll())
            {
                yield return update;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static VisibleAgentActivity Classify(VisibleTextNode node, string fullText)
    {
        string metadata = $"{node.Name} {node.AutomationId} {node.ControlType}";

        if (ContainsAny(metadata, "tool", "skill", "terminal", "command", "console") ||
            ContainsAny(fullText, "running tool", "using skill", "ran command", "tool call"))
        {
            return VisibleAgentActivity.ToolOrSkill;
        }

        if (ContainsAny(fullText, "planning", "thinking", "editing", "running tests", "working", "searching"))
        {
            return VisibleAgentActivity.Progress;
        }

        // Only use final/assistant semantics explicitly exposed by the UI tree. Generic text is
        // not called final because the three desktop apps structure their trees differently.
        if (ContainsAny(metadata, "final", "assistant response", "assistant-message", "response-complete"))
        {
            return VisibleAgentActivity.FinalResponseCandidate;
        }

        return VisibleAgentActivity.VisibleText;
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string text) =>
        string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal)
                              .Replace('\r', '\n')
                              .Split('\n')
                              .Select(line => line.Trim())
                              .Where(line => line.Length > 0));
}
