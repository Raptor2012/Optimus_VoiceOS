namespace Optimus.Core.Digests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Inference;
using Optimus.Providers.Ao;

/// <summary>
/// Configuration options for <see cref="DigestScheduler"/>.
/// </summary>
public sealed class DigestSchedulerOptions
{
    /// <summary>Window over which routine events are accumulated before summarization.</summary>
    public TimeSpan RoutineWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum time between unsolicited spoken digests.</summary>
    public TimeSpan UnsolicitedSpokenCooldown { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum facts retained per project group.</summary>
    public int MaxFactsPerGroup { get; set; } = 25;
}

/// <summary>
/// Schedules and coordinates local intelligence updates from Agent Orchestrator events.
/// Implements 30s routine event batching, 2-minute unsolicited rate limiting, decision immediacy,
/// gating against interrupting speech/approval, and conversational pause delivery.
/// </summary>
public sealed class DigestScheduler : IDisposable
{
    private readonly IAoClient? _aoClient;
    private readonly IDigestSummarizer _summarizer;
    private readonly DigestSchedulerOptions _options;
    private readonly object _gate = new();

    private readonly Dictionary<string, List<DigestFact>> _pendingFactsByProject = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _projectNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastStatusBySession = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DigestEvent> _digestHistory = new();

    private readonly HashSet<string> _seenCdcEventSeqs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenMessageIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenActivityIds = new(StringComparer.Ordinal);

    private DateTimeOffset _lastUnsolicitedSpokenTime = DateTimeOffset.MinValue;
    private DigestEvent? _pendingSpokenDigest;
    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private bool _isUserSpeaking;
    private bool _disposed;

    public Func<bool>? IsSpeaking { get; set; }
    public Func<bool>? IsAwaitingApproval { get; set; }
    public Func<bool>? IsUserSpeaking { get; set; }

    public Action<DigestEvent>? OnDigestProduced { get; set; }
    public Action<DigestEvent>? OnSpeakDigest { get; set; }
    public Action<string>? OnOpenOriginalResponse { get; set; }
    public Action? OnCancelSpeaking { get; set; }

    public DigestSchedulerOptions Options => _options;

    public IReadOnlyList<DigestEvent> History
    {
        get
        {
            lock (_gate)
            {
                return _digestHistory.ToArray();
            }
        }
    }

    public DigestEvent? PendingSpokenDigest
    {
        get
        {
            lock (_gate)
            {
                return _pendingSpokenDigest;
            }
        }
    }

    public DigestScheduler(
        IDigestSummarizer summarizer,
        IAoClient? aoClient = null,
        DigestSchedulerOptions? options = null)
    {
        _summarizer = summarizer ?? throw new ArgumentNullException(nameof(summarizer));
        _aoClient = aoClient;
        _options = options ?? new DigestSchedulerOptions();
    }

