namespace Optimus.Core.Voice;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Audio;

/// <summary>
/// Minimal one-shot listener for approval commands and replacement dictation.
/// Detects speech onset and completes quickly after short silence, bounded by strict timeouts.
/// </summary>
public sealed class OneShotApprovalListener : IApprovalListener, IDisposable
{
    private readonly IAudioCaptureService _captureService;
    private readonly float _speechThreshold;
    private readonly TimeSpan _approvalSilenceDuration;
    private readonly TimeSpan _approvalInitialTimeout;
    private readonly TimeSpan _approvalMaxDuration;

    private readonly TimeSpan _dictationSilenceDuration;
    private readonly TimeSpan _dictationInitialTimeout;
    private readonly TimeSpan _dictationMaxDuration;

    private CancellationTokenSource? _activeCts;
    private readonly object _lock = new();
    private bool _isListening;
    private bool _disposed;

    public bool IsListening
    {
        get
        {
            lock (_lock)
            {
                return _isListening;
            }
        }
        private set
        {
            lock (_lock)
            {
                _isListening = value;
            }
        }
    }

    public OneShotApprovalListener(
        IAudioCaptureService captureService,
        float speechThreshold = 0.02f,
        TimeSpan? approvalSilenceDuration = null,
        TimeSpan? approvalInitialTimeout = null,
        TimeSpan? approvalMaxDuration = null,
        TimeSpan? dictationSilenceDuration = null,
        TimeSpan? dictationInitialTimeout = null,
        TimeSpan? dictationMaxDuration = null)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _speechThreshold = speechThreshold;

        _approvalSilenceDuration = approvalSilenceDuration ?? TimeSpan.FromMilliseconds(550);
        _approvalInitialTimeout = approvalInitialTimeout ?? TimeSpan.FromMilliseconds(3500);
        _approvalMaxDuration = approvalMaxDuration ?? TimeSpan.FromMilliseconds(5000);

        _dictationSilenceDuration = dictationSilenceDuration ?? TimeSpan.FromMilliseconds(1100);
        _dictationInitialTimeout = dictationInitialTimeout ?? TimeSpan.FromMilliseconds(5000);
        _dictationMaxDuration = dictationMaxDuration ?? TimeSpan.FromMilliseconds(20000);
    }

    public Task<byte[]> ListenForApprovalAsync(CancellationToken cancellationToken = default) =>
        ListenCoreAsync(_approvalSilenceDuration, _approvalInitialTimeout, _approvalMaxDuration, cancellationToken);

    public Task<byte[]> ListenForReplacementDictationAsync(CancellationToken cancellationToken = default) =>
        ListenCoreAsync(_dictationSilenceDuration, _dictationInitialTimeout, _dictationMaxDuration, cancellationToken);

    private async Task<byte[]> ListenCoreAsync(
        TimeSpan silenceDuration,
        TimeSpan initialTimeout,
        TimeSpan maxDuration,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource linkedCts;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isListening)
            {
                throw new InvalidOperationException("Approval listener is already listening.");
            }

            _isListening = true;
            _activeCts?.Dispose();
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts = _activeCts;
        }

        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        var silenceStopwatch = new Stopwatch();
        bool hasSpeech = false;

        void OnChunkAvailable(object? sender, AudioChunkEventArgs e)
        {
            if (e.PeakAmplitude >= _speechThreshold)
            {
                hasSpeech = true;
                silenceStopwatch.Restart();
            }
            else
            {
                if (hasSpeech && silenceStopwatch.Elapsed >= silenceDuration)
                {
                    doneTcs.TrySetResult(true);
                }
                else if (!hasSpeech && stopwatch.Elapsed >= initialTimeout)
                {
                    doneTcs.TrySetResult(true);
                }
            }

            if (stopwatch.Elapsed >= maxDuration)
            {
                doneTcs.TrySetResult(true);
            }
        }

        _captureService.AudioChunkAvailable += OnChunkAvailable;

        try
        {
            _captureService.StartCapture();

            using var reg = linkedCts.Token.Register(() => doneTcs.TrySetCanceled(linkedCts.Token));

            try
            {
                await doneTcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<byte>();
            }

            return _captureService.StopCapture();
        }
        finally
        {
            _captureService.AudioChunkAvailable -= OnChunkAvailable;
            if (_captureService.IsCapturing)
            {
                _captureService.StopCapture();
            }

            lock (_lock)
            {
                _isListening = false;
            }
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _activeCts?.Cancel();
            if (_isListening && _captureService.IsCapturing)
            {
                _captureService.StopCapture();
                _isListening = false;
            }
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
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
            if (_isListening && _captureService.IsCapturing)
            {
                _captureService.StopCapture();
                _isListening = false;
            }
        }
    }
}
