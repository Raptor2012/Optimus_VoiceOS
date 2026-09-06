using System.Text.RegularExpressions;

namespace Optimus.Core.Conversation;

public sealed record BrowsingContext
{
    public BrowsingContext(string? foregroundApp = null, string? location = null, DateTimeOffset? updatedAtUtc = null)
    {
        ForegroundApp = foregroundApp;
        Location = location;
        UpdatedAtUtc = updatedAtUtc ?? DateTimeOffset.UtcNow;
    }

    public string? ForegroundApp { get; init; }
    public string? Location { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record RequestContext(
    string? Objective = null,
    string? TargetId = null,
    string? TargetName = null,
    bool HasPendingWork = false);

public sealed record MonitoringContext
{
    public MonitoringContext(
        string? targetId = null,
        string? conversationId = null,
        string? lastResponseSummary = null,
        bool awaitingUserInstruction = false,
        IReadOnlyList<string>? decisions = null)
    {
        TargetId = targetId;
        ConversationId = conversationId;
        LastResponseSummary = lastResponseSummary;
        AwaitingUserInstruction = awaitingUserInstruction;
        Decisions = decisions ?? Array.Empty<string>();
    }

    public string? TargetId { get; init; }
    public string? ConversationId { get; init; }
    public string? LastResponseSummary { get; init; }
    public bool AwaitingUserInstruction { get; init; }
    public IReadOnlyList<string> Decisions { get; init; }
}

/// <summary>These contexts are intentionally independent; browsing never retargets a request.</summary>
public sealed record ConversationContexts(
    BrowsingContext Browsing,
    RequestContext Request,
    MonitoringContext Monitoring);

public sealed record PendingWork(
    string Objective,
    string TargetId,
    string TargetName,
    bool Dispatched = false);

public sealed record ConversationTurnResult(
    TurnContext Turn,
    ModelDecision Decision,
    ContextResolution Resolution,
    ConversationContexts Contexts);

public sealed record AgentResponseResult(
    string TargetId,
    string Response,
    string Summary,
    IReadOnlyList<string> Decisions,
    ModelDecision Decision,
    ConversationContexts Contexts);

/// <summary>
/// The single natural-language entry point for the desktop operator.
///
/// The coordinator decides what a turn means and binds an executable request to one exact
/// target. A caller that executes the returned tool can acknowledge dispatch separately; this
/// makes corrections safe while work is still pending and prevents a correction from pretending
/// that an already-sent request went somewhere else.
/// </summary>
public sealed class ConversationCoordinator
{
    private static readonly string[] ActionWords =
    {
        "ask", "send", "tell", "message", "write", "type", "open", "go", "find", "navigate",
        "focus", "switch", "investigate", "review", "check", "look", "read", "run", "create", "update",
        "delete", "remove", "close", "erase", "cancel", "mention", "add", "watch", "monitor"
    };

    private static readonly string[] DestructiveWords =
    {
        "delete", "remove", "erase", "uninstall", "close", "cancel", "discard", "overwrite", "format"
    };

    private static readonly string[] RecipientWords =
    {
        "ask", "send", "tell", "message", "write", "type", "mention", "forward", "relay"
    };

    private readonly ContextResolver _resolver;
    private readonly Dictionary<string, string> _storedPreferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TurnContext> _turns = new();
    private BrowsingContext _browsing = new();
    private RequestContext _request = new();
    private MonitoringContext _monitoring = new();
    private PendingWork? _pendingWork;

    public ConversationCoordinator(
        ContextResolver? resolver = null,
        IEnumerable<KeyValuePair<string, string>>? storedPreferences = null)
    {
        _resolver = resolver ?? new ContextResolver();
        if (storedPreferences != null)
        {
            foreach ((string key, string value) in storedPreferences)
            {
                SetStoredPreference(key, value);
            }
        }
    }

    public IReadOnlyList<TurnContext> Turns => _turns.AsReadOnly();

    public string? CurrentObjective => _request.Objective;

    public PendingWork? PendingWork => _pendingWork;

    public BrowsingContext BrowsingContext => _browsing;

    public RequestContext RequestContext => _request;

    public MonitoringContext MonitoringContext => _monitoring;

    public ConversationContexts Contexts => new(_browsing, _request, _monitoring);

    public void SetStoredPreference(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _storedPreferences[key.Trim()] = value.Trim();
    }

    /// <summary>Updates only browsing context. It cannot change the bound request or monitor.</summary>
    public void UpdateBrowsingContext(string? foregroundApp, string? location = null)
    {
        _browsing = new BrowsingContext(
            string.IsNullOrWhiteSpace(foregroundApp) ? null : foregroundApp.Trim(),
            string.IsNullOrWhiteSpace(location) ? null : location.Trim());
    }

    public ConversationTurnResult Process(
        string transcript,
        string originDevice = "pc",
        string? foregroundApp = null,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        var turn = new TurnContext(
            originDevice,
            transcript,
            _request.Objective,
            RelevantMemoryRefs(),
            timestamp,
            foregroundApp ?? _browsing.ForegroundApp);
        return ProcessTurn(turn);
    }

    public ConversationTurnResult HandleTurn(TurnContext turn) => ProcessTurn(turn);

    public ConversationTurnResult ProcessTurn(TurnContext turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        _turns.Add(turn);
        if (turn.ForegroundApp != null)
        {
            UpdateBrowsingContext(turn.ForegroundApp, _browsing.Location);
        }

        string text = turn.Transcript.Trim();
        if (IsStop(text))
        {
            _pendingWork = null;
            _request = new RequestContext();
            _monitoring = _monitoring with { AwaitingUserInstruction = false };
            return Result(turn, ModelDecision.Complete("Stopped the current desktop work and speech."), MissingResolution(text));
        }

        ContextResolution resolution = _resolver.Resolve(
            text,
            _request.Objective,
            _browsing.ForegroundApp,
            _storedPreferences,
            _request.TargetId);

        bool correction = IsCorrection(text);
        string objective = correction && _pendingWork != null
            ? ApplyCorrection(_pendingWork.Objective, text)
            : text;

        if (correction && _pendingWork != null && _pendingWork.Dispatched &&
            resolution.IsResolved && !string.Equals(resolution.TargetId, _pendingWork.TargetId, StringComparison.OrdinalIgnoreCase))
        {
            // Never silently rewrite the destination of something already sent.
            return Result(
                turn,
                ModelDecision.Respond(
                    $"The earlier request was already sent to {_pendingWork.TargetName}; I did not redirect it. " +
                    $"The new request can be sent to {resolution.TargetName} when you give the next instruction."),
                resolution);
        }

        if (!LooksExecutable(text) || IsConversationalExplanation(text))
        {
            string response = BuildConversationalResponse(text);
            return Result(turn, ModelDecision.Respond(response), resolution);
        }

        if (resolution.Status == ContextResolutionStatus.Ambiguous)
        {
            string choices = string.Join(", ", resolution.Candidates.Select(candidate => candidate.Name));
            return Result(turn, ModelDecision.Clarify($"Which destination should I use: {choices}?"), resolution);
        }

        bool needsRecipient = RequiresRecipient(text);
        if (!resolution.IsResolved && needsRecipient)
        {
            string prompt = resolution.Status == ContextResolutionStatus.Unknown
                ? "Which application or conversation should receive this?"
                : "I cannot identify the requested destination. Which application or conversation should receive this?";
            return Result(turn, ModelDecision.WaitForRecipient(prompt), resolution);
        }

        if (IsDestructive(text))
        {
            string target = resolution.TargetName ?? "the selected destination";
            return Result(
                turn,
                ModelDecision.Clarify($"This would {DescribeDestructiveAction(text)} in {target}. Do you want me to continue?", true),
                resolution);
        }

        if (!resolution.IsResolved)
        {
            return Result(turn, ModelDecision.Respond("I can explain that, but I need a destination before I act."), resolution);
        }

        if (correction && _pendingWork != null && _pendingWork.Dispatched)
        {
            return Result(turn, ModelDecision.Respond("I updated the objective for the next instruction; the earlier request is unchanged."), resolution);
        }

        _pendingWork = new PendingWork(objective, resolution.TargetId!, resolution.TargetName!);
        _request = new RequestContext(objective, resolution.TargetId, resolution.TargetName, true);
        _monitoring = _monitoring with { AwaitingUserInstruction = false };
        var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["objective"] = objective,
            ["targetId"] = resolution.TargetId!
        };
        return Result(
            turn,
            ModelDecision.InvokeTool("desktop.execute", objective, resolution.TargetId!, arguments),
            resolution);
    }

    /// <summary>Call this after the desktop adapter has actually sent the bound request.</summary>
    public void AcknowledgeToolInvocation()
    {
        if (_pendingWork != null)
        {
            _pendingWork = _pendingWork with { Dispatched = true };
        }
    }

    /// <summary>
    /// Captures an agent response, creates a local summary, exposes its questions/decisions, and
    /// waits. It never generates or sends an answer to the agent.
    /// </summary>
    public AgentResponseResult CaptureAgentResponse(
        string targetId,
        string response,
        string? conversationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(response);

        string summary = SummarizeLocally(response);
        string[] decisions = ExtractDecisions(response);
        _monitoring = new MonitoringContext(
            targetId.Trim(),
            conversationId,
            summary,
            awaitingUserInstruction: true,
            decisions);
        _pendingWork = null;
        _request = new RequestContext();
        ModelDecision decision = ModelDecision.WaitForRecipient(
            decisions.Length == 0
                ? $"Response from {targetId}: {summary}"
                : $"Response from {targetId}: {summary} Decisions or questions: {string.Join(" ", decisions)}");
        return new AgentResponseResult(targetId.Trim(), response, summary, decisions, decision, Contexts);
    }

    public AgentResponseResult RecordAgentResponse(string targetId, string response, string? conversationId = null) =>
        CaptureAgentResponse(targetId, response, conversationId);

    public static string SummarizeLocally(string response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        string normalized = Regex.Replace(response.Trim(), @"\s+", " ");
        string[] sentences = Regex.Split(normalized, @"(?<=[.!?])\s+")
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .Take(3)
            .ToArray();
        string summary = string.Join(" ", sentences);
        if (summary.Length > 480)
        {
            summary = summary[..477].TrimEnd() + "...";
        }
        return summary;
    }

    private ConversationTurnResult Result(TurnContext turn, ModelDecision decision, ContextResolution resolution) =>
        new(turn, decision, resolution, Contexts);

    private string[] RelevantMemoryRefs() =>
        _monitoring.TargetId == null ? Array.Empty<string>() : new[] { $"monitor:{_monitoring.TargetId}" };

    private static ContextResolution MissingResolution(string text) =>
        new(ContextResolutionStatus.Missing, null, null, Array.Empty<ContextTarget>(), null, text);

    private static bool LooksExecutable(string text) =>
        ActionWords.Any(word => Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase));

