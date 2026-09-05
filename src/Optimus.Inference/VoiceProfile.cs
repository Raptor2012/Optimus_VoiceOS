namespace Optimus.Inference;

using System.Globalization;

/// <summary>
/// The synthesis profile that gives Optimus its own speaking character.
/// </summary>
/// <remarks>
/// <para>
/// This is an original profile, not an impersonation. It is a set of synthesis parameters
/// applied to a stock neural voice, chosen for four qualities the review flow needs: weight
/// (unhurried pacing), clarity (steady phoneme timing so identifiers survive), restraint (low
/// generator variance, so it does not perform), and authority (a short settled pause after each
/// sentence rather than a rushed run-on).
/// </para>
/// <para>
/// The requested direction is an Optimus-Prime-inspired robotic commander: lower register,
/// restrained cadence, subtle metallic texture. This does not reproduce the actor's performance;
/// no voice cloning, reference audio, or speaker embedding is used.
/// </para>
/// <para>
/// Base voice selected by the S006 measurement on this machine: Piper
/// <c>en_GB-northern_english_male-medium</c> had the lowest warm time-to-first-audio (257 ms
/// median) and the best real-time factor (0.149) of every candidate that met the latency target.
/// </para>
/// </remarks>
public sealed record VoiceProfile(
    double LengthScale,
    double NoiseScale,
    double NoiseW,
    double SentenceSilenceSeconds)
{
    public double PitchRatio { get; init; } = 0.93;
    public double Resonance { get; init; } = 0.06;
    /// <summary>
    /// The shipped profile.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>LengthScale 1.00</c> — neutral synthesis speed; pitch coloration adds about
    /// 7.5 percent duration, so the combined voice remains deliberate without a long delay.</item>
    /// <item><c>NoiseScale 0.55</c> — below the 0.667 default, reducing pitch waver so the voice
    /// sounds settled rather than expressive.</item>
    /// <item><c>NoiseW 0.60</c> — below the 0.8 default, steadying phoneme durations. This is what
    /// keeps <c>fetchUser</c> and <c>src/config.ts</c> crisp instead of slurred.</item>
    /// <item><c>SentenceSilence 0.15</c> — a real but tight pause between sentences: enough to
    /// sound composed, short enough to keep segment handoff under the gap target.</item>
    /// </list>
    /// </remarks>
    public static VoiceProfile Default { get; } = new(
        LengthScale: 1.00,
        NoiseScale: 0.55,
        NoiseW: 0.60,
        SentenceSilenceSeconds: 0.15);

    /// <summary>Renders the profile as piper command-line arguments.</summary>
    public string[] ToArguments() =>
    [
        "--length_scale", LengthScale.ToString(CultureInfo.InvariantCulture),
        "--noise_scale", NoiseScale.ToString(CultureInfo.InvariantCulture),
        "--noise_w", NoiseW.ToString(CultureInfo.InvariantCulture),
        "--sentence_silence", SentenceSilenceSeconds.ToString(CultureInfo.InvariantCulture)
    ];
}
