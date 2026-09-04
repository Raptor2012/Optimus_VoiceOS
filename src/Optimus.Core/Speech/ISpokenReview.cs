namespace Optimus.Core.Speech;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Reads a draft and its destination aloud.
/// </summary>
/// <remarks>
/// A seam for testing the widget without an audio device or a speech model, not an engine
/// abstraction: there is exactly one implementation, and no selection or fallback between
/// engines exists anywhere.
/// <para>
/// Note what is absent. There is no send, confirm, or approval member, so the rule that nothing
/// is sent merely because speech finished holds by construction rather than by convention.
/// </para>
/// </remarks>
public interface ISpokenReview
{
    /// <summary>True while audio is being synthesized or played.</summary>
    bool IsSpeaking { get; }

    /// <summary>Speaks the exact draft, the exact destination, then the review question.</summary>
    Task<SpokenReviewResult> SpeakReviewAsync(
        string draft,
        string destinationName,
        CancellationToken cancellationToken = default);

    /// <summary>Stops speech immediately and silences the device.</summary>
    void Cancel();
}
