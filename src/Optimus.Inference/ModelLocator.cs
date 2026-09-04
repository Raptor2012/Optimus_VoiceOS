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
