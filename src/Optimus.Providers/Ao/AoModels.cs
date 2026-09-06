namespace Optimus.Providers.Ao;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed record AoProject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("sessionPrefix")] string? SessionPrefix = null,
    [property: JsonPropertyName("orchestratorAgent")] string? OrchestratorAgent = null,
    [property: JsonPropertyName("folderMissing")] bool FolderMissing = false
);

public sealed record AoSessionActivity(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("lastActivityAt")] DateTimeOffset? LastActivityAt = null
);

public sealed record AoSessionPr(
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("number")] int Number = 0,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("mergeability")] string? Mergeability = null
);

public sealed record AoSession(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("kind")] string Kind, // "worker" | "orchestrator"
    [property: JsonPropertyName("harness")] string Harness,
    [property: JsonPropertyName("displayName")] string? DisplayName = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("displayStatus")] string? DisplayStatus = null,
    [property: JsonPropertyName("kanbanColumn")] string? KanbanColumn = null,
    [property: JsonPropertyName("branch")] string? Branch = null,
    [property: JsonPropertyName("activity")] AoSessionActivity? Activity = null,
    [property: JsonPropertyName("prs")] IReadOnlyList<AoSessionPr>? Prs = null,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt = null,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset? UpdatedAt = null,
    [property: JsonPropertyName("isTerminated")] bool IsTerminated = false
);

public sealed record AoConversationMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("role")] string Role, // "user" | "assistant"
    [property: JsonPropertyName("origin")] string? Origin = null,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt = null
);

public sealed record AoSendMessageRequest(
    [property: JsonPropertyName("message")] string Message
);

public sealed record AoSendMessageResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("message")] string? Message = null
);

public sealed record AoProjectsResponse(
    [property: JsonPropertyName("projects")] IReadOnlyList<AoProject> Projects
);

public sealed record AoSessionsResponse(
    [property: JsonPropertyName("sessions")] IReadOnlyList<AoSession> Sessions
);

public sealed record AoConversationResponse(
    [property: JsonPropertyName("messages")] IReadOnlyList<AoConversationMessage>? Messages = null
);
