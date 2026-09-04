namespace Optimus.Shell;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Phone;
using Optimus.Inference;

/// <summary>
/// Bridges the phone endpoint to the existing STT and cleanup pipeline.
/// </summary>
/// <remarks>
/// The phone captures; the PC transcribes, cleans and (in a later slice) sends. Audio arrives as
/// 16 kHz mono PCM16 frames, is buffered in memory for the length of one utterance, and is
/// discarded as soon as the draft exists. Nothing is written to disk.
/// </remarks>
public sealed class PhoneSession : IDisposable
{
    private readonly PhoneEndpoint _endpoint;
    private readonly VoicePipeline? _pipeline;
    private readonly Action<string, string, string>? _onDraft;
    private readonly Action<string>? _onStatus;
    private readonly object _lock = new();

    private MemoryStream? _buffer;
    private CancellationTokenSource? _processingCts;
    private int _utteranceGeneration;
    private bool _disposed;

    public PhoneSession(
        PhoneEndpoint endpoint,
        VoicePipeline? pipeline,
        Action<string, string, string>? onDraft = null,
        Action<string>? onStatus = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _pipeline = pipeline;
        _onDraft = onDraft;
        _onStatus = onStatus;

        _endpoint.ConnectionChanged += OnConnectionChanged;
        _endpoint.CaptureStarted += OnCaptureStarted;
        _endpoint.CaptureStopped += OnCaptureStopped;
        _endpoint.AudioReceived += OnAudioReceived;
    }

    private void OnConnectionChanged(object? sender, PhoneConnectionEventArgs e)
    {
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
        }
    }

    private void OnCaptureStarted(object? sender, EventArgs e)
    {
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
                VoicePipelineResult result = await _pipeline.ProcessAsync(pcm, token).ConfigureAwait(false);

                // Same rule as the desktop path: a superseded utterance never overwrites a newer one.
                if (Volatile.Read(ref _utteranceGeneration) != generation)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(result.RawTranscript))
                {
                    _endpoint.SendStatus("idle", "No speech detected");
                    return;
                }

                _endpoint.SendDraft(result.RawTranscript, result.CleanedDraft, result.TimingSummary);
                _endpoint.SendStatus("confirm", "Review the draft");
                _onDraft?.Invoke(result.RawTranscript, result.CleanedDraft, result.TimingSummary);
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
