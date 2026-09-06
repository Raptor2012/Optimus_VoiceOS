namespace Optimus.Core.Workflow;

using System.Text.RegularExpressions;
using Optimus.Providers.Ao;

public enum WorkflowOperation
{
    ProjectStatus,
    ActiveSessions,
    SessionDetails,
    SpawnWorker,
    SendToWorker,
    PullRequestStatus,
    Unsupported
}

/// <summary>Structured, narration-ready result of a local AO workflow operation.</summary>
public sealed record WorkflowResult(
    WorkflowOperation Operation,
    bool Success,
    string Summary,
    object? Data = null);

/// <summary>
/// Translates a small set of natural-language workflow objectives into AO operations. The
/// orchestrator intentionally stays deterministic: it never invents a worker, session, or PR id.
/// </summary>
public sealed class WorkflowOrchestrator
{
    private readonly AoWorkflowBridge _bridge;

    public WorkflowOrchestrator(AoWorkflowBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    public Task<WorkflowResult> ExecuteAsync(string objective, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        string text = objective.Trim();

        if (LooksLikeSpawn(text)) return SpawnAsync(text, cancellationToken);
        if (TryReadWorkerMessage(text, out string? workerId, out string? message)) return SendAsync(workerId!, message!, cancellationToken);
        if (TryReadPrNumber(text, out int prNumber)) return GetPrAsync(prNumber, cancellationToken);
        if (TryReadSessionId(text, out string? sessionId)) return GetSessionAsync(sessionId!, cancellationToken);
        if (LooksLikeWorkers(text)) return GetWorkersAsync(cancellationToken);
        if (LooksLikeStatus(text)) return GetStatusAsync(cancellationToken);

        return Task.FromResult(new WorkflowResult(
            WorkflowOperation.Unsupported,
            false,
            "I can check AO project status, list active workers, inspect a session or pull request, send a worker a message, or spawn a worker."));
    }

    public Task<WorkflowResult> HandleAsync(string objective, CancellationToken cancellationToken = default) => ExecuteAsync(objective, cancellationToken);
    public Task<WorkflowResult> ProcessAsync(string objective, CancellationToken cancellationToken = default) => ExecuteAsync(objective, cancellationToken);

    private async Task<WorkflowResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        AoProjectStatus status = await _bridge.GetProjectStatus(cancellationToken).ConfigureAwait(false);
        return new(WorkflowOperation.ProjectStatus, status.IsAvailable, status.Summary, status);
    }

    private async Task<WorkflowResult> GetWorkersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AoSession> sessions = await _bridge.GetActiveSessions(cancellationToken).ConfigureAwait(false);
        if (sessions.Count == 0)
            return new(WorkflowOperation.ActiveSessions, true, "There are no active AO workers or sessions.", sessions);

        string summary = string.Join(" ", sessions.Select(session =>
            $"{session.DisplayName ?? session.Id} is {session.DisplayStatus ?? session.Status ?? session.Activity?.State ?? "active"}."));
        return new(WorkflowOperation.ActiveSessions, true, $"I found {sessions.Count} active AO session{(sessions.Count == 1 ? "" : "s")}. {summary}", sessions);
    }

    private async Task<WorkflowResult> GetSessionAsync(string id, CancellationToken cancellationToken)
    {
        AoSessionDetails? details = await _bridge.GetSessionDetails(id, cancellationToken).ConfigureAwait(false);
        return details == null
            ? new(WorkflowOperation.SessionDetails, false, $"I could not find AO session {id}.")
            : new(WorkflowOperation.SessionDetails, true, details.Summary, details);
    }

    private async Task<WorkflowResult> SpawnAsync(string text, CancellationToken cancellationToken)
    {
        string prompt = Regex.Match(text, @"\bfor\s+(?<prompt>.+)$", RegexOptions.IgnoreCase).Groups["prompt"].Value.Trim();
        if (prompt.Length == 0)
            prompt = Regex.Replace(text, @"^spawn(?:\s+a|\s+an|\s+new)?\s+worker\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (prompt.Length == 0)
            return new(WorkflowOperation.SpawnWorker, false, "What should the new worker work on?");

        string name = prompt.Length <= 48 ? prompt : prompt[..48].TrimEnd() + "…";
        AoWorkerSpawnResult result = await _bridge.SpawnWorker(name, prompt, cancellationToken).ConfigureAwait(false);
        return new(WorkflowOperation.SpawnWorker, result.Success, result.Summary, result);
    }

    private async Task<WorkflowResult> SendAsync(string workerId, string message, CancellationToken cancellationToken)
    {
        string summary = await _bridge.SendToWorker(workerId, message, cancellationToken).ConfigureAwait(false);
        bool success = summary.StartsWith("Sent ", StringComparison.Ordinal);
        return new(WorkflowOperation.SendToWorker, success, summary);
    }

    private async Task<WorkflowResult> GetPrAsync(int number, CancellationToken cancellationToken)
    {
        AoPrState? state = await _bridge.GetPrState(number, cancellationToken).ConfigureAwait(false);
        return state == null
            ? new(WorkflowOperation.PullRequestStatus, false, $"I could not find pull request {number} in AO.")
            : new(WorkflowOperation.PullRequestStatus, true, state.Summary, state);
    }

    private static bool LooksLikeSpawn(string text) =>
        Regex.IsMatch(text, @"\bspawn\b.*\bworker\b|\bworker\b.*\bspawn\b", RegexOptions.IgnoreCase);

    private static bool LooksLikeWorkers(string text) =>
        Regex.IsMatch(text, @"\b(workers?|sessions?|agents?)\b", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(text, @"\b(check|list|status|active|running|working|show|monitor)\b", RegexOptions.IgnoreCase);

    private static bool LooksLikeStatus(string text) =>
        Regex.IsMatch(text, @"\b(project|AO|orchestrator)\b", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(text, @"\b(check|status|how|what|summary|progress)\b", RegexOptions.IgnoreCase);

    private static bool TryReadWorkerMessage(string text, out string? workerId, out string? message)
    {
        Match match = Regex.Match(text, @"(?:send|tell|message)\s+(?<message>.+?)\s+to\s+(?:worker\s+)?(?<id>[A-Za-z0-9_.:-]+)$", RegexOptions.IgnoreCase);
        workerId = match.Success ? match.Groups["id"].Value : null;
        message = match.Success ? match.Groups["message"].Value.Trim() : null;
        return match.Success && !string.IsNullOrWhiteSpace(workerId) && !string.IsNullOrWhiteSpace(message);
    }

    private static bool TryReadPrNumber(string text, out int number)
    {
        number = 0;
        Match match = Regex.Match(text, @"(?:PR|pull\s+request)\s*#?\s*(?<number>\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["number"].Value, out number) && number > 0;
    }

    private static bool TryReadSessionId(string text, out string? id)
    {
        Match match = Regex.Match(text, @"(?:session|worker)\s+(?<id>[A-Za-z0-9_.:-]+)", RegexOptions.IgnoreCase);
        id = match.Success ? match.Groups["id"].Value : null;
        return match.Success && !string.IsNullOrWhiteSpace(id);
    }
}
