namespace Optimus.Core.Speech;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Phone;
using Optimus.Inference;

/// <summary>
/// Speaks the draft review and approval prompt on the connected phone via Piper TTS and PhoneEndpoint.
/// Never falls back or silently reroutes to the PC speaker.
/// </summary>
public sealed class PhoneSpokenReview : ISpokenReview, IDisposable
{
    private readonly PiperSpeechSynthesizer? _synthesizer;
    private readonly Func<string, CancellationToken, Task<SpeechSegment>>? _synthFunc;
    private readonly PhoneEndpoint _phoneEndpoint;
    private readonly bool _ownsSynthesizer;
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private long _activeGeneration;
    private bool _disposed;

    public bool IsSpeaking { get; private set; }

    public PhoneSpokenReview(
        PiperSpeechSynthesizer synthesizer,
        PhoneEndpoint phoneEndpoint,
        bool ownsSynthesizer = false)
    {
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _phoneEndpoint = phoneEndpoint ?? throw new ArgumentNullException(nameof(phoneEndpoint));
        _ownsSynthesizer = ownsSynthesizer;
    }

    internal PhoneSpokenReview(
        Func<string, CancellationToken, Task<SpeechSegment>> synthFunc,
        PhoneEndpoint phoneEndpoint)
    {
        _synthFunc = synthFunc ?? throw new ArgumentNullException(nameof(synthFunc));
        _phoneEndpoint = phoneEndpoint ?? throw new ArgumentNullException(nameof(phoneEndpoint));
    }

    public async Task<SpokenReviewResult> SpeakReviewAsync(
        string draft,
        string destinationName,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_phoneEndpoint.IsConnected)
        {
            return new SpokenReviewResult(
                SpokenReviewOutcome.Failed,
                null,
                "Phone disconnected; review was not spoken.");
        }

        CancellationTokenSource linked;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked = _cts;
        }

        CancellationToken token = linked.Token;
        long generation = _phoneEndpoint.NextPlaybackGeneration();
        Volatile.Write(ref _activeGeneration, generation);

        IReadOnlyList<string> lines;
        try
        {
            lines = SpokenReviewPlayer.BuildReviewLines(draft, destinationName);
        }
        catch (ArgumentException ex)
        {
            return new SpokenReviewResult(SpokenReviewOutcome.Failed, null, ex.Message);
        }

        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPlaybackDrained(object? _, PhonePlaybackDrainedEventArgs e)
        {
            if (e.Generation == generation)
            {
                drained.TrySetResult();
            }
        }

        void OnConnectionChanged(object? _, PhoneConnectionEventArgs e)
        {
            if (!e.Connected)
            {
                drained.TrySetException(new InvalidOperationException("Phone disconnected during review playback."));
            }
        }

        _phoneEndpoint.PlaybackDrained += OnPlaybackDrained;
        _phoneEndpoint.ConnectionChanged += OnConnectionChanged;

        IsSpeaking = true;
        var total = Stopwatch.StartNew();
        var gaps = new List<long>();
        long firstAudioMs = -1;

        try
        {
            _phoneEndpoint.SendPlaybackStart(generation);
            int sequence = 0;

            for (int i = 0; i < lines.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var segmentClock = Stopwatch.StartNew();
                SpeechSegment segment = _synthFunc != null
                    ? await _synthFunc(lines[i], token).ConfigureAwait(false)
                    : await Task.Run(() => _synthesizer!.Speak(lines[i], token), token).ConfigureAwait(false);
                segmentClock.Stop();

                if (i == 0)
                {
                    firstAudioMs = segment.TimeToFirstAudioMs;
                }
                else
                {
                    gaps.Add(segment.TimeToFirstAudioMs);
                }

                _phoneEndpoint.SendTtsAudio(new PhoneTtsAudioSegment(
                    generation,
                    sequence++,
                    segment.SampleRate,
                    1,
                    PhonePcmEncoding.Pcm16LittleEndian,
                    segment.Pcm));
            }

            _phoneEndpoint.SendPlaybackChime(generation);
            _phoneEndpoint.SendPlaybackEnd(generation);

            await drained.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
            total.Stop();

            var timings = new SpokenReviewTimings(
                firstAudioMs < 0 ? 0 : firstAudioMs,
                total.ElapsedMilliseconds,
                0,
                gaps,
                0);

            return new SpokenReviewResult(SpokenReviewOutcome.Completed, timings, null);
        }
        catch (OperationCanceledException)
        {
            try { _phoneEndpoint.CancelPlayback(generation); } catch { }
            return new SpokenReviewResult(SpokenReviewOutcome.Cancelled, null, null);
        }
        catch (Exception ex)
        {
            try { _phoneEndpoint.CancelPlayback(generation); } catch { }
            return new SpokenReviewResult(SpokenReviewOutcome.Failed, null, ex.Message);
        }
        finally
        {
            _phoneEndpoint.PlaybackDrained -= OnPlaybackDrained;
            _phoneEndpoint.ConnectionChanged -= OnConnectionChanged;
            IsSpeaking = false;
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
        }

        try
        {
            _phoneEndpoint.CancelPlayback(Volatile.Read(ref _activeGeneration));
        }
        catch
        {
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
        }

        try
        {
            _phoneEndpoint.CancelPlayback(Volatile.Read(ref _activeGeneration));
        }
        catch
        {
        }

        if (_ownsSynthesizer && _synthesizer != null)
        {
            _synthesizer.Dispose();
        }
    }
}