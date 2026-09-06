namespace Optimus.Shell;

using System;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Audio;
using Optimus.Core.Narration;
using Optimus.Core.Phone;
using Optimus.Inference;
using Optimus.Providers;
using Optimus.Providers.Windows;

/// <summary>Connects one exact bound agent window to filtered, ordered local speech.</summary>
public sealed class AgentNarrationCoordinator : IDisposable
{
    private readonly PiperSpeechSynthesizer _synthesizer;
    private readonly PhoneEndpoint? _phone;
    private readonly Func<NarrationOptions> _options;
    private readonly Action<string> _status;
    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private WaveOutPlayer? _pcPlayer;
    private long _generation;
    private long _activeGeneration;
    private bool _disposed;
    private volatile bool _muted;

    /// <summary>
    /// True while narration audio is actually coming out of this PC's speakers.
    /// </summary>
    /// <remarks>
    /// The continuous session reads this to keep the microphone closed while the tool is
    /// talking, so it never transcribes its own output.
    /// </remarks>
    public bool IsSpeakingOnPc => _pcPlayer?.IsPlaying == true;

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (muted) Cancel();
    }

    public AgentNarrationCoordinator(
        PiperSpeechSynthesizer synthesizer,
        PhoneEndpoint? phone,
        Func<NarrationOptions> options,
        Action<string> status)
    {
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _phone = phone;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _status = status ?? throw new ArgumentNullException(nameof(status));
    }

    /// <summary>Baselines and starts observing before the prompt is submitted.</summary>
    public void Start(IDestinationAdapter adapter, bool speakOnPhone, string confirmedPrompt)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        IAgentObserver observer;
        if (adapter is IObservableDestinationAdapter observable)
        {
            observer = observable.CreateObserver();
        }
        else if (adapter is WindowsAppAdapter windows)
        {
            observer = windows.CreateObserver();
        }
        else
        {
            throw new InvalidOperationException($"Narration is not supported for {adapter.DisplayName}.");
        }

        Cancel();
        if (_muted) return;

        var cts = new CancellationTokenSource();
        long generation = _phone != null
            ? _phone.NextPlaybackGeneration()
            : Interlocked.Increment(ref _generation);
        lock (_gate)
        {
            _runCts = cts;
            _activeGeneration = generation;
        }

        _status($"Watching {adapter.DisplayName} for visible updates");
        _ = Task.Run(() => RunAsync(observer, adapter.DisplayName, speakOnPhone, confirmedPrompt.Trim(), generation, cts.Token));
    }

    public void Cancel()
    {
        CancellationTokenSource? old;
        long gen;
        lock (_gate)
        {
            old = _runCts;
            _runCts = null;
            gen = _activeGeneration;
        }

        old?.Cancel();
        old?.Dispose();
        _pcPlayer?.Stop();
        if (_phone?.IsConnected == true && gen > 0)
        {
            try { _phone.CancelPlayback(gen); }
            catch (InvalidOperationException) { }
        }
    }

    private async Task RunAsync(
        IAgentObserver observer,
        string destinationName,
        bool speakOnPhone,
        string confirmedPrompt,
        long generation,
        CancellationToken token)
    {
        // Allow the target window to settle after prompt submission, then capture baseline on background thread.
        await Task.Delay(200, token).ConfigureAwait(false);
        for (int retry = 0; retry < 5; retry++)
        {
            observer.Poll();
            if (observer.CapturedNodeCount > 0)
            {
                break;
            }
            await Task.Delay(100, token).ConfigureAwait(false);
        }

        string runId = generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var scheduler = new NarrationScheduler(_options());
        using var observeCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        Task observe = Task.Run(async () =>
        {
            try
            {
                await foreach (VisibleAgentUpdate update in observer.ObserveAsync(TimeSpan.FromMilliseconds(180), observeCts.Token))
                {
                    // Sending makes the user's own prompt appear in the conversation. It is not
                    // agent activity and must not be echoed back as Comprehensive narration.
                    string trimmed = update.Text.Trim();
                    VisibleAgentUpdate actualUpdate = update;
                    if (!string.IsNullOrEmpty(confirmedPrompt))
                    {
                        if (string.Equals(trimmed, confirmedPrompt, StringComparison.OrdinalIgnoreCase) ||
                            (trimmed.Length <= confirmedPrompt.Length + 15 && trimmed.Contains(confirmedPrompt, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        if (trimmed.StartsWith(confirmedPrompt, StringComparison.OrdinalIgnoreCase))
                        {
                            string remainder = trimmed[confirmedPrompt.Length..].Trim();
                            if (remainder.Length == 0)
                            {
                                continue;
                            }
                            actualUpdate = update with { Text = remainder };
                        }
                    }

                    scheduler.Options = _options();
                    scheduler.Enqueue(ToNarrationEvent(runId, actualUpdate));
                }
            }
            catch (OperationCanceledException) { }
        }, token);

        try
        {
            await foreach (SpeakableItem item in scheduler.GetSpeakableStreamAsync(token))
            {
                _status($"{destinationName}: {item.Text}");
                SpeechSegment segment = await Task.Run(
                    () => _synthesizer.Speak(item.Text, token), token).ConfigureAwait(false);

                if (speakOnPhone)
                {
                    await PlayOnPhoneAsync(segment, generation, token).ConfigureAwait(false);
                }
                else
                {
                    PlayOnPc(segment, token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            _status($"Narration stopped: {ex.Message}");
        }
        finally
        {
            observeCts.Cancel();
            try { await observe.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    internal static NarrationEvent ToNarrationEvent(string runId, VisibleAgentUpdate update)
    {
        NarrationEventType type = update.Activity switch
        {
            VisibleAgentActivity.Progress => NarrationEventType.Progress,
            VisibleAgentActivity.ToolOrSkill when update.Text.Contains("skill", StringComparison.OrdinalIgnoreCase) => NarrationEventType.SkillUse,
            VisibleAgentActivity.ToolOrSkill => NarrationEventType.ToolCall,
            VisibleAgentActivity.FinalResponseCandidate => NarrationEventType.FinalResponse,
            _ when update.Text.TrimEnd().EndsWith('?') => NarrationEventType.AgentQuestion,
            VisibleAgentActivity.VisibleText => NarrationEventType.FinalResponse,
            _ => NarrationEventType.FinalResponse
        };

        // Thought headlines and high-value transitions remain audible in Concise mode.
        if (type == NarrationEventType.Progress && ContainsTransition(update.Text))
        {
            type = NarrationEventType.StatusTransition;
        }

        return new NarrationEvent(
            runId,
            type,
            update.Text,
            update.ObservedAtUtc,
            isStreamingFragment: type == NarrationEventType.Progress);
    }

    private static bool ContainsTransition(string text)
    {
        string cleaned = text.Trim().TrimStart('>', '*', '-', '•', '[', '(', ' ', '#');
        if (cleaned.StartsWith("thought", StringComparison.OrdinalIgnoreCase) ||
            cleaned.StartsWith("thinking", StringComparison.OrdinalIgnoreCase) ||
            cleaned.StartsWith("interpreting", StringComparison.OrdinalIgnoreCase) ||
            cleaned.StartsWith("planning", StringComparison.OrdinalIgnoreCase) ||
            cleaned.StartsWith("reasoning", StringComparison.OrdinalIgnoreCase) ||
            cleaned.StartsWith("analyzing", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("running tests", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("tests passed", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("tests failed", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("build succeeded", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("build failed", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            cleaned.StartsWith("completed ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string line in text.Split('\n'))
        {
            string lineClean = line.Trim().TrimStart('>', '*', '-', '•', '[', '(', ' ', '#');
            if (lineClean.StartsWith("thought", StringComparison.OrdinalIgnoreCase) ||
                lineClean.StartsWith("thinking", StringComparison.OrdinalIgnoreCase) ||
                lineClean.StartsWith("interpreting", StringComparison.OrdinalIgnoreCase) ||
                lineClean.StartsWith("planning", StringComparison.OrdinalIgnoreCase) ||
                lineClean.StartsWith("reasoning", StringComparison.OrdinalIgnoreCase) ||
                lineClean.StartsWith("analyzing", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void PlayOnPc(SpeechSegment segment, CancellationToken token)
    {
        _pcPlayer ??= new WaveOutPlayer(segment.SampleRate);
        if (!_pcPlayer.IsOpen) _pcPlayer.Open();
        _pcPlayer.Queue(segment.Pcm, segment.Pcm.Length);
        _pcPlayer.WaitForDrain(token);
    }

    private async Task PlayOnPhoneAsync(SpeechSegment segment, long generation, CancellationToken token)
    {
        if (_phone?.IsConnected != true)
        {
            throw new InvalidOperationException("Phone disconnected; narration was not rerouted.");
        }

        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDrained(object? _, PhonePlaybackDrainedEventArgs e)
        {
            if (e.Generation == generation) drained.TrySetResult();
        }

        _phone.PlaybackDrained += OnDrained;
        try
        {
            _phone.SendPlaybackStart(generation);
            _phone.SendTtsAudio(new PhoneTtsAudioSegment(
                generation, 0, segment.SampleRate, 1,
                PhonePcmEncoding.Pcm16LittleEndian, segment.Pcm));
            _phone.SendPlaybackEnd(generation);
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
        }
        finally
        {
            _phone.PlaybackDrained -= OnDrained;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _pcPlayer?.Dispose();
    }
}
