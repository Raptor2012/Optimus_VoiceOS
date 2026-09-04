namespace Optimus.Inference;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Raw speech-to-text output plus its timing.</summary>
public sealed record TranscriptionResult(string Text, long ElapsedMilliseconds, double AudioSeconds)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>Real-time factor: decode time relative to audio length. Lower is faster.</summary>
    public double RealTimeFactor =>
        AudioSeconds > 0 ? ElapsedMilliseconds / 1000.0 / AudioSeconds : 0;
}

/// <summary>Cleanup output plus its timing.</summary>
public sealed record CleanupResult(string Text, long ElapsedMilliseconds, bool Applied, string? UnavailableReason = null);

/// <summary>Everything the widget needs to render one utterance.</summary>
public sealed record VoicePipelineResult(
    string RawTranscript,
    string CleanedDraft,
    long TranscribeMilliseconds,
    long CleanupMilliseconds,
    long TotalMilliseconds,
    double AudioSeconds,
    bool CleanupApplied,
    string? CleanupUnavailableReason)
{
    public double RealTimeFactor =>
        AudioSeconds > 0 ? TranscribeMilliseconds / 1000.0 / AudioSeconds : 0;

    /// <summary>One-line stage breakdown for the widget's status area.</summary>
    public string TimingSummary =>
        $"audio {AudioSeconds:F1}s · STT {TranscribeMilliseconds} ms · cleanup {CleanupMilliseconds} ms · total {TotalMilliseconds} ms";
}

public interface ISpeechTranscriber : IDisposable
{
    bool IsLoaded { get; }

    void EnsureLoaded();

    TranscriptionResult Transcribe(byte[] pcm16Mono16k, CancellationToken cancellationToken = default);
}

public interface IPromptCleaner : IDisposable
{
    bool IsLoaded { get; }

    void EnsureLoaded();

    Task<CleanupResult> CleanAsync(string rawTranscript, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one throwaway cleanup so the first real prompt does not pay first-call cost.
    /// </summary>
    /// <remarks>
    /// Loading the weights is not enough. The measured 3.8 s first call against ~900 ms
    /// afterwards is prompt processing of the fixed system + few-shot prefix, which the server
    /// then keeps in its KV cache. Priming pays that once, in the background, at startup.
    /// </remarks>
    Task PrimeAsync(CancellationToken cancellationToken = default);
}
