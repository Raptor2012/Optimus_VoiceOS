namespace Optimus.Core.Narration;

/// <summary>
/// Controls the granularity of voice narration.
/// </summary>
public enum NarrationMode
{
    /// <summary>
    /// Emits only meaningful transitions, agent questions, failures, and final responses,
    /// while suppressing ordinary progress noise.
    /// </summary>
    Concise,

    /// <summary>
    /// Emits every newly visible reasoning and progress message in order,
    /// as well as transitions, questions, and final responses.
    /// </summary>
    Comprehensive
}
