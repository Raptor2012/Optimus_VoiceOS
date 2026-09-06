namespace Optimus.Core.Monitoring;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Memory;
using Optimus.Providers.Desktop;
using Optimus.Providers.Windows;

/// <summary>
/// Configuration options for <see cref="ResponseMonitor"/>.
/// </summary>
public sealed class ResponseMonitorOptions
{
    /// <summary>Polling interval between checks for new text.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Duration the observed text must remain stable to declare completion.</summary>
    public TimeSpan StabilityDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum overall time to wait for a response.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Watches provider responses in desktop application windows after tool execution.
/// Polls <see cref="IDesktopObserver"/> for visible text changes, detects when text has remained
/// stable for 2 seconds (response finished), updates the response in <see cref="MemoryStore"/>,
/// and triggers <see cref="NarrationScheduler"/> if the user preference "narrate_responses" is true.
/// </summary>
public sealed class ResponseMonitor
{
    private readonly IDesktopObserver _observer;
    private readonly MemoryStore _memoryStore;
    private readonly NarrationScheduler? _narrationScheduler;
    private readonly ResponseMonitorOptions _options;

    /// <summary>
    /// Event fired when a provider response has completed and been stored.
    /// Parameters are (turnId, responseText).
    /// </summary>
    public event Action<string, string>? ResponseCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResponseMonitor"/> class.
    /// </summary>
    public ResponseMonitor(
        IDesktopObserver observer,
        MemoryStore memoryStore,
        NarrationScheduler? narrationScheduler = null,
        ResponseMonitorOptions? options = null)
    {
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _narrationScheduler = narrationScheduler;
        _options = options ?? new ResponseMonitorOptions();
    }

    /// <summary>
    /// Captures the current visible text baseline before a message is sent.
    /// </summary>
    public string CaptureBaseline(IntPtr hwnd)
    {
        IReadOnlyList<VisibleTextNode> nodes = _observer.ReadContent(hwnd);
        return ExtractText(nodes);
    }

    /// <summary>
    /// Convenience method invoked after SendMessage: establishes baseline (if not provided)
    /// and monitors the window until response finishes.
    /// </summary>
    public Task<string?> AfterSendMessageAsync(
        IntPtr hwnd,
        string turnId,
        string? baselineText = null,
        CancellationToken cancellationToken = default) =>
        MonitorResponseAsync(hwnd, turnId, baselineText, cancellationToken);

    /// <summary>
    /// Polls the window for text changes, detects completion when text is stable for the configured duration,
    /// updates conversation_history, and optionally triggers narration.
    /// </summary>
    public async Task<string?> MonitorResponseAsync(
        IntPtr hwnd,
        string turnId,
        string? baselineText = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        string baseline = baselineText ?? CaptureBaseline(hwnd);
        string lastPolledText = baseline;
        DateTimeOffset lastChangeTime = DateTimeOffset.UtcNow;
        bool hasStartedResponding = false;

        var timeoutCts = new CancellationTokenSource(_options.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                await Task.Delay(_options.PollInterval, linkedCts.Token).ConfigureAwait(false);

                IReadOnlyList<VisibleTextNode> nodes = _observer.ReadContent(hwnd);
                string currentText = ExtractText(nodes);

                if (!string.Equals(currentText, lastPolledText, StringComparison.Ordinal))
                {
                    // Text changed
                    lastPolledText = currentText;
                    lastChangeTime = DateTimeOffset.UtcNow;
                    if (!hasStartedResponding && !string.Equals(currentText, baseline, StringComparison.Ordinal))
                    {
                        hasStartedResponding = true;
                    }
                }
                else if (hasStartedResponding)
                {
                    // Text has not changed since last poll; check stability duration
                    TimeSpan elapsedStable = DateTimeOffset.UtcNow - lastChangeTime;
                    if (elapsedStable >= _options.StabilityDuration)
                    {
                        // Response complete!
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            Trace.TraceWarning($"[ResponseMonitor] Monitoring timed out after {_options.Timeout.TotalSeconds}s.");
        }

        string responseText = ExtractNewText(baseline, lastPolledText);
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            // 1. Store in conversation_history
            _memoryStore.UpdateResponse(turnId, responseText);

            // 2. Trigger narration scheduling if narrate_responses = true
            string? pref = _memoryStore.GetPreference("narrate_responses");
            if (string.Equals(pref, "true", StringComparison.OrdinalIgnoreCase))
            {
                _narrationScheduler?.QueueNarration(responseText, turnId);
            }

            ResponseCompleted?.Invoke(turnId, responseText);
            return responseText;
        }

        return null;
    }

    /// <summary>
    /// Combines visible text nodes into a normalized multi-line string.
    /// </summary>
    public static string ExtractText(IReadOnlyList<VisibleTextNode> nodes)
    {
        if (nodes == null || nodes.Count == 0) return string.Empty;
        return string.Join("\n", nodes
            .Select(n => n.Text?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    /// <summary>
    /// Extracts text added after the baseline snapshot.
    /// </summary>
    public static string ExtractNewText(string baseline, string current)
    {
        if (string.IsNullOrWhiteSpace(baseline)) return current.Trim();
        if (string.IsNullOrWhiteSpace(current)) return string.Empty;

        if (current.StartsWith(baseline, StringComparison.Ordinal))
        {
            return current[baseline.Length..].Trim();
        }

        // Line-based diff fallback if formatting or timestamps shifted
        string[] baselineLines = baseline.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] currentLines = current.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (currentLines.Length > baselineLines.Length)
        {
            return string.Join("\n", currentLines.Skip(baselineLines.Length)).Trim();
        }

        // If prefix matching failed, return whatever difference exists
        return current.Trim();
    }
}
