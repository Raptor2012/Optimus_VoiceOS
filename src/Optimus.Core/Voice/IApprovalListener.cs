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

    /// <summary>Cancels active listening immediately and silences audio capture.</summary>
    void Cancel();
}
