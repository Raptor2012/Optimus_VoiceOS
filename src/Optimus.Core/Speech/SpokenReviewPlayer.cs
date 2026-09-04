namespace Optimus.Core.Speech;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Audio;
using Optimus.Inference;

/// <summary>Measured latencies for one spoken review, reported on the widget.</summary>
public sealed record SpokenReviewTimings(
    long TimeToFirstAudioMs,
    long TotalMs,
    long ApprovalReadyMs,
    IReadOnlyList<long> SegmentGapsMs,
    int AudibleUnderruns)
{
    public long MaxSegmentGapMs
    {
        get
        {
            long max = 0;
            foreach (long gap in SegmentGapsMs)
            {
                if (gap > max)
                {
                    max = gap;
                }
            }

            return max;
        }
    }

    /// <summary>
    /// True when playback never ran dry between segments.
    /// </summary>
    /// <remarks>
    /// This, not synthesis latency, is what the listener actually hears. Segments are queued to
    /// the output device, so as long as the next one is ready before the previous finishes
    /// playing, the audio is continuous and the synthesis time is invisible.
    /// </remarks>
    public bool PlaybackWasContinuous => AudibleUnderruns == 0;

    public string Summary => string.Create(
        CultureInfo.InvariantCulture,
        $"speech first audio {TimeToFirstAudioMs} ms · synth gap max {MaxSegmentGapMs} ms · audible gaps {AudibleUnderruns} · total {TotalMs} ms · approval ready +{ApprovalReadyMs} ms");
}

public enum SpokenReviewOutcome
{
    /// <summary>Spoken to the end and the approval chime played.</summary>
    Completed,

    /// <summary>Cancelled part-way; nothing further should happen.</summary>
    Cancelled,

    /// <summary>Speech was unavailable or failed. The draft is still on screen.</summary>
    Failed
}

public sealed record SpokenReviewResult(
    SpokenReviewOutcome Outcome,
    SpokenReviewTimings? Timings,
    string? FailureDetail)
{
    public bool ApprovalMayBegin => Outcome == SpokenReviewOutcome.Completed;
}

/// <summary>
/// Speaks the exact draft and destination, then chimes to mark that approval listening may begin.
/// </summary>
/// <remarks>
/// <para>
/// This class only speaks. It never sends, never confirms and returns no approval decision, so
/// the invariant that nothing is sent merely because speech finished is structural: there is no
/// code path from here to a destination adapter. Spoken approval classification is a separate
/// slice and is deliberately absent.
/// </para>
/// <para>
/// Segments are synthesized one at a time and queued for playback as each becomes available, so
/// audio starts on the first phrase instead of after the whole message. The engine process is
/// kept warm by the caller.
/// </para>
/// </remarks>
public sealed class SpokenReviewPlayer : ISpokenReview, IDisposable
{
    private readonly PiperSpeechSynthesizer _synthesizer;
    private readonly bool _ownsSynthesizer;
    private readonly object _lock = new();

    private WaveOutPlayer? _player;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public SpokenReviewPlayer()
        : this(new PiperSpeechSynthesizer(), ownsSynthesizer: true)
    {
    }

    public SpokenReviewPlayer(PiperSpeechSynthesizer synthesizer, bool ownsSynthesizer = false)
    {
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _ownsSynthesizer = ownsSynthesizer;
    }

    /// <summary>True while audio is being synthesized or played. Capture must stay closed.</summary>
    public bool IsSpeaking { get; private set; }

    public long WarmupMilliseconds { get; private set; }

    /// <summary>Loads the voice and pays first-synthesis cost, off the review path.</summary>
    public void Warmup()
    {
        var stopwatch = Stopwatch.StartNew();
        _synthesizer.Warmup();
        stopwatch.Stop();
        WarmupMilliseconds = stopwatch.ElapsedMilliseconds;
    }

