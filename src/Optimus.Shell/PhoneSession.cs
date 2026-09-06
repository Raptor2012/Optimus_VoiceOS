namespace Optimus.Shell;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Optimus.Core.Phone;
using Optimus.Inference;
using Optimus.Providers;
using Optimus.Providers.Ao;
using Optimus.Core.Voice;

/// <summary>
/// Bridges the phone endpoint to the existing STT and cleanup pipeline.
/// </summary>
/// <remarks>
/// The phone captures; the PC transcribes, cleans and sends. Audio arrives as
/// 16 kHz mono PCM16 frames, is buffered in memory for the length of one utterance, and is
/// discarded as soon as the draft exists. Nothing is written to disk.
/// </remarks>
public sealed class PhoneSession : IDisposable
{
    private readonly PhoneEndpoint _endpoint;
    private readonly VoicePipeline? _pipeline;
    private readonly DestinationRegistry? _destinations;
    private readonly AoProjectBridge? _aoBridge;
    private readonly IAoClient? _aoClient;
    private readonly Action<string, string, string>? _onDraft;
    private readonly Action<string>? _onStatus;
    private readonly Action? _onCancel;
    private readonly Action<string, string, string>? _onSending;
    private readonly Action<string, string, SendResult>? _onSendCompleted;
    private readonly VoiceDestinationResolver? _destinationResolver;
    private readonly Action<string, string, string, string?>? _onDraftWithDestination;
    private readonly Action<string>? _onDestinationSelected;
    private readonly Action<string>? _onDraftEdited;
    private readonly object _lock = new();
    public Action<byte[]>? ProcessCapturedAudio { get; set; }
    public Action? CaptureBeginning { get; set; }

    /// <summary>
    /// Raised when a capture the phone announced can no longer complete, because the phone went
    /// away before sending its audio.
    /// </summary>
    /// <remarks>
    /// Without this the widget waits on a device that is gone: the phone signals capture start,
    /// drops, and nothing ever moves the widget out of the listening state.
    /// </remarks>
    public Action? CaptureAbandoned { get; set; }

    private MemoryStream? _buffer;
    private CancellationTokenSource? _processingCts;
    private int _utteranceGeneration;
    private long _activeConnectionCursor;
    private int _sendInProgress;
    private bool _disposed;

    public PhoneSession(
        PhoneEndpoint endpoint,
        VoicePipeline? pipeline,
        DestinationRegistry? destinations = null,
        Action<string, string, string>? onDraft = null,
        Action<string>? onStatus = null,
        Action? onCancel = null,
        Action<string, string, string>? onSending = null,
        Action<string, string, SendResult>? onSendCompleted = null,
        VoiceDestinationResolver? destinationResolver = null,
        Action<string, string, string, string?>? onDraftWithDestination = null,
        Action<string>? onDestinationSelected = null,
        Action<string>? onDraftEdited = null,
        AoProjectBridge? aoBridge = null,
        IAoClient? aoClient = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _pipeline = pipeline;
        _destinations = destinations;
        _aoClient = aoClient;
        _aoBridge = aoBridge ?? (aoClient != null ? new AoProjectBridge(aoClient) : null);
        _onDraft = onDraft;
        _onStatus = onStatus;
        _onCancel = onCancel;
        _onSending = onSending;
        _onSendCompleted = onSendCompleted;
        _destinationResolver = destinationResolver ?? (destinations != null ? CreateResolver(destinations) : null);
        _onDraftWithDestination = onDraftWithDestination;
        _onDestinationSelected = onDestinationSelected;
        _onDraftEdited = onDraftEdited;

        _endpoint.ConnectionChanged += OnConnectionChanged;
        _endpoint.CaptureStarted += OnCaptureStarted;
        _endpoint.CaptureStopped += OnCaptureStopped;
        _endpoint.AudioReceived += OnAudioReceived;
        _endpoint.DestinationsRequested += OnDestinationsRequested;
        _endpoint.ProjectsRequested += OnProjectsRequested;
        _endpoint.ConfirmRequested += OnConfirmRequested;
        _endpoint.CancelRequested += OnCancelRequested;
        _endpoint.DestinationSelected += OnDestinationSelected;
        _endpoint.DraftEdited += OnDraftEdited;
        _endpoint.ApprovalResolved += OnApprovalResolved;
    }

    private void OnDraftEdited(object? sender, string text) =>
        _onDraftEdited?.Invoke(text);

