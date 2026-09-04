namespace Optimus.Core.Narration;

/// <summary>
/// Configuration options for the voice narration scheduler.
/// </summary>
public sealed record NarrationOptions
{
    /// <summary>
    /// Gets the narration mode (Concise or Comprehensive). Default is Concise.
    /// </summary>
    public NarrationMode Mode { get; init; } = NarrationMode.Concise;

    /// <summary>
    /// Gets a value indicating whether tool calls, skill uses, and their results should be narrated.
    /// Operates independently of <see cref="Mode"/>. Default is false.
    /// </summary>
    public bool NarrateToolsAndSkills { get; init; }
}
