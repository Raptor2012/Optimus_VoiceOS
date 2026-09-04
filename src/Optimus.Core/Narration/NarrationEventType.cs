namespace Optimus.Core.Narration;

/// <summary>
/// Identifies the semantic category of an observed agent event.
/// </summary>
public enum NarrationEventType
{
    /// <summary>
    /// Visible progress or reasoning text stream from the agent.
    /// </summary>
    Progress,

    /// <summary>
    /// Coarse status transition (e.g., planning, editing files, running tests, completed).
    /// </summary>
    StatusTransition,

    /// <summary>
    /// Visible invocation of an agent tool.
    /// </summary>
    ToolCall,

    /// <summary>
    /// Visible invocation of a higher-level skill.
    /// </summary>
    SkillUse,

    /// <summary>
    /// Short result from a tool execution.
    /// </summary>
    ToolResult,

    /// <summary>
    /// Short result from a skill execution.
    /// </summary>
    SkillResult,

    /// <summary>
    /// Direct question asked by the agent requiring user attention or response.
    /// </summary>
    AgentQuestion,

    /// <summary>
    /// Visible final completion response or concluding message from the agent.
    /// </summary>
    FinalResponse,

    /// <summary>
    /// Error, failure, or exception state.
    /// </summary>
    Error
}
