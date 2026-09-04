namespace Optimus.Inference;

using System;
using System.IO;

/// <summary>
/// Resolves on-disk model locations. Override the root with <c>OPTIMUS_MODELS_DIR</c>.
/// Weights live outside the repository and are never committed.
/// </summary>
public static class ModelLocator
{
    public const string RootEnvironmentVariable = "OPTIMUS_MODELS_DIR";

    private const string DefaultRoot = @"D:\SamHaydenVoiceTool\models";

    public static string Root =>
        Environment.GetEnvironmentVariable(RootEnvironmentVariable) is { Length: > 0 } custom
            ? custom
            : DefaultRoot;

    public static string ParakeetDirectory => Path.Combine(Root, "parakeet-tdt-0.6b-v2-int8");

    public static string ParakeetEncoder => Path.Combine(ParakeetDirectory, "encoder.int8.onnx");

    public static string ParakeetDecoder => Path.Combine(ParakeetDirectory, "decoder.int8.onnx");

    public static string ParakeetJoiner => Path.Combine(ParakeetDirectory, "joiner.int8.onnx");

    public static string ParakeetTokens => Path.Combine(ParakeetDirectory, "tokens.txt");

    public static string ParakeetSampleWav => Path.Combine(ParakeetDirectory, "test_wavs", "0.wav");

    /// <summary>Gemma 4 E2B instruct, QAT q4_0 GGUF.</summary>
    public static string GemmaCleanupModel =>
        Path.Combine(Root, "gemma-4-e2b", "gemma-4-E2B_q4_0-it.gguf");

    /// <summary>llama.cpp server binary. Override with <c>OPTIMUS_LLAMA_SERVER</c>.</summary>
    public static string LlamaServerExecutable =>
        Environment.GetEnvironmentVariable("OPTIMUS_LLAMA_SERVER") is { Length: > 0 } custom
            ? custom
            : @"D:\SamHaydenVoiceTool\runtimes\llama.cpp\llama-server.exe";

    /// <summary>
    /// The voice selected by the S006 measurement on this machine, confirmed by listening.
    /// Override with <c>OPTIMUS_TTS_VOICE</c>.
    /// </summary>
    /// <remarks>
    /// Measured warm time-to-first-audio 257 ms median and real-time factor 0.149, the fastest
    /// of every candidate. Kokoro voices scored better on depth and intelligibility but missed
    /// the 250 ms first-audio target by roughly four times on this CPU path.
    /// </remarks>
    public static string TtsVoiceModel =>
        Environment.GetEnvironmentVariable("OPTIMUS_TTS_VOICE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Root, "piper-voices", "en_GB-northern_english_male-medium.onnx");

    /// <summary>Piper binary. Override with <c>OPTIMUS_PIPER</c>.</summary>
    public static string PiperExecutable =>
        Environment.GetEnvironmentVariable("OPTIMUS_PIPER") is { Length: > 0 } custom
            ? custom
            : @"D:\SamHaydenVoiceTool\runtimes\piper\piper.exe";

    public static bool TtsAvailable =>
        File.Exists(TtsVoiceModel) &&
        File.Exists(TtsVoiceModel + ".json") &&
        File.Exists(PiperExecutable);

    public static void RequireTts()
    {
        foreach (string path in new[] { PiperExecutable, TtsVoiceModel, TtsVoiceModel + ".json" })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Text-to-speech file is missing: {path}", path);
            }
        }
    }

    public static bool ParakeetAvailable =>
        File.Exists(ParakeetEncoder) &&
        File.Exists(ParakeetDecoder) &&
        File.Exists(ParakeetJoiner) &&
        File.Exists(ParakeetTokens);

    public static bool GemmaAvailable =>
        File.Exists(GemmaCleanupModel) && File.Exists(LlamaServerExecutable);

    /// <summary>Throws with the exact missing path, so a setup problem is obvious.</summary>
    public static void RequireParakeet()
    {
        foreach (string path in new[] { ParakeetEncoder, ParakeetDecoder, ParakeetJoiner, ParakeetTokens })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Parakeet model file is missing: {path}", path);
            }
        }
    }

    public static void RequireGemma()
    {
        if (!File.Exists(GemmaCleanupModel))
        {
            throw new FileNotFoundException($"Gemma cleanup model is missing: {GemmaCleanupModel}", GemmaCleanupModel);
        }

        if (!File.Exists(LlamaServerExecutable))
        {
            throw new FileNotFoundException(
                $"llama-server executable is missing: {LlamaServerExecutable}", LlamaServerExecutable);
        }
    }
}
