namespace Optimus.Core.Memory;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Inference;

/// <summary>
/// Periodically (every 4 hours by default or on demand) invokes local Gemma to summarize
/// recent conversation history into a concise digest and saves it in the summaries table.
/// </summary>
public sealed class SummaryGenerator : IDisposable
{
    private readonly MemoryStore _memoryStore;
    private readonly LlamaServerProcess? _llamaServer;
    private readonly Func<string, CancellationToken, Task<string>>? _summarizeDelegate;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _timerTask;
    private bool _disposed;

    /// <summary>
    /// Gets the interval between periodic summary runs (default 4 hours).
    /// </summary>
    public TimeSpan Interval => _interval;

    /// <summary>
    /// Event fired whenever a new summary is successfully generated and stored.
    /// </summary>
    public event Action<SummaryRecord>? SummaryGenerated;

    /// <summary>
    /// Initializes a new instance of the <see cref="SummaryGenerator"/> class using <see cref="LlamaServerProcess"/>.
    /// </summary>
    /// <param name="memoryStore">Persistent memory store.</param>
    /// <param name="llamaServer">Resident Gemma llama-server process.</param>
    /// <param name="interval">Periodic execution interval (default 4 hours).</param>
    /// <param name="autoStart">Whether to immediately start the periodic background timer.</param>
    public SummaryGenerator(
        MemoryStore memoryStore,
        LlamaServerProcess? llamaServer = null,
        TimeSpan? interval = null,
        bool autoStart = false)
    {
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _llamaServer = llamaServer;
        _interval = interval ?? TimeSpan.FromHours(4);

        if (autoStart)
        {
            Start();
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SummaryGenerator"/> class with a custom summarizer delegate.
    /// </summary>
    public SummaryGenerator(
        MemoryStore memoryStore,
        Func<string, CancellationToken, Task<string>> summarizeDelegate,
        TimeSpan? interval = null,
        bool autoStart = false)
    {
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _summarizeDelegate = summarizeDelegate ?? throw new ArgumentNullException(nameof(summarizeDelegate));
        _interval = interval ?? TimeSpan.FromHours(4);

        if (autoStart)
        {
            Start();
        }
    }

    /// <summary>
    /// Starts the background timer loop.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_timerTask != null || _disposed) return;
            _timerTask = Task.Run(PeriodicRunLoopAsync);
        }
    }

    private async Task PeriodicRunLoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                await GenerateSummaryAsync(cancellationToken: _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Generates a summary for the given period (or since the last summary) on demand.
    /// </summary>
    /// <param name="periodStart">Start of period. If null, uses the end of the last summary or 4 hours ago.</param>
    /// <param name="periodEnd">End of period. If null, uses current UTC time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SummaryRecord?> GenerateSummaryAsync(
        DateTimeOffset? periodStart = null,
        DateTimeOffset? periodEnd = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset end = periodEnd ?? DateTimeOffset.UtcNow;
            DateTimeOffset start = periodStart ?? ResolveDefaultStartTime(end);

            IReadOnlyList<ConversationTurnRecord> turns = _memoryStore.GetTurnsBetween(start, end);
            if (turns.Count == 0)
            {
                return null;
            }

            string formattedHistory = FormatTurns(turns);
            string summaryText = await SummarizeWithGemmaAsync(formattedHistory, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(summaryText))
            {
                return null;
            }

            long id = _memoryStore.SaveSummary(start, end, summaryText.Trim());
            var record = new SummaryRecord(id, start, end, summaryText.Trim(), DateTimeOffset.UtcNow);

            SummaryGenerated?.Invoke(record);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    private DateTimeOffset ResolveDefaultStartTime(DateTimeOffset currentEnd)
    {
        IReadOnlyList<SummaryRecord> previous = _memoryStore.GetSummaries(1);
        if (previous.Count > 0 && previous[0].PeriodEnd < currentEnd)
        {
            return previous[0].PeriodEnd;
        }

        return currentEnd - _interval;
    }

    private async Task<string> SummarizeWithGemmaAsync(string formattedTurns, CancellationToken cancellationToken)
    {
        if (_summarizeDelegate != null)
        {
            return await _summarizeDelegate(formattedTurns, cancellationToken).ConfigureAwait(false);
        }

        if (_llamaServer != null && _llamaServer.IsRunning)
        {
            var messages = new List<LlamaServerProcess.ChatMessage>
            {
                new("system", "You are the local conversation summarizer for Optimus Voice OS. Provide a concise 1-3 sentence summary of the following recent conversation history, focusing on key goals, actions taken, and outcomes. Output only the summary text without preamble."),
                new("user", $"Conversation History:\n{formattedTurns}")
            };

            return await _llamaServer.ChatAsync(messages, 256, cancellationToken).ConfigureAwait(false);
        }

        // Fallback heuristic if local Gemma server is not active
        return GenerateFallbackSummary(formattedTurns);
    }

    private static string FormatTurns(IReadOnlyList<ConversationTurnRecord> turns)
    {
        var sb = new StringBuilder();
        foreach (ConversationTurnRecord turn in turns)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"[Turn {turn.TurnId}] Time: {turn.Timestamp:yyyy-MM-dd HH:mm:ss} Device: {turn.Device}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"User: {turn.Transcript}");
            if (!string.IsNullOrWhiteSpace(turn.Objective))
                sb.AppendLine(CultureInfo.InvariantCulture, $"Objective: {turn.Objective}");
            if (!string.IsNullOrWhiteSpace(turn.Response))
                sb.AppendLine(CultureInfo.InvariantCulture, $"Response: {turn.Response}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static string GenerateFallbackSummary(string formattedTurns)
    {
        string[] lines = formattedTurns.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int userLines = lines.Count(l => l.StartsWith("User:", StringComparison.OrdinalIgnoreCase));
        return $"Session activity digest: Completed {userLines} conversation turns.";
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();
        _gate.Dispose();
    }
}
