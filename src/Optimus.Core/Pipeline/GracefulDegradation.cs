namespace Optimus.Core.Pipeline;

using Optimus.Inference;
using Optimus.Providers;

/// <summary>Centralized, user-facing fallbacks for unavailable pipeline stages.</summary>
public static class GracefulDegradation
{
    public const double MinimumSpeechConfidence = 0.55;

    public static string ForLowConfidence(double? confidence) => confidence is null
        ? "I could not make out that request. Could you say it again?"
        : $"I only heard that with {confidence:P0} confidence. Could you repeat or clarify it?";

    public static string ForModelUnavailable(string? detail = null) =>
        "The local model is unavailable right now, so I queued your request and will continue when it is ready." +
        (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail.Trim()})");

    public static string ForTargetUnavailable(string target, string? alternatives = null) =>
        $"{target} is not running or is not ready to receive input." +
        (string.IsNullOrWhiteSpace(alternatives) ? " Open it and try again, or choose another destination." : $" You could use {alternatives} instead.");

    public static string ForException(string stage) =>
        $"I could not complete the {stage} step. Nothing was sent; please try again.";

    public static bool IsLowConfidence(TranscriptionResult result) =>
        result.Confidence.HasValue && result.Confidence.Value < MinimumSpeechConfidence;

    public static bool IsTargetUnavailable(ProviderState state) => !state.IsAvailable || !state.InputReady;
}
