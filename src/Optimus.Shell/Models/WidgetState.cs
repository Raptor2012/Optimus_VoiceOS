namespace Optimus.Shell.Models;

public enum WidgetState
{
    Idle,
    Listening,
    Processing,
    Confirm,
    Sending,
    Sent,
    Error,
    ReadingDraft,
    AwaitingApproval,
    Redictating,

    /// <summary>A continuous session is open and waiting for the next utterance, hands free.</summary>
    SessionListening,

    /// <summary>Playback was interrupted by speech; brief contraction before transition to listening.</summary>
    Interrupted
}
