namespace Optimus.Inference.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using Optimus.Inference;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// End-to-end S002 smoke test over the real Parakeet and Qwen runtimes.
/// </summary>
/// <remarks>
/// Gated on <c>OPTIMUS_MODEL_SMOKE=1</c> so the ordinary test run stays fast and does not
/// require ~1.2 GB of weights. Run with:
/// <code>
/// $env:OPTIMUS_MODEL_SMOKE=1; dotnet test .\tests\Optimus.Inference.Tests -c Release
/// </code>
/// </remarks>
public class VoicePipelineSmokeTests
{
    private readonly ITestOutputHelper _output;

    public VoicePipelineSmokeTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("OPTIMUS_MODEL_SMOKE") == "1";

    [Fact]
    public async Task Pipeline_TranscribesAndCleansSampleUtterance()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set OPTIMUS_MODEL_SMOKE=1 to run the model smoke test.");
            return;
        }

        Assert.True(ModelLocator.ParakeetAvailable, $"Parakeet model missing under {ModelLocator.ParakeetDirectory}");
        Assert.True(ModelLocator.GemmaAvailable, $"Gemma model or llama-server missing ({ModelLocator.GemmaCleanupModel})");
        Assert.True(File.Exists(ModelLocator.ParakeetSampleWav), $"Sample wav missing: {ModelLocator.ParakeetSampleWav}");

        byte[] pcm = ReadPcm16MonoWav(ModelLocator.ParakeetSampleWav, out int sampleRate);
        _output.WriteLine($"Sample wav        : {sampleRate} Hz, {pcm.Length} bytes");
        Assert.Equal(16000, sampleRate);

        using var pipeline = new VoicePipeline();

        var warmupStart = DateTime.UtcNow;
        pipeline.Warmup();
        _output.WriteLine($"Model load        : {(DateTime.UtcNow - warmupStart).TotalMilliseconds:F0} ms");
        Assert.True(pipeline.IsWarm);

        VoicePipelineResult result = await pipeline.ProcessAsync(pcm);

        _output.WriteLine($"Raw transcript    : {result.RawTranscript}");
        _output.WriteLine($"Cleaned draft     : {result.CleanedDraft}");
        _output.WriteLine($"Timings           : {result.TimingSummary}");
        _output.WriteLine($"Real-time factor  : {result.RealTimeFactor:F3}");
        _output.WriteLine($"Cleanup applied   : {result.CleanupApplied} {result.CleanupUnavailableReason}");

        Assert.False(string.IsNullOrWhiteSpace(result.RawTranscript), "Parakeet produced no transcript.");
        Assert.False(string.IsNullOrWhiteSpace(result.CleanedDraft), "Cleanup produced no draft.");
        Assert.True(result.TranscribeMilliseconds > 0);
        Assert.True(result.TotalMilliseconds >= result.TranscribeMilliseconds);
    }

    /// <summary>Minimal RIFF/WAVE reader for 16-bit mono PCM test fixtures.</summary>
    private static byte[] ReadPcm16MonoWav(string path, out int sampleRate)
    {
        byte[] file = File.ReadAllBytes(path);

        if (file.Length < 12 ||
            System.Text.Encoding.ASCII.GetString(file, 0, 4) != "RIFF" ||
            System.Text.Encoding.ASCII.GetString(file, 8, 4) != "WAVE")
        {
            throw new InvalidDataException($"Not a RIFF/WAVE file: {path}");
        }

        sampleRate = 0;
        int position = 12;

        while (position + 8 <= file.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(file, position, 4);
            int chunkSize = BitConverter.ToInt32(file, position + 4);
            int body = position + 8;

            if (chunkId == "fmt ")
            {
                sampleRate = BitConverter.ToInt32(file, body + 4);
            }
            else if (chunkId == "data")
            {
                int length = Math.Min(chunkSize, file.Length - body);
                byte[] pcm = new byte[length];
                Array.Copy(file, body, pcm, 0, length);
                return pcm;
            }

            position = body + chunkSize + (chunkSize % 2);
        }

        throw new InvalidDataException($"No data chunk in {path}");
    }
}
