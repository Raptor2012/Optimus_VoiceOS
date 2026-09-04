namespace Optimus.Core.Tests;

using System;
using System.Globalization;
using System.Threading;
using Optimus.Core.Audio;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Real-hardware smoke test against the default Windows microphone.
/// </summary>
/// <remarks>
/// Skipped unless <c>OPTIMUS_MIC_SMOKE=1</c>, because it needs an actual capture endpoint and
/// microphone permission. Run with:
/// <code>
/// $env:OPTIMUS_MIC_SMOKE=1; dotnet test .\tests\Optimus.Core.Tests -c Release --filter MicrophoneSmoke
/// </code>
/// </remarks>
public class MicrophoneSmokeTests
{
    private const int CaptureMs = 3000;

    private readonly ITestOutputHelper _output;

    public MicrophoneSmokeTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("OPTIMUS_MIC_SMOKE") == "1";

    [Fact]
    public void MicrophoneSmoke_CapturesCanonical16kMonoPcm16()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set OPTIMUS_MIC_SMOKE=1 to run the real-microphone smoke test.");
            return;
        }

        using var capture = new WasapiAudioCapture();
        string? error = null;
        capture.ErrorOccurred += (_, e) => error = e.Message;

        capture.StartCapture();
        Assert.Null(error);
        Assert.True(capture.IsCapturing, "Capture did not start.");

        _output.WriteLine($"Device mix format : {capture.LastCaptureFormatDescription}");

        Thread.Sleep(CaptureMs);
        byte[] pcm = capture.StopCapture();

        Assert.Null(error);

        int acquired = capture.LastPacketsAcquired;
        int released = capture.LastPacketsReleased;
        int silent = capture.LastSilentPackets;

        _output.WriteLine($"Packets acquired  : {acquired}");
        _output.WriteLine($"Packets released  : {released}");
        _output.WriteLine($"Silent packets    : {silent}");
        _output.WriteLine($"PCM bytes         : {pcm.Length}");

        double seconds = pcm.Length / (double)(AudioResampler.TargetSampleRate * sizeof(short));
        _output.WriteLine(
            "Decoded duration  : " + seconds.ToString("F2", CultureInfo.InvariantCulture) + " s");

        // Every acquired packet was released; a mismatch is the stall bug returning.
        Assert.Equal(acquired, released);
        Assert.True(acquired > 0, "No audio packets were acquired from the microphone.");

        // 16 kHz mono PCM16: two bytes per sample, one channel.
        Assert.True(pcm.Length > 0, "Capture produced no PCM.");
        Assert.Equal(0, pcm.Length % sizeof(short));

        // Duration must match wall-clock capture within a generous tolerance, which only
        // holds if the sample rate really is 16 kHz mono.
        double expected = CaptureMs / 1000.0;
        Assert.InRange(seconds, expected * 0.5, expected * 1.5);

        short peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = BitConverter.ToInt16(pcm, i);
            int magnitude = s == short.MinValue ? short.MaxValue : Math.Abs(s);
            if (magnitude > peak)
            {
                peak = (short)magnitude;
            }
        }

        _output.WriteLine($"Peak amplitude    : {peak} / 32767");
        _output.WriteLine(peak > 300
            ? "Signal present: the microphone picked up audible sound."
            : "NOTE: near-silence captured. The format path is proven; speak during the run to also exercise signal level.");
    }
}