    private void OnDestinationSelected(object? sender, string destinationId) =>
        _onDestinationSelected?.Invoke(destinationId);

    private static VoiceDestinationResolver? CreateResolver(DestinationRegistry destinations)
    {
        var aliases = new List<VoiceDestinationAlias>();
        foreach ((IDestinationAdapter adapter, _) in destinations.ProbeAll())
        {
            aliases.Add(new VoiceDestinationAlias(adapter.DestinationId, adapter.DisplayName));
            if (!string.Equals(adapter.DestinationId, adapter.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                aliases.Add(new VoiceDestinationAlias(adapter.DestinationId, adapter.DestinationId));
            }
        }
        return aliases.Count > 0 ? new VoiceDestinationResolver(aliases) : null;
    }

    private void OnDestinationsRequested(object? sender, EventArgs e) => PushDestinations();

    private void OnProjectsRequested(object? sender, EventArgs e) => _ = PushProjectsAsync();

    /// <summary>Pushes the live AO projects and sessions to the phone.</summary>
    public async Task PushProjectsAsync(CancellationToken cancellationToken = default)
    {
        if (_aoBridge != null)
        {
            try
            {
                var cards = await _aoBridge.GetLiveProjectCardsAsync(cancellationToken).ConfigureAwait(false);
                if (cards.Count > 0)
                {
                    _endpoint.SendProjects(cards);
                    return;
                }
            }
            catch
            {
                // Fall back to simple projects if bridge fails
            }
        }

        if (_aoClient != null)
        {
            try
            {
                var projects = await _aoClient.GetProjectsAsync(cancellationToken).ConfigureAwait(false);
                _endpoint.SendProjects(projects);
            }
            catch
            {
                // Daemon might not be running or reachable
            }
        }
    }

    /// <summary>Sends the destination list with live readiness, so the phone shows the truth.</summary>
    public void PushDestinations()
    {
        if (_destinations == null)
        {
            _endpoint.SendDestinations(Array.Empty<PhoneDestination>());
            return;
        }

        List<PhoneDestination> list = _destinations.ProbeAll()
            .Select(entry => new PhoneDestination(
                entry.Adapter.DestinationId,
                entry.Adapter.DisplayName,
                entry.Status.CanSend,
                entry.Status.Detail))
            .ToList();

        _endpoint.SendDestinations(list);
    }

    private void OnCancelRequested(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _utteranceGeneration);
        Interlocked.Exchange(ref _sendInProgress, 0);
        _endpoint.SendStatus("idle", "Cancelled");
        _onCancel?.Invoke();
        _onStatus?.Invoke("Phone cancelled the draft");
    }

    /// <summary>
    /// Sends exactly the text the phone displayed, to exactly the destination it named.
    /// </summary>
    /// <remarks>
    /// The text comes from the phone rather than from this process's copy of the draft, because
    /// the user may have edited it there and the rule is that what was visible is what is sent.
    /// Nothing is substituted: an unknown or unbound destination fails and says why.
    /// </remarks>
    private void OnConfirmRequested(object? sender, PhoneConfirmEventArgs e)
    {
        if (_destinations == null)
        {
            _endpoint.SendSendResult(false, e.DestinationId, "No destinations are configured on the PC.");
            return;
        }

        IDestinationAdapter? adapter = _destinations.Find(e.DestinationId);
        if (adapter == null)
        {
            _endpoint.SendSendResult(false, e.DestinationId, "That destination does not exist.");
            return;
        }

        DestinationStatus status = adapter.Probe();
        if (!status.CanSend)
        {
            _endpoint.SendSendResult(false, adapter.DisplayName, status.Detail);
            PushDestinations();
            return;
        }

        // A double tap or duplicate frame must not submit the same phone draft twice.
        if (Interlocked.CompareExchange(ref _sendInProgress, 1, 0) != 0)
        {
            // Keep the phone in Sending; a false outcome here would make the original live send
            // look failed and re-enable confirmation before its real result arrives.
            _endpoint.SendStatus("sending", "A send is already in progress.");
            return;
        }

        _endpoint.SendStatus("sending", $"Sending to {adapter.DisplayName}...");
        _onSending?.Invoke(e.Text, adapter.DestinationId, adapter.DisplayName);
        _onStatus?.Invoke($"Phone confirmed: sending to {adapter.DisplayName}");

        _ = Task.Run(async () =>
        {
            SendResult result;
            try
            {
                var confirmed = new ConfirmedDraft(e.Text, adapter.DestinationId);
                result = await adapter.SendAsync(confirmed).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new SendResult(SendStatus.Failed, ex.Message, 0);
            }

            // A failure remains retryable. Success stays consumed until a new capture starts.
            if (!result.Succeeded)
            {
                Interlocked.Exchange(ref _sendInProgress, 0);
            }

            // Sent is reported only when the adapter actually succeeded.
            _onSendCompleted?.Invoke(e.Text, adapter.DisplayName, result);
            _endpoint.SendSendResult(result.Succeeded, adapter.DisplayName, result.Detail);
            _endpoint.SendStatus(result.Succeeded ? "sent" : "error", result.Detail);
            _onStatus?.Invoke(result.Succeeded
                ? $"Phone prompt sent to {adapter.DisplayName}"
                : $"Phone prompt NOT sent: {result.Detail}");
        });
    }