    private static bool RequiresRecipient(string text) =>
        RecipientWords
            .Any(word => Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase));

    private static bool IsDestructive(string text) =>
        DestructiveWords.Any(word => Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase));

    private static string DescribeDestructiveAction(string text)
    {
        string verb = DestructiveWords.FirstOrDefault(word =>
            Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase)) ?? "change";
        return verb;
    }

    private static bool IsStop(string text) =>
        Regex.IsMatch(text.Trim(), @"^(stop|cancel|never mind|nevermind|abort)([.!?\s]*)$", RegexOptions.IgnoreCase);

    private static bool IsConversationalExplanation(string text) =>
        text.Contains("don't do anything", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("do not do anything", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("just explain", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("what did it say", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("what did they say", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("how do ", StringComparison.OrdinalIgnoreCase);

    private string BuildConversationalResponse(string text)
    {
        if ((text.StartsWith("what did it say", StringComparison.OrdinalIgnoreCase) ||
             text.StartsWith("what did they say", StringComparison.OrdinalIgnoreCase)) &&
            _monitoring.LastResponseSummary != null)
        {
            return _monitoring.LastResponseSummary;
        }

        if (IsConversationalExplanation(text))
        {
            return "I will explain this without issuing a desktop action.";
        }
        return "I understand. Tell me if you want me to act on that.";
    }

    private static bool IsCorrection(string text) =>
        Regex.IsMatch(text.Trim(),
            @"^(actually|no[, ]|instead|change that|make it|add that|also|mention that|remove that|replace that)\b",
            RegexOptions.IgnoreCase);

    private static string ApplyCorrection(string previous, string correction)
    {
        string updated = Regex.Replace(
            correction.Trim(),
            @"^(actually|no|instead|change that|make it|add that|also|mention that|remove that|replace that)[, ]*",
            string.Empty,
            RegexOptions.IgnoreCase);
        if (updated.Length == 0) return previous;
        if (correction.Contains("remove", StringComparison.OrdinalIgnoreCase) ||
            correction.Contains("replace", StringComparison.OrdinalIgnoreCase))
        {
            return updated;
        }
        return $"{previous} {updated}".Trim();
    }

    private static string[] ExtractDecisions(string response)
    {
        return Regex.Split(response.Trim(), @"(?<=[.!?])\s+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => sentence.EndsWith('?') ||
                Regex.IsMatch(sentence, @"\b(decision|choose|select|approval|approve|blocked|needs you)\b", RegexOptions.IgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }
}
