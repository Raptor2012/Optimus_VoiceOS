namespace Optimus.Providers.Ao;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

public sealed record AoAgentWorker(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model")] string Model
);

public sealed record AoFileChangeSummary(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("additions")] int Additions,
    [property: JsonPropertyName("deletions")] int Deletions
);

public sealed record AoProjectTask(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("progressPercent")] int ProgressPercent,
    [property: JsonPropertyName("agent")] AoAgentWorker Agent,
    [property: JsonPropertyName("conversationSnippet")] string ConversationSnippet,
    [property: JsonPropertyName("changedFiles")] IReadOnlyList<AoFileChangeSummary> ChangedFiles,
    [property: JsonPropertyName("planOrResultContent")] string PlanOrResultContent = ""
);

public sealed record AoProjectConversation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("lastMessage")] string LastMessage,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("messageCount")] int MessageCount
);

public sealed record AoProjectCardItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("objective")] string Objective,
    [property: JsonPropertyName("progressSentence")] string ProgressSentence,
    [property: JsonPropertyName("completedTasks")] int CompletedTasks,
    [property: JsonPropertyName("totalTasks")] int TotalTasks,
    [property: JsonPropertyName("activeWorkers")] IReadOnlyList<AoAgentWorker> ActiveWorkers,
    [property: JsonPropertyName("nextAction")] string NextAction,
    [property: JsonPropertyName("destinationId")] string DestinationId,
    [property: JsonPropertyName("tasks")] IReadOnlyList<AoProjectTask> Tasks,
    [property: JsonPropertyName("conversations")] IReadOnlyList<AoProjectConversation> Conversations,
    [property: JsonPropertyName("recentResultSummary")] string RecentResultSummary
);

public sealed class AoProjectBridge
{
    private readonly IAoClient _client;

    public AoProjectBridge(IAoClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<AoProjectCardItem>> GetLiveProjectCardsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _client.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
        if (projects.Count == 0)
        {
            return Array.Empty<AoProjectCardItem>();
        }

        var allSessions = await _client.GetSessionsAsync(null, cancellationToken).ConfigureAwait(false);
        var result = new List<AoProjectCardItem>();

        foreach (var proj in projects)
        {
            var projSessions = allSessions
                .Where(s => string.Equals(s.ProjectId, proj.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var workers = new List<AoAgentWorker>();
            var tasks = new List<AoProjectTask>();
            int completed = 0;

            foreach (var s in projSessions)
            {
                string harness = s.Harness ?? "agent";
                string model = s.Model ?? harness;
                string role = string.Equals(s.Kind, "orchestrator", StringComparison.OrdinalIgnoreCase)
                    ? "Orchestration & Coordination"
                    : "Implementation Worker";

                var worker = new AoAgentWorker(
                    s.Id,
                    s.DisplayName ?? s.Id,
                    role,
                    model
                );

                bool isCompleted = string.Equals(s.Status, "merged", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(s.DisplayStatus, "merged", StringComparison.OrdinalIgnoreCase);

                bool isActive = string.Equals(s.Activity?.State, "active", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Status, "working", StringComparison.OrdinalIgnoreCase);

                if (isCompleted) completed++;
                if (isActive && !workers.Any(w => w.Id == worker.Id))
                {
                    workers.Add(worker);
                }

                string taskStatus = isCompleted ? "Completed" : (isActive ? "In Progress" : "Queued");
                int progressPercent = isCompleted ? 100 : (isActive ? 75 : 10);

                string snippet = s.DisplayStatus ?? s.Status ?? "Session ready";
                if (s.Prs != null && s.Prs.Count > 0)
                {
                    snippet += $" (PR #{s.Prs[0].Number}: {s.Prs[0].State})";
                }

                string planOrResult = $"Session ID: {s.Id}\nKind: {s.Kind}\nHarness: {s.Harness}\nModel: {s.Model}\nBranch: {s.Branch}\nStatus: {s.DisplayStatus ?? s.Status}";

                tasks.Add(new AoProjectTask(
                    s.Id,
                    s.DisplayName ?? s.Id,
                    taskStatus,
                    progressPercent,
                    worker,
                    snippet,
                    Array.Empty<AoFileChangeSummary>(),
                    planOrResult
                ));
            }

            string objective = string.Equals(proj.Id, "optimus_voiceos", StringComparison.OrdinalIgnoreCase)
                ? "Ship personal-use voice-first horizontal layer between Windows 11 & Pixel 9a."
                : $"Manage and supervise coding agent sessions for {proj.Name}.";

            string progressSentence = projSessions.Count > 0
                ? $"{completed} of {projSessions.Count} tasks completed. {workers.Count} active workers."
                : "Project registered and ready for sessions.";

            string nextAction = workers.Count > 0
                ? $"Monitor active worker {workers[0].Name}."
                : "Plan or spawn next task slice.";

            string recentResult = projSessions.FirstOrDefault(s => string.Equals(s.Status, "merged", StringComparison.OrdinalIgnoreCase))?.DisplayName != null
                ? $"Merged recent task: {projSessions.First(s => string.Equals(s.Status, "merged", StringComparison.OrdinalIgnoreCase)).DisplayName}"
                : "No merged tasks yet.";

            var convs = new List<AoProjectConversation>();
            var orch = projSessions.FirstOrDefault(s => string.Equals(s.Kind, "orchestrator", StringComparison.OrdinalIgnoreCase));
            if (orch != null)
            {
                convs.Add(new AoProjectConversation(
                    orch.Id,
                    $"{proj.Name} Orchestrator",
                    orch.DisplayStatus ?? "Active",
                    DateTimeOffset.UtcNow.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture),
                    5
                ));
            }

            result.Add(new AoProjectCardItem(
                proj.Id,
                proj.Name,
                objective,
                progressSentence,
                completed,
                projSessions.Count,
                workers,
                nextAction,
                $"ao:{proj.Id}",
                tasks,
                convs,
                recentResult
            ));
        }

        return result;
    }
}
