namespace Optimus.Core.Voice;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Captures short spoken approval commands or replacement dictation without requiring a hotkey.
/// </summary>
public interface IApprovalListener
{
    /// <summary>Gets a value indicating whether the listener is actively capturing audio.</summary>
    bool IsListening { get; }

    /// <summary>
    /// Listens for a short approval command (e.g. "send", "cancel", "redictate") after the chime,
    /// completing quickly once silence is detected.
    /// </summary>
    Task<byte[]> ListenForApprovalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Listens for a replacement prompt dictation following a redictate command,
    /// completing when the user finishes speaking.
    /// </summary>
    Task<byte[]> ListenForReplacementDictationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Listens for the next utterance in an open continuous session, where no hotkey hold
    /// precedes the speech.
    /// </summary>
    /// <remarks>
    /// Returns an empty array when the listening window expires with no speech. That is the
    /// ordinary quiet case, not an error: the caller simply asks again, which bounds how much
    /// audio is ever held in memory while a session sits idle.
    /// </remarks>
    Task<byte[]> ListenForSessionUtteranceAsync(CancellationToken cancellationToken = default);

    /// <summary>Cancels active listening immediately and silences audio capture.</summary>
    void Cancel();
}