    /// <summary>
    /// Builds the spoken review: the exact draft, the exact destination, then the question.
    /// </summary>
    /// <remarks>
    /// The draft is spoken verbatim. Nothing is paraphrased or re-punctuated, because the whole
    /// point of the review is that what is heard matches what is displayed.
    /// </remarks>
    public static IReadOnlyList<string> BuildReviewLines(string draft, string destinationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);

        var lines = new List<string>();
        lines.AddRange(SpeechSegmenter.Split(draft.Trim()));
        lines.Add($"Destination: {destinationName}.");
        lines.Add($"Send this to {destinationName}, or redictate?");
        return lines;
    }

    /// <summary>Speaks the review. Cancellable; returns why it ended.</summary>
    public Task<SpokenReviewResult> SpeakReviewAsync(
        string draft,
        string destinationName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        CancellationTokenSource linked;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked = _cts;
        }

        return Task.Run(() => SpeakReview(draft, destinationName, linked.Token), linked.Token);
    }

    private SpokenReviewResult SpeakReview(string draft, string destinationName, CancellationToken token)
    {
        IReadOnlyList<string> lines;
        try
        {
            lines = BuildReviewLines(draft, destinationName);
        }
        catch (ArgumentException ex)
        {
            return new SpokenReviewResult(SpokenReviewOutcome.Failed, null, ex.Message);
        }

        var total = Stopwatch.StartNew();
        var gaps = new List<long>();
        long firstAudioMs = -1;
        int underruns = 0;

        IsSpeaking = true;

        try
        {
            WaveOutPlayer player = EnsurePlayer(_synthesizer.SampleRate);

            for (int i = 0; i < lines.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var segmentClock = Stopwatch.StartNew();
                SpeechSegment segment = _synthesizer.Speak(lines[i], token);
                segmentClock.Stop();

                if (i == 0)
                {
                    firstAudioMs = segment.TimeToFirstAudioMs;
                }
                else
                {
                    // Synthesis latency for this segment. It is only audible if the device ran
                    // out of queued audio while waiting for it.
                    gaps.Add(segment.TimeToFirstAudioMs);

                    if (!player.IsPlaying)
                    {
                        underruns++;
                    }
                }

                player.Queue(segment.Pcm, segment.Pcm.Length);
            }

            if (!player.WaitForDrain(token))
            {
                return new SpokenReviewResult(SpokenReviewOutcome.Cancelled, null, null);
            }

            // Playback is genuinely finished; measure the handoff to approval readiness, which
            // is the chime plus the time to notice the device has gone quiet again.
            var handoff = Stopwatch.StartNew();
            byte[] chime = Chime.Build(_synthesizer.SampleRate);
            player.Queue(chime, chime.Length);
            player.WaitForDrain(token);
            handoff.Stop();

            total.Stop();

            var timings = new SpokenReviewTimings(
                firstAudioMs < 0 ? 0 : firstAudioMs,
                total.ElapsedMilliseconds,
                handoff.ElapsedMilliseconds,
                gaps,
                underruns);

            return new SpokenReviewResult(SpokenReviewOutcome.Completed, timings, null);
        }
        catch (OperationCanceledException)
        {
            StopPlayback();
            return new SpokenReviewResult(SpokenReviewOutcome.Cancelled, null, null);
        }
        catch (Exception ex) when (
            ex is System.IO.FileNotFoundException or InvalidOperationException or System.IO.IOException)
        {
            StopPlayback();
            return new SpokenReviewResult(SpokenReviewOutcome.Failed, null, ex.Message);
        }
        finally
        {
            IsSpeaking = false;
        }
    }

    private WaveOutPlayer EnsurePlayer(int sampleRate)
    {
        lock (_lock)
        {
            if (_player == null)
            {
                _player = new WaveOutPlayer(sampleRate);
                _player.Open();
            }

            return _player;
        }
    }

    /// <summary>Cancels speech immediately and silences the device.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }

        StopPlayback();
    }

    private void StopPlayback()
    {
        lock (_lock)
        {
            _player?.Stop();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _player?.Dispose();
            _player = null;
        }

        if (_ownsSynthesizer)
        {
            _synthesizer.Dispose();
        }
    }
}
