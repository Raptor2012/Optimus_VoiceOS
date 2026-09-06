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

public sealed record AoConversationActivity(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("activityStatus")] string? ActivityStatus = null,
    [property: JsonPropertyName("delta")] string? Delta = null,
    [property: JsonPropertyName("detail")] object? Detail = null,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt = null
);

public sealed record AoConversationTurn(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("providerTurnId")] string? ProviderTurnId = null,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset? RequestedAt = null,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt = null,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt = null,
    [property: JsonPropertyName("plan")] object? Plan = null
);

public sealed record AoConversationResponse(
    [property: JsonPropertyName("conversationId")] string? ConversationId = null,
    [property: JsonPropertyName("sessionId")] string? SessionId = null,
    [property: JsonPropertyName("turns")] IReadOnlyList<AoConversationTurn>? Turns = null,
    [property: JsonPropertyName("messages")] IReadOnlyList<AoConversationMessage>? Messages = null,
    [property: JsonPropertyName("activities")] IReadOnlyList<AoConversationActivity>? Activities = null
);

public sealed record AoSendMessageRequest(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("clientMessageId")] string? ClientMessageId = null
);

public sealed record AoLegacySendMessageRequest(
    [property: JsonPropertyName("message")] string Message
);

public sealed record AoSendMessageResponse(
    [property: JsonPropertyName("ok")] bool Ok = true,
    [property: JsonPropertyName("sessionId")] string? SessionId = null,
    [property: JsonPropertyName("turnId")] string? TurnId = null,
    [property: JsonPropertyName("duplicate")] bool Duplicate = false,
    [property: JsonPropertyName("message")] string? Message = null
);

public sealed record AoResolveApprovalRequest(
    [property: JsonPropertyName("decisionId")] string DecisionId
);

public sealed record AoCdcEvent(
    long Seq,
    string? ProjectId,
    string? SessionId,
    string Type,
    string? PayloadJson,
    DateTimeOffset CreatedAt
);

public sealed record AoProjectsResponse(
    [property: JsonPropertyName("projects")] IReadOnlyList<AoProject> Projects
);

public sealed record AoSessionsResponse(
    [property: JsonPropertyName("sessions")] IReadOnlyList<AoSession> Sessions
);