    /// <summary>
    /// Starts background polling or streaming from AoClient and the routine 30s flush timer.
    /// </summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_workerTask != null) return;
            _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _workerTask = Task.Run(() => WorkerLoopAsync(_workerCts.Token), _workerCts.Token);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _workerCts;
            _workerCts = null;
            _workerTask = null;
        }
        cts?.Cancel();
        cts?.Dispose();
    }

    #region Ingestion & Filtering

    /// <summary>
    /// Ingests a structured fact. Filters out low-level tool chatter and repeated statuses.
    /// Decisions trigger immediate summarization.
    /// </summary>
    public void IngestFact(DigestFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        if (IsToolChatter(fact.Content, fact.EventType))
        {
            return;
        }

        if (string.Equals(fact.EventType, "status", StringComparison.OrdinalIgnoreCase))
        {
            string targetKey = fact.SessionOrTaskId ?? fact.ProjectId ?? "default";
            if (IsRepeatedStatus(targetKey, fact.Content))
            {
                return;
            }
        }

        string projectId = !string.IsNullOrWhiteSpace(fact.ProjectId) ? fact.ProjectId : "default";

        lock (_gate)
        {
            if (!_pendingFactsByProject.TryGetValue(projectId, out var list))
            {
                list = new List<DigestFact>();
                _pendingFactsByProject[projectId] = list;
            }

            list.Add(fact);
            if (list.Count > _options.MaxFactsPerGroup)
            {
                list.RemoveAt(0);
            }
        }

        if (fact.IsDecision)
        {
            // Requirement: "Show decisions immediately, speak at next conversational pause."
            _ = Task.Run(() => FlushProjectAsync(projectId));
        }
    }

    /// <summary>
    /// Ingests a raw CDC event from AoClient.
    /// </summary>
    public void IngestCdcEvent(AoCdcEvent cdcEvent)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);

        string seqKey = $"{cdcEvent.Seq}:{cdcEvent.Type}:{cdcEvent.SessionId}";
        lock (_gate)
        {
            if (!_seenCdcEventSeqs.Add(seqKey))
            {
                return;
            }
        }

        string content = string.Empty;
        bool isDecision = false;
        bool isMeaningful = false;
        IReadOnlyList<string>? options = null;

        if (!string.IsNullOrWhiteSpace(cdcEvent.PayloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(cdcEvent.PayloadJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("message", out var msgElem))
                {
                    content = msgElem.GetString() ?? string.Empty;
                }
                else if (root.TryGetProperty("text", out var txtElem))
                {
                    content = txtElem.GetString() ?? string.Empty;
                }
                else if (root.TryGetProperty("delta", out var deltaElem))
                {
                    content = deltaElem.GetString() ?? string.Empty;
                }
                else if (root.TryGetProperty("status", out var statusElem))
                {
                    content = statusElem.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("requiresDecision", out var dElem) && dElem.GetBoolean())
                {
                    isDecision = true;
                    isMeaningful = true;
                }

                if (root.TryGetProperty("options", out var optElem) && optElem.ValueKind == JsonValueKind.Array)
                {
                    var optList = new List<string>();
                    foreach (var o in optElem.EnumerateArray())
                    {
                        if (o.GetString() is { } s) optList.Add(s);
                    }
                    options = optList;
                }
            }
            catch
            {
                content = cdcEvent.PayloadJson;
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            content = cdcEvent.Type;
        }

        if (string.Equals(cdcEvent.Type, "approval_requested", StringComparison.OrdinalIgnoreCase))
        {
            isDecision = true;
            isMeaningful = true;
        }
        else if (content.Contains("tests passed", StringComparison.OrdinalIgnoreCase) ||
                 content.Contains("build failed", StringComparison.OrdinalIgnoreCase) ||
                 content.Contains("merged", StringComparison.OrdinalIgnoreCase))
        {
            isMeaningful = true;
        }

        var fact = new DigestFact(
            EventId: $"cdc-{cdcEvent.Seq}",
            ProjectId: cdcEvent.ProjectId,
            SessionOrTaskId: cdcEvent.SessionId,
            EventType: cdcEvent.Type,
            Content: content,
            Timestamp: cdcEvent.CreatedAt,
            IsDecision: isDecision,
            IsMeaningful: isMeaningful,
            Options: options,
            RawPayload: cdcEvent.PayloadJson
        );

        IngestFact(fact);
    }

    /// <summary>
    /// Ingests conversation turns, messages, and activities from a session.
    /// </summary>
    public void IngestConversation(
        string sessionId,
        AoConversationResponse conv,
        string? projectId = null,
        string? projectName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(conv);

        if (!string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(projectName))
        {
            lock (_gate)
            {
                _projectNames[projectId] = projectName;
            }
        }

        // Process activities
        if (conv.Activities != null)
        {
            for (int i = 0; i < conv.Activities.Count; i++)
            {
                var act = conv.Activities[i];
                string actKey = act.Id ?? $"{sessionId}:act:{i}";
                lock (_gate)
                {
                    if (!_seenActivityIds.Add(actKey)) continue;
                }

                string? delta = act.Delta?.Trim();
                if (!string.IsNullOrWhiteSpace(delta) && !IsToolChatter(delta, act.Kind))
                {
                    bool isDecision = string.Equals(act.Kind, "approval_requested", StringComparison.OrdinalIgnoreCase);
                    bool isMeaningful = isDecision ||
                                        delta.Contains("tests passed", StringComparison.OrdinalIgnoreCase) ||
                                        delta.Contains("build failed", StringComparison.OrdinalIgnoreCase);

                    IngestFact(new DigestFact(
                        EventId: actKey,
                        ProjectId: projectId,
                        SessionOrTaskId: sessionId,
                        EventType: act.Kind ?? "activity",
                        Content: delta,
                        Timestamp: act.CreatedAt ?? DateTimeOffset.UtcNow,
                        IsDecision: isDecision,
                        IsMeaningful: isMeaningful
                    ));
                }
            }
        }

        // Process assistant messages
        if (conv.Messages != null)
        {
            foreach (var msg in conv.Messages)
            {
                if (!string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(msg.Text))
                {
                    continue;
                }

                lock (_gate)
                {
                    if (!_seenMessageIds.Add(msg.Id)) continue;
                }

                string text = msg.Text.Trim();
                bool isQuestion = text.EndsWith('?');
                bool isMeaningful = true;

                IngestFact(new DigestFact(
                    EventId: msg.Id,
                    ProjectId: projectId,
                    SessionOrTaskId: sessionId,
                    EventType: isQuestion ? "question" : "response",
                    Content: text,
                    Timestamp: msg.CreatedAt ?? DateTimeOffset.UtcNow,
                    IsDecision: isQuestion,
                    IsMeaningful: isMeaningful
                ));
            }
        }
    }

    public static bool IsToolChatter(string? text, string? kind)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (string.Equals(kind, "tool_call", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(kind, "tool", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("view_file", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("grep_search", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("find_by_name", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("list_dir", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("read_url_content", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("git status", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("manage_task", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("Tool: view_file", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Tool: grep_search", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Tool: find_by_name", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Tool: list_dir", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Tool: read_url_content", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Tool: manage_task", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Running: git status", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Running: dir", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public bool IsRepeatedStatus(string sessionOrTask, string status)
    {
        if (string.IsNullOrWhiteSpace(sessionOrTask) || string.IsNullOrWhiteSpace(status))
            return false;

        string trimmed = status.Trim();
        lock (_gate)
        {
            if (_lastStatusBySession.TryGetValue(sessionOrTask, out string? last) &&
                string.Equals(last, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _lastStatusBySession[sessionOrTask] = trimmed;
            return false;
        }
    }

    #endregion

    #region Summarization & Scheduling

    /// <summary>
    /// Flushes accumulated routine events across all projects.
    /// </summary>
    public async Task FlushAllAsync(CancellationToken cancellationToken = default)
    {
        List<string> projectIds;
        lock (_gate)
        {
            projectIds = _pendingFactsByProject.Keys.ToList();
        }

        foreach (string projId in projectIds)
        {
            await FlushProjectAsync(projId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Flushes facts for one project group through Gemma summarization.
    /// </summary>
    public async Task<DigestEvent?> FlushProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        List<DigestFact> facts;
        string? projectName;

        lock (_gate)
        {
            if (!_pendingFactsByProject.TryGetValue(projectId, out var list) || list.Count == 0)
            {
                return null;
            }

            facts = new List<DigestFact>(list);
            list.Clear();
            _projectNames.TryGetValue(projectId, out projectName);
        }

        string? taskRef = facts.FindLast(f => !string.IsNullOrWhiteSpace(f.SessionOrTaskId))?.SessionOrTaskId;
        DigestEvent digest = await _summarizer.SummarizeAsync(projectId, projectName, taskRef, facts, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _digestHistory.Add(digest);
        }

        // Requirement: "Show decisions immediately, speak at next conversational pause."
        OnDigestProduced?.Invoke(digest);

        ScheduleSpokenDigest(digest);
        return digest;
    }

    private void ScheduleSpokenDigest(DigestEvent digest)
    {
        // Requirement: "speak only meaningful results and decisions"
        if (!digest.IsMeaningful && !digest.RequiresDecision)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Requirement: "limit unsolicited digests to 1 per 2 min"
        // Decisions are urgent alerts and bypass the unsolicited cooldown; routine/meaningful results adhere to it.
        if (!digest.RequiresDecision && (now - _lastUnsolicitedSpokenTime) < _options.UnsolicitedSpokenCooldown)
        {
            return;
        }

        lock (_gate)
        {
            if (CanSpeakNow_NoLock())
            {
                _pendingSpokenDigest = null;
                _lastUnsolicitedSpokenTime = now;
                OnSpeakDigest?.Invoke(digest);
            }
            else
            {
                // Postpone until the next conversational pause
                _pendingSpokenDigest = digest;
            }
        }
    }

    private bool CanSpeakNow_NoLock()
    {
        // Requirement: "never interrupt speech or approval with background updates"
        if (IsSpeaking?.Invoke() == true) return false;
        if (IsAwaitingApproval?.Invoke() == true) return false;
        if (IsUserSpeaking?.Invoke() == true || _isUserSpeaking) return false;
        return true;
    }

    /// <summary>
    /// User started speaking: cancel or postpone active digest.
    /// </summary>
    public void NotifyUserSpeechStarted()
    {
        lock (_gate)
        {
            _isUserSpeaking = true;
        }

        // Requirement: "Cancel/postpone digest when user speaks"
        OnCancelSpeaking?.Invoke();
    }

    /// <summary>
    /// User finished speaking.
    /// </summary>
    public void NotifyUserSpeechEnded()
    {
        lock (_gate)
        {
            _isUserSpeaking = false;
        }

        NotifyConversationalPause();
    }

    /// <summary>
    /// Notifies that a conversational pause has occurred (TTS finished, approval completed, or speech ended).
    /// Speaks any postponed digest.
    /// </summary>
    public void NotifyConversationalPause()
    {
        DigestEvent? toSpeak = null;
        lock (_gate)
        {
            if (_pendingSpokenDigest != null && CanSpeakNow_NoLock())
            {
                toSpeak = _pendingSpokenDigest;
                _pendingSpokenDigest = null;
                _lastUnsolicitedSpokenTime = DateTimeOffset.UtcNow;
            }
        }

        if (toSpeak != null)
        {
            OnSpeakDigest?.Invoke(toSpeak);
        }
    }

    /// <summary>
    /// "Give me the details" opens the original response from the latest digest.
    /// </summary>
    public string? OpenDetails()
    {
        DigestEvent? latest;
        lock (_gate)
        {
            latest = _digestHistory.Count > 0 ? _digestHistory[^1] : null;
        }

        if (latest == null) return null;

        string details = latest.OriginalResponse ??
                         latest.DetailedSummary ??
                         latest.SpokenSummary;

        OnOpenOriginalResponse?.Invoke(details);
        return details;
    }

    #endregion

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Combine routine events over 30s window
                await Task.Delay(_options.RoutineWindow, cancellationToken).ConfigureAwait(false);
                await FlushAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Soft retry
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _summarizer.Dispose();
    }
}
