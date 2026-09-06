namespace Optimus.Core.Conversation;

public enum ModelDecisionKind
{
    Respond,
    Clarify,
    InvokeTool,
    WaitForRecipient,
    Complete
}

/// <summary>A local-model decision. A tool decision always carries the exact resolved target.</summary>
public sealed record ModelDecision
{
    public ModelDecision(
        ModelDecisionKind kind,
        string message,
        string? toolName = null,
        string? targetId = null,
        IReadOnlyDictionary<string, string>? arguments = null,
        bool requiresConfirmation = false)
    {
        Kind = kind;
        Message = message ?? string.Empty;
        ToolName = toolName;
        TargetId = targetId;
        Arguments = arguments ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        RequiresConfirmation = requiresConfirmation;
    }

    public ModelDecisionKind Kind { get; }

    /// <summary>Alias useful to callers that describe the value as a decision type.</summary>
    public ModelDecisionKind Type => Kind;

    public string Message { get; }

    public string? ToolName { get; }

    /// <summary>Set only after reference resolution; never infer a destination at execution time.</summary>
    public string? TargetId { get; }

    public IReadOnlyDictionary<string, string> Arguments { get; }

    public bool RequiresConfirmation { get; }

    public bool IsExecutable => Kind == ModelDecisionKind.InvokeTool && !RequiresConfirmation;

    public static ModelDecision Respond(string message) =>
        new(ModelDecisionKind.Respond, message);

    public static ModelDecision Clarify(string message, bool requiresConfirmation = false) =>
        new(ModelDecisionKind.Clarify, message, requiresConfirmation: requiresConfirmation);

    public static ModelDecision InvokeTool(
        string toolName,
        string objective,
        string targetId,
        IReadOnlyDictionary<string, string>? arguments = null) =>
        new(
            ModelDecisionKind.InvokeTool,
            objective,
            toolName,
            targetId,
            arguments ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static ModelDecision WaitForRecipient(string message) =>
        new(ModelDecisionKind.WaitForRecipient, message);

    public static ModelDecision Complete(string message = "Completed.") =>
        new(ModelDecisionKind.Complete, message);
}
