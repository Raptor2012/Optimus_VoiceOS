namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Speech;
using Optimus.Inference;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Real target-machine measurements of the shipped .NET speech path.
/// </summary>
/// <remarks>
/// Gated on <c>OPTIMUS_TTS_SMOKE=1</c> because it starts the real engine and plays audio through
/// the default output device. The Python benchmark picked the engine; this measures what the app
/// actually does, including process warm-up, segmentation and queued playback.
/// </remarks>
public class SpeechEngineSmokeTests
{
    private readonly ITestOutputHelper _output;

    public SpeechEngineSmokeTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("OPTIMUS_TTS_SMOKE") == "1";

    private const string Draft =
        "Add a retry to the fetchUser function in api.ts, then bump the --timeout flag to 30 seconds in config.yaml.";

    [Fact]
    public void WarmSynthesis_MeetsTheFirstAudioTarget()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set OPTIMUS_TTS_SMOKE=1 to measure the real speech engine.");
            return;
        }

        Assert.True(ModelLocator.TtsAvailable, $"Voice or engine missing: {ModelLocator.TtsVoiceModel}");

        using var synthesizer = new PiperSpeechSynthesizer();

        var warm = Stopwatch.StartNew();
        synthesizer.Warmup();
        warm.Stop();

        _output.WriteLine($"Voice            : {ModelLocator.TtsVoiceModel}");
        _output.WriteLine($"Profile          : {synthesizer.Profile}");
        _output.WriteLine($"Sample rate      : {synthesizer.SampleRate} Hz");
        _output.WriteLine($"Warmup           : {warm.ElapsedMilliseconds} ms");

        // Five warm runs of the review question, which is what the user waits on.
        var ttfa = new List<long>();
        var rtf = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            SpeechSegment segment = synthesizer.Speak("Send this to Claude, or redictate?", CancellationToken.None);
            ttfa.Add(segment.TimeToFirstAudioMs);
            rtf.Add(segment.RealTimeFactor);
        }

        ttfa.Sort();
        long median = ttfa[ttfa.Count / 2];

        _output.WriteLine($"TTFA samples ms  : {string.Join(", ", ttfa)}");
        _output.WriteLine($"TTFA median ms   : {median}");
        _output.WriteLine($"RTF median       : {rtf.OrderBy(x => x).ElementAt(rtf.Count / 2):F3}");

        Assert.True(median < 250, $"Warm time-to-first-audio was {median} ms; target is under 250 ms.");
    }

    [Fact]
    public async Task SpokenReview_MeetsGapAndApprovalReadinessTargets()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set OPTIMUS_TTS_SMOKE=1 to measure the spoken review.");
            return;
        }

        Assert.True(ModelLocator.TtsAvailable, $"Voice or engine missing: {ModelLocator.TtsVoiceModel}");

        using var player = new SpokenReviewPlayer();
        player.Warmup();
        _output.WriteLine($"Warmup           : {player.WarmupMilliseconds} ms");

        SpokenReviewResult result = await player.SpeakReviewAsync(Draft, "Claude");

        Assert.Equal(SpokenReviewOutcome.Completed, result.Outcome);
        Assert.NotNull(result.Timings);

        SpokenReviewTimings timings = result.Timings!;
        _output.WriteLine($"Lines spoken     : {SpokenReviewPlayer.BuildReviewLines(Draft, "Claude").Count}");
        _output.WriteLine($"First audio ms   : {timings.TimeToFirstAudioMs}");
        _output.WriteLine($"Segment gaps ms  : {string.Join(", ", timings.SegmentGapsMs)}");
        _output.WriteLine($"Max synth gap ms : {timings.MaxSegmentGapMs}");
        _output.WriteLine($"Audible gaps     : {timings.AudibleUnderruns}");
        _output.WriteLine($"Total ms         : {timings.TotalMs}");
        _output.WriteLine($"Approval ready ms: {timings.ApprovalReadyMs}");
        _output.WriteLine($"Summary          : {timings.Summary}");

        _output.WriteLine(timings.TimeToFirstAudioMs < 250
            ? "First audio within the 250 ms target."
            : $"NOTE: first audio {timings.TimeToFirstAudioMs} ms, over the 250 ms target.");

        Assert.True(timings.ApprovalReadyMs < 200,
            $"Approval readiness took {timings.ApprovalReadyMs} ms; target is under 200 ms.");

        // The listener hears a gap only if the device ran dry waiting for the next segment.
        Assert.True(timings.PlaybackWasContinuous,
            $"Playback ran dry {timings.AudibleUnderruns} time(s); speech should be continuous.");
    }

    /// <summary>Cancellation must silence the device and leave the player reusable.</summary>
    [Fact]
    public async Task CancellingAReviewStopsPlaybackAndRecovers()
    {
        if (!Enabled)
        {
            _output.WriteLine("Skipped: set OPTIMUS_TTS_SMOKE=1 to exercise cancellation.");
            return;
        }

        using var player = new SpokenReviewPlayer();
        player.Warmup();

        var longDraft = string.Join(" ", Enumerable.Repeat(
            "This is a long sentence that will take a while to speak aloud in full.", 6));

        Task<SpokenReviewResult> speaking = player.SpeakReviewAsync(longDraft, "Claude");
        await Task.Delay(400);
        player.Cancel();

        SpokenReviewResult result = await speaking;
        _output.WriteLine($"Cancelled outcome: {result.Outcome}");
        Assert.Equal(SpokenReviewOutcome.Cancelled, result.Outcome);
        Assert.False(player.IsSpeaking);

        // Still usable afterwards, which is what returns the widget to a working state.
        SpokenReviewResult second = await player.SpeakReviewAsync("Short draft.", "Codex");
        Assert.Equal(SpokenReviewOutcome.Completed, second.Outcome);
    }

    /// <summary>A missing model must fail cleanly, not crash the widget.</summary>
    [Fact]
    public async Task MissingVoiceFailsWithoutThrowing()
    {
        using var synthesizer = new PiperSpeechSynthesizer(
            ModelLocator.PiperExecutable,
            @"D:\definitely\not\a\real\voice.onnx");
        using var player = new SpokenReviewPlayer(synthesizer);

        SpokenReviewResult result = await player.SpeakReviewAsync("Some draft.", "Claude");

        Assert.Equal(SpokenReviewOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureDetail));
        Assert.False(player.IsSpeaking);
    }
}