    /// <summary>
    /// Notifies the phone that a send has begun on the PC (via spoken confirmation or button).
    /// Sets the send guard to prevent double sends from the phone and updates phone status.
    /// </summary>
    public void NotifySending(string text, string destinationId, string destinationName)
    {
        Interlocked.Exchange(ref _sendInProgress, 1);
        _endpoint.SendStatus("sending", $"Sending to {destinationName}...");
    }

    /// <summary>
    /// Notifies the phone that a PC-initiated send has completed, clearing the phone draft on success.
    /// </summary>
    public void NotifySendCompleted(string text, string destinationName, SendResult result)
    {
        if (!result.Succeeded)
        {
            Interlocked.Exchange(ref _sendInProgress, 0);
        }

        _endpoint.SendSendResult(result.Succeeded, destinationName, result.Detail);
        _endpoint.SendStatus(result.Succeeded ? "sent" : "error", result.Detail);
        _onStatus?.Invoke(result.Succeeded
            ? $"Prompt sent to {destinationName}"
            : $"Prompt NOT sent: {result.Detail}");
    }

    /// <summary>
    /// Notifies the phone that the draft was cancelled on PC.
    /// </summary>
    public void NotifyCancelled()
    {
        Interlocked.Exchange(ref _sendInProgress, 0);
        _endpoint.SendStatus("idle", "Cancelled");
    }

    private void OnConnectionChanged(object? sender, PhoneConnectionEventArgs e)
    {
        lock (_lock)
        {
            // The old socket can report its finally-block after a replacement connection has
            // already been accepted. Never let that stale disconnect clear the new utterance.
            if (e.Cursor > 0 && e.Cursor < _activeConnectionCursor)
            {
                return;
            }

            if (e.Cursor > 0)
            {
                _activeConnectionCursor = e.Cursor;
            }
        }

        lock (_lock)
        {
            // A reconnect abandons any half-captured utterance rather than splicing it onto
            // whatever the phone sends next.
            _buffer?.Dispose();
            _buffer = null;
            Interlocked.Increment(ref _utteranceGeneration);
        }

        _onStatus?.Invoke(e.Connected ? $"Phone connected ({e.Remote})" : "Phone disconnected");

        if (e.Connected)
        {
            _endpoint.SendStatus("idle", "Connected to PC");
            PushDestinations();
            _ = PushProjectsAsync();
        }
        else
        {
            CaptureAbandoned?.Invoke();
        }
    }

    private void OnCaptureStarted(object? sender, EventArgs e)
    {
        CaptureBeginning?.Invoke();
        Interlocked.Exchange(ref _sendInProgress, 0);
        lock (_lock)
        {
            _processingCts?.Cancel();
            _buffer?.Dispose();
            _buffer = new MemoryStream();
            Interlocked.Increment(ref _utteranceGeneration);
        }

        _endpoint.SendStatus("listening", "Listening...");
        _onStatus?.Invoke("Phone is listening");
    }

    private void OnAudioReceived(object? sender, PhoneAudioEventArgs e)
    {
        lock (_lock)
        {
            _buffer?.Write(e.Pcm, 0, e.Pcm.Length);
        }
    }

