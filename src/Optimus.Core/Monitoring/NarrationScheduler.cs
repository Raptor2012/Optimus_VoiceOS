namespace Optimus.Core.Monitoring;

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Optimus.Core.Audio;
using Optimus.Inference;

/// <summary>
/// A queued item to be spoken via Piper TTS.
/// </summary>
public sealed record ResponseNarrationItem(
    string Text,
    string? TurnId = null,
    DateTimeOffset QueuedAt = default);

/// <summary>
/// Queues and coordinates response narrations via the Piper TTS pipeline.
/// Defers playback when STT/user speaking is active, stops playback on user interruption,
/// and feeds the WebRTC echo cancellation bridge during speech output.
/// </summary>
public sealed class NarrationScheduler : IDisposable
{
    private readonly PiperSpeechSynthesizer? _synthesizer;
    private readonly EchoCanceller? _echoCanceller;
    private readonly Func<string, CancellationToken, Task>? _customPlayer;
    private readonly Channel<ResponseNarrationItem> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly object _stateLock = new();

    private WaveOutPlayer? _player;
    private CancellationTokenSource? _activePlaybackCts;
    private bool _disposed;

    /// <summary>
    /// Gets or sets a predicate returning true while the user is actively speaking into the microphone.
    /// Narration is deferred until the user stops speaking.
    /// </summary>
    public Func<bool>? IsUserSpeaking { get; set; }

    /// <summary>
    /// Gets or sets a predicate returning true while the capture system is actively listening.
    /// </summary>
    public Func<bool>? IsListening { get; set; }

    /// <summary>
    /// Gets a value indicating whether narration is currently playing audio.
    /// </summary>
    public bool IsSpeaking { get; private set; }

    /// <summary>
    /// Event raised when a narration item begins playing.
    /// </summary>
    public event Action<ResponseNarrationItem>? NarrationStarted;

    /// <summary>
    /// Event raised when a narration item finishes playing.
    /// </summary>
    public event Action<ResponseNarrationItem>? NarrationCompleted;

    /// <summary>
    /// Initializes a new instance of the <see cref="NarrationScheduler"/> class.
    /// </summary>
    /// <param name="synthesizer">Piper TTS speech synthesizer. Optional in test environments.</param>
    /// <param name="echoCanceller">Echo cancellation bridge to feed rendered playback samples to.</param>
    /// <param name="isUserSpeaking">Callback indicating if user speech/STT is active.</param>
    /// <param name="isListening">Callback indicating if the microphone is currently active.</param>
    /// <param name="customPlayer">Optional custom playback delegate for testing.</param>
    public NarrationScheduler(
        PiperSpeechSynthesizer? synthesizer = null,
        EchoCanceller? echoCanceller = null,
        Func<bool>? isUserSpeaking = null,
        Func<bool>? isListening = null,
        Func<string, CancellationToken, Task>? customPlayer = null)
    {
        _synthesizer = synthesizer;
        _echoCanceller = echoCanceller;
        IsUserSpeaking = isUserSpeaking;
        IsListening = isListening;
        _customPlayer = customPlayer;

        _queue = Channel.CreateUnbounded<ResponseNarrationItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _workerTask = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Queues a response or summary for spoken narration.
    /// </summary>
    /// <param name="text">The response text or summary to speak.</param>
    /// <param name="turnId">Optional associated conversation turn ID.</param>
    public void QueueNarration(string text, string? turnId = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string spokenSummary = SummarizeForSpeech(text);
        if (string.IsNullOrWhiteSpace(spokenSummary)) return;

        var item = new ResponseNarrationItem(spokenSummary, turnId, DateTimeOffset.UtcNow);
        _queue.Writer.TryWrite(item);
    }

    /// <summary>
    /// Immediately interrupts and cancels any active playback and clears pending items.
    /// </summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            _activePlaybackCts?.Cancel();
            _player?.Stop();
        }

        // Drain pending queue items
        while (_queue.Reader.TryRead(out _)) { }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out ResponseNarrationItem? item))
                {
                    if (item == null) continue;

                    // 1. Defer narration while the user is speaking or microphone is capturing
                    await WaitForSilenceAsync(_cts.Token).ConfigureAwait(false);

                    // 2. Play the item
                    await PlayItemAsync(item, _cts.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private async Task WaitForSilenceAsync(CancellationToken cancellationToken)
    {
        while (IsUserSpeaking?.Invoke() == true || IsListening?.Invoke() == true)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PlayItemAsync(ResponseNarrationItem item, CancellationToken shutdownToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        lock (_stateLock)
        {
            _activePlaybackCts = linkedCts;
            IsSpeaking = true;
        }

        NarrationStarted?.Invoke(item);

        try
        {
            if (_customPlayer != null)
            {
                await _customPlayer(item.Text, linkedCts.Token).ConfigureAwait(false);
            }
            else if (_synthesizer != null)
            {
                await PlayWithPiperAsync(item.Text, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Interrupted by user or shutdown
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[NarrationScheduler] Failed to speak response: {ex.Message}");
        }
        finally
        {
            lock (_stateLock)
            {
                IsSpeaking = false;
                if (_activePlaybackCts == linkedCts)
                {
                    _activePlaybackCts = null;
                }
            }
            NarrationCompleted?.Invoke(item);
        }
    }

    private Task PlayWithPiperAsync(string text, CancellationToken token)
    {
        return Task.Run(() =>
        {
            if (_synthesizer == null) return;

            int sampleRate = _synthesizer.SampleRate;
            WaveOutPlayer player = EnsurePlayer(sampleRate);

            // Split into sentences for lower latency first audio
            string[] sentences = Regex.Split(text.Trim(), @"(?<=[.!?])\s+");

            foreach (string sentence in sentences)
            {
                if (string.IsNullOrWhiteSpace(sentence)) continue;
                token.ThrowIfCancellationRequested();

                // If user started speaking during playback, immediately abort
                if (IsUserSpeaking?.Invoke() == true)
                {
                    player.Stop();
                    return;
                }

                SpeechSegment segment = _synthesizer.Speak(sentence.Trim(), token);
                player.Queue(segment.Pcm, segment.Pcm.Length);
            }

            player.WaitForDrain(token);
        }, token);
    }

    private WaveOutPlayer EnsurePlayer(int sampleRate)
    {
        lock (_stateLock)
        {
            if (_player == null)
            {
                _player = new WaveOutPlayer(sampleRate, pcm => _echoCanceller?.RenderPlayback(pcm.Span));
                _player.Open();
            }
            return _player;
        }
    }

    /// <summary>
    /// Reduces a potentially long provider response to a concise 1-3 sentence summary suitable for voice.
    /// </summary>
    public static string SummarizeForSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string normalized = Regex.Replace(text.Trim(), @"\s+", " ");

        // Remove markdown code blocks and excess symbols
        normalized = Regex.Replace(normalized, @"```[\s\S]*?```", "");
        normalized = Regex.Replace(normalized, @"`([^`]+)`", "$1");

        string[] sentences = Regex.Split(normalized.Trim(), @"(?<=[.!?])\s+")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(3)
            .ToArray();

        string result = string.Join(" ", sentences);
        if (result.Length > 300)
        {
            result = result[..297].TrimEnd() + "...";
        }
        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _queue.Writer.TryComplete();
            _activePlaybackCts?.Cancel();

            _player?.Dispose();
            _player = null;
            _cts.Dispose();
        }
    }
}
