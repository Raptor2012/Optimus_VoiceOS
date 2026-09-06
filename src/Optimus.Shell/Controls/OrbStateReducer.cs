namespace Optimus.Shell.Controls;

using Optimus.Shell.Models;

/// <summary>
/// Determines orb visual parameters from the current <see cref="WidgetState"/>, audio levels,
/// and user toggles. A single source of truth for animation, labels, and control eligibility.
/// </summary>
public sealed class OrbStateReducer
{
    /// <summary>Computed orb visual state, applied every frame by <see cref="VoiceOrb"/>.</summary>
    public readonly record struct OrbFrame(
        /// <summary>Base radius multiplier (1.0 = rest). Breathing adds ±0.03; listening peaks at ~1.15.</summary>
        double RadiusScale,
        /// <summary>Opacity of the orb core [0..1]. Dim when not listening.</summary>
        double CoreOpacity,
        /// <summary>Opacity of an outer halo ring [0..1]. Used for awaiting-confirmation glow.</summary>
        double HaloOpacity,
        /// <summary>Internal rotation speed in degrees/second. Nonzero for interpreting/processing.</summary>
        double RotationSpeed,
        /// <summary>Fill color hex for the orb gradient center.</summary>
        string CenterColor,
        /// <summary>Fill color hex for the orb gradient edge.</summary>
        string EdgeColor,
        /// <summary>Short label shown beside the orb.</summary>
        string Label,
        /// <summary>Whether the microphone toggle should show enabled.</summary>
        bool MicEligible,
        /// <summary>Whether playback is currently active (for mute icon state).</summary>
        bool PlaybackActive);

    // Cached per-state base frames. Energy is blended on top.
    private static readonly OrbFrame NotListening = new(
        RadiusScale: 1.0, CoreOpacity: 0.35, HaloOpacity: 0.0, RotationSpeed: 0,
        CenterColor: "#404860", EdgeColor: "#252A36",
        Label: "Not listening", MicEligible: false, PlaybackActive: false);

    private static readonly OrbFrame IdleBase = new(
        RadiusScale: 1.0, CoreOpacity: 0.6, HaloOpacity: 0.0, RotationSpeed: 0,
        CenterColor: "#8C9EFF", EdgeColor: "#3D4A6B",
        Label: "Ready", MicEligible: true, PlaybackActive: false);

    private static readonly OrbFrame ListeningBase = new(
        RadiusScale: 1.0, CoreOpacity: 0.95, HaloOpacity: 0.0, RotationSpeed: 0,
        CenterColor: "#8C9EFF", EdgeColor: "#5C6BC0",
        Label: "Listening", MicEligible: true, PlaybackActive: false);

    private static readonly OrbFrame InterpretingBase = new(
        RadiusScale: 1.0, CoreOpacity: 0.85, HaloOpacity: 0.0, RotationSpeed: 120,
        CenterColor: "#7C8CFF", EdgeColor: "#4A5A9F",
        Label: "Processing", MicEligible: false, PlaybackActive: false);

    private static readonly OrbFrame SpeakingBase = new(
        RadiusScale: 1.0, CoreOpacity: 0.85, HaloOpacity: 0.0, RotationSpeed: 0,
        CenterColor: "#A78BFA", EdgeColor: "#6D5BBB",
        Label: "Speaking", MicEligible: false, PlaybackActive: true);

    private static readonly OrbFrame AwaitingBase = new(
        RadiusScale: 1.0, CoreOpacity: 0.8, HaloOpacity: 0.4, RotationSpeed: 0,
        CenterColor: "#8C9EFF", EdgeColor: "#5C6BC0",
        Label: "Confirm?", MicEligible: true, PlaybackActive: false);

    private static readonly OrbFrame SendingBase = new(
        RadiusScale: 1.0, CoreOpacity: 0.75, HaloOpacity: 0.0, RotationSpeed: 180,
        CenterColor: "#5E5CE6", EdgeColor: "#3D3B99",
        Label: "Sending", MicEligible: false, PlaybackActive: false);