    private void OnCaptureStopped(object? sender, EventArgs e)
    {
        byte[] pcm;
        int generation;

        lock (_lock)
        {
            if (_buffer == null || _buffer.Length == 0)
            {
                _endpoint.SendStatus("idle", "No audio captured");
                return;
            }

            pcm = _buffer.ToArray();
            _buffer.Dispose();
            _buffer = null;

            _processingCts?.Dispose();
            _processingCts = new CancellationTokenSource();
            generation = Interlocked.Increment(ref _utteranceGeneration);
        }

        double seconds = pcm.Length / 32000.0;
        if (ProcessCapturedAudio != null)
        {
            ProcessCapturedAudio(pcm);
            return;
        }

        if (_pipeline == null)
        {
            _endpoint.SendStatus("idle", $"Captured {seconds:F1}s (no models loaded)");
            return;
        }

        _endpoint.SendStatus("processing", $"Transcribing {seconds:F1}s...");
        _onStatus?.Invoke($"Phone utterance: transcribing {seconds:F1}s");

        CancellationToken token;
        lock (_lock)
        {
            token = _processingCts!.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
                TranscriptionResult transcription = _pipeline.TranscribeOnly(pcm, token);

                // Same rule as the desktop path: a superseded utterance never overwrites a newer one.
                if (Volatile.Read(ref _utteranceGeneration) != generation)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(transcription.Text))
                {
                    _endpoint.SendStatus("idle", "No speech detected");
                    return;
                }

                string textToClean = transcription.Text;
                string? targetDestinationId = null;

                if (_destinationResolver != null)
                {
                    VoiceDestinationResolution resolution = _destinationResolver.Resolve(transcription.Text);
                    if (resolution.Status == VoiceDestinationResolutionStatus.Resolved && resolution.DestinationId != null)
                    {
                        targetDestinationId = resolution.DestinationId;
                        textToClean = resolution.PromptText;
                        _onDestinationSelected?.Invoke(targetDestinationId);
                    }
                }

                token.ThrowIfCancellationRequested();

                CleanupResult cleanup;
                if (!string.IsNullOrWhiteSpace(textToClean))
                {
                    try
                    {
                        cleanup = await _pipeline.Cleaner.CleanAsync(textToClean, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        cleanup = new CleanupResult(textToClean, 0, false, ex.Message);
                    }
                }
                else
                {
                    cleanup = new CleanupResult(string.Empty, 0, false, "Empty prompt");
                }

                totalStopwatch.Stop();

                if (Volatile.Read(ref _utteranceGeneration) != generation)
                {
                    return;
                }

                string timings = $"audio {seconds:F1}s · STT {transcription.ElapsedMilliseconds} ms · cleanup {cleanup.ElapsedMilliseconds} ms · total {totalStopwatch.ElapsedMilliseconds} ms";

                _endpoint.SendDraft(textToClean, cleanup.Text, timings, targetDestinationId);
                _endpoint.SendStatus("confirm", "Review the draft");
                if (_onDraftWithDestination != null)
                {
                    _onDraftWithDestination.Invoke(textToClean, cleanup.Text, timings, targetDestinationId);
                }
                else
                {
                    _onDraft?.Invoke(textToClean, cleanup.Text, timings);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (Volatile.Read(ref _utteranceGeneration) == generation)
                {
                    _endpoint.SendError(ex.Message);
                }
            }
        }, token);
    }

    private void OnApprovalResolved(object? sender, PhoneApprovalResolutionEventArgs e)
    {
        if (_aoClient == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _aoClient.ResolveApprovalAsync(e.SessionId, e.RequestId, e.DecisionId).ConfigureAwait(false);
            }
            catch { }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _endpoint.ConnectionChanged -= OnConnectionChanged;
        _endpoint.CaptureStarted -= OnCaptureStarted;
        _endpoint.CaptureStopped -= OnCaptureStopped;
        _endpoint.AudioReceived -= OnAudioReceived;
        _endpoint.DestinationsRequested -= OnDestinationsRequested;
        _endpoint.ProjectsRequested -= OnProjectsRequested;
        _endpoint.ConfirmRequested -= OnConfirmRequested;
        _endpoint.CancelRequested -= OnCancelRequested;
        _endpoint.DestinationSelected -= OnDestinationSelected;
        _endpoint.DraftEdited -= OnDraftEdited;
        _endpoint.ApprovalResolved -= OnApprovalResolved;

        lock (_lock)
        {
            _buffer?.Dispose();
            _buffer = null;
            _processingCts?.Cancel();
            _processingCts?.Dispose();
            _processingCts = null;
        }
    }
}
