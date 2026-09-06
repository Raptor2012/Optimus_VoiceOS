namespace Optimus.Providers.Ao;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Providers.Windows;

/// <summary>
/// Observes an active Agent Orchestrator session conversation for public responses, tool calls, and progress events.
/// </summary>
public sealed class AoSessionObserver : IAgentObserver
{
    private readonly IAoClient _aoClient;
    private readonly string _sessionId;
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenActivityIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenTexts = new(StringComparer.Ordinal);

    public AoSessionObserver(IAoClient aoClient, string sessionId)
    {
        _aoClient = aoClient ?? throw new ArgumentNullException(nameof(aoClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _sessionId = sessionId;
    }

    public string SessionId => _sessionId;

    public int CapturedNodeCount { get; private set; }

    public IReadOnlyList<VisibleAgentUpdate> Poll()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            AoConversationResponse? conv = _aoClient.GetSessionConversationAsync(_sessionId, cts.Token)
                .GetAwaiter().GetResult();

            if (conv == null)
            {
                return Array.Empty<VisibleAgentUpdate>();
            }

            return ProcessConversation(conv);
        }
        catch
        {
            return Array.Empty<VisibleAgentUpdate>();
        }
    }

    public async IAsyncEnumerable<VisibleAgentUpdate> ObserveAsync(
        TimeSpan pollInterval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);

        while (!cancellationToken.IsCancellationRequested)
        {
            AoConversationResponse? conv = null;
            try
            {
                conv = await _aoClient.GetSessionConversationAsync(_sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Soft retry on transient network hiccups
            }

            if (conv != null)
            {
                foreach (VisibleAgentUpdate update in ProcessConversation(conv))
                {
                    yield return update;
                }
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private List<VisibleAgentUpdate> ProcessConversation(AoConversationResponse conv)
    {
        var updates = new List<VisibleAgentUpdate>();

        // Process activities (tool calls, commands, reasoning deltas)
        if (conv.Activities != null)
        {
            for (int i = 0; i < conv.Activities.Count; i++)
            {
                AoConversationActivity act = conv.Activities[i];
                string actKey = act.Id ?? $"{act.Kind}:{i}:{act.Delta?.Length ?? 0}";
                if (!_seenActivityIds.Add(actKey))
                {
                    continue;
                }

                VisibleAgentUpdate? update = ExtractActivityUpdate(act);
                if (update != null && _seenTexts.Add(update.Text))
                {
                    updates.Add(update);
                }
            }
        }

        // Process assistant messages (final or intermediate public responses)
        if (conv.Messages != null)
        {
            foreach (AoConversationMessage msg in conv.Messages)
            {
                if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(msg.Text))
                {
                    continue;
                }

                if (!_seenMessageIds.Add(msg.Id))
                {
                    continue;
                }

                string trimmed = msg.Text.Trim();
                if (_seenTexts.Add(trimmed))
                {
                    VisibleAgentActivity activity = trimmed.EndsWith('?')
                        ? VisibleAgentActivity.VisibleText
                        : VisibleAgentActivity.FinalResponseCandidate;

                    updates.Add(new VisibleAgentUpdate(
                        trimmed,
                        activity,
                        msg.CreatedAt ?? DateTimeOffset.UtcNow));
                }
            }
        }

        CapturedNodeCount = _seenMessageIds.Count + _seenActivityIds.Count;
        return updates;
    }

    private static VisibleAgentUpdate? ExtractActivityUpdate(AoConversationActivity act)
    {
        DateTimeOffset timestamp = act.CreatedAt ?? DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(act.Delta))
        {
            string delta = act.Delta.Trim();
            if (delta.Length > 0 && delta.Length < 300)
            {
                return new VisibleAgentUpdate(delta, VisibleAgentActivity.Progress, timestamp);
            }
        }

        if (act.Detail is JsonElement elem)
        {
            if (elem.TryGetProperty("command", out JsonElement cmdElem))
            {
                string? cmd = cmdElem.GetString();
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    string shortCmd = cmd.Length > 100 ? cmd[..100] + "..." : cmd;
                    return new VisibleAgentUpdate($"Running: {shortCmd}", VisibleAgentActivity.ToolOrSkill, timestamp);
                }
            }
            if (elem.TryGetProperty("toolName", out JsonElement toolElem))
            {
                string? tool = toolElem.GetString();
                if (!string.IsNullOrWhiteSpace(tool))
                {
                    return new VisibleAgentUpdate($"Tool: {tool}", VisibleAgentActivity.ToolOrSkill, timestamp);
                }
            }
            if (elem.TryGetProperty("text", out JsonElement textElem))
            {
                string? text = textElem.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return new VisibleAgentUpdate(text.Trim(), VisibleAgentActivity.Progress, timestamp);
                }
            }
            if (elem.TryGetProperty("event", out JsonElement evElem) &&
                string.Equals(evElem.GetString(), "plan", StringComparison.OrdinalIgnoreCase) &&
                elem.TryGetProperty("steps", out JsonElement stepsElem) &&
                stepsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement step in stepsElem.EnumerateArray())
                {
                    if (step.TryGetProperty("status", out JsonElement s) &&
                        string.Equals(s.GetString(), "in_progress", StringComparison.OrdinalIgnoreCase) &&
                        step.TryGetProperty("text", out JsonElement t))
                    {
                        string? stepText = t.GetString();
                        if (!string.IsNullOrWhiteSpace(stepText))
                        {
                            return new VisibleAgentUpdate($"Plan: {stepText}", VisibleAgentActivity.Progress, timestamp);
                        }
                    }
                }
            }
        }

        return null;
    }
}