    private static readonly OrbFrame SentBase = new(
        RadiusScale: 1.0, CoreOpacity: 0.7, HaloOpacity: 0.0, RotationSpeed: 0,
        CenterColor: "#30D158", EdgeColor: "#1E8E3A",
        Label: "Sent", MicEligible: true, PlaybackActive: false);

    private static readonly OrbFrame InterruptedBase = new(
        RadiusScale: 0.82, CoreOpacity: 0.9, HaloOpacity: 0.0, RotationSpeed: 0,
        CenterColor: "#8C9EFF", EdgeColor: "#5C6BC0",
        Label: "Interrupted", MicEligible: true, PlaybackActive: false);

    private static readonly OrbFrame ErrorBase = new(
        RadiusScale: 1.0, CoreOpacity: 0.8, HaloOpacity: 0.0, RotationSpeed: 0,
        CenterColor: "#FF453A", EdgeColor: "#992822",
        Label: "Error", MicEligible: true, PlaybackActive: false);

    private static readonly OrbFrame MonitoringBase = new(
        RadiusScale: 1.0, CoreOpacity: 0.65, HaloOpacity: 0.15, RotationSpeed: 30,
        CenterColor: "#30D158", EdgeColor: "#1E8E3A",
        Label: "Monitoring", MicEligible: true, PlaybackActive: true);

    /// <summary>
    /// Compute the current orb frame from widget state and live audio energy.
    /// </summary>
    /// <param name="state">Current widget state.</param>
    /// <param name="micEnergy">Normalised microphone energy [0..1].</param>
    /// <param name="playbackEnergy">Normalised playback energy [0..1].</param>
    /// <param name="isSpeakingReview">True while the TTS is reading aloud.</param>
    /// <param name="isSessionOpen">True while a continuous session is active.</param>
    /// <param name="isPaused">True if the user paused listening.</param>
    /// <param name="isMuted">True if speech playback is muted.</param>
    /// <param name="breathPhase">Breathing animation phase [0..2π], advanced by caller.</param>
    public static OrbFrame Reduce(
        WidgetState state,
        double micEnergy,
        double playbackEnergy,
        bool isSpeakingReview,
        bool isSessionOpen,
        bool isPaused,
        bool isMuted,
        double breathPhase)
    {
        if (isPaused)
        {
            return NotListening;
        }

        OrbFrame basis = state switch
        {
            WidgetState.Idle when isSessionOpen => IdleBase with { Label = "Session idle" },
            WidgetState.Idle => IdleBase,
            WidgetState.Listening => ListeningBase,
            WidgetState.SessionListening => ListeningBase with { Label = "Session listening" },
            WidgetState.Processing => InterpretingBase,
            WidgetState.Confirm => AwaitingBase with { Label = "Review" },
            WidgetState.ReadingDraft when isSpeakingReview => SpeakingBase,
            WidgetState.ReadingDraft => InterpretingBase with { Label = "Reading" },
            WidgetState.AwaitingApproval => AwaitingBase,
            WidgetState.Redictating => ListeningBase with { Label = "Redictating" },
            WidgetState.Interrupted => InterruptedBase,
            WidgetState.Sending => SendingBase,
            WidgetState.Sent => SentBase,
            WidgetState.Error => ErrorBase,
            _ => IdleBase
        };

        // Apply breathing pulse on idle states
        double breathScale = 1.0;
        if (state is WidgetState.Idle or WidgetState.Sent or WidgetState.SessionListening)
        {
            breathScale = 1.0 + 0.03 * System.Math.Sin(breathPhase);
        }

        // Apply mic energy expansion during listening
        double energyScale = 1.0;
        if (state is WidgetState.Listening or WidgetState.SessionListening or WidgetState.Redictating or WidgetState.Interrupted)
        {
            energyScale = 1.0 + 0.15 * micEnergy;
        }

        // Apply playback energy pulsing during speaking
        double speakScale = 1.0;
        if (isSpeakingReview || state is WidgetState.ReadingDraft)
        {
            speakScale = 1.0 + 0.08 * playbackEnergy;
        }

        double finalScale = basis.RadiusScale * breathScale * energyScale * speakScale;
        bool playbackActive = basis.PlaybackActive && !isMuted;

        return basis with
        {
            RadiusScale = finalScale,
            PlaybackActive = playbackActive
        };
    }
}
