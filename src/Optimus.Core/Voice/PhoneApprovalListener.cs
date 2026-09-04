namespace Optimus.Core.Voice;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Optimus.Core.Phone;

/// <summary>
/// Captures spoken approval commands or replacement dictation from the connected phone
/// using peak amplitude VAD over PhoneEndpoint incoming audio frames.
/// Never sends anything if the phone disconnects during approval.
/// </summary>
public sealed class PhoneApprovalListener : IApprovalListener, IDisposable
{
    private readonly PhoneEndpoint _phoneEndpoint;
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

    public PhoneApprovalListener(
        PhoneEndpoint phoneEndpoint,
        float speechThreshold = 0.02f,
        TimeSpan? approvalSilenceDuration = null,
        TimeSpan? approvalInitialTimeout = null,
        TimeSpan? approvalMaxDuration = null,
        TimeSpan? dictationSilenceDuration = null,
        TimeSpan? dictationInitialTimeout = null,
        TimeSpan? dictationMaxDuration = null)
    {
        _phoneEndpoint = phoneEndpoint ?? throw new ArgumentNullException(nameof(phoneEndpoint));
        _speechThreshold = speechThreshold;

        _approvalSilenceDuration = approvalSilenceDuration ?? TimeSpan.FromMilliseconds(550);
        _approvalInitialTimeout = approvalInitialTimeout ?? TimeSpan.FromMilliseconds(3500);
        _approvalMaxDuration = approvalMaxDuration ?? TimeSpan.FromMilliseconds(5000);

        _dictationSilenceDuration = dictationSilenceDuration ?? TimeSpan.FromMilliseconds(1100);
        _dictationInitialTimeout = dictationInitialTimeout ?? TimeSpan.FromMilliseconds(5000);
        _dictationMaxDuration = dictationMaxDuration ?? TimeSpan.FromMilliseconds(20000);
    }

    public Task<byte[]> ListenForApprovalAsync(CancellationToken cancellationToken = default) =>
        ListenCoreAsync(
            isRedictation: false,
            _approvalSilenceDuration,
            _approvalInitialTimeout,
            _approvalMaxDuration,
            cancellationToken);

    public Task<byte[]> ListenForReplacementDictationAsync(CancellationToken cancellationToken = default) =>
        ListenCoreAsync(
            isRedictation: true,
            _dictationSilenceDuration,
            _dictationInitialTimeout,
            _dictationMaxDuration,
            cancellationToken);

    private async Task<byte[]> ListenCoreAsync(
        bool isRedictation,
        TimeSpan silenceDuration,
        TimeSpan initialTimeout,
        TimeSpan maxDuration,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource linkedCts;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_phoneEndpoint.IsConnected)
            {
                return Array.Empty<byte>();
            }

            if (_isListening)
            {
                throw new InvalidOperationException("Phone approval listener is already listening.");
            }

            _isListening = true;
            _activeCts?.Dispose();
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts = _activeCts;
        }

        var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pcmStream = new MemoryStream();
        var stopwatch = Stopwatch.StartNew();
        var silenceStopwatch = new Stopwatch();
        bool hasSpeech = false;
        bool disconnected = false;

        void OnAudioReceived(object? sender, PhoneAudioEventArgs e)
        {
            if (e.Pcm == null || e.Pcm.Length == 0)
            {
                return;
            }

            lock (pcmStream)
            {
                pcmStream.Write(e.Pcm, 0, e.Pcm.Length);
            }

            float peak = ComputePeakPcm16(e.Pcm);
            if (peak >= _speechThreshold)
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

        void OnConnectionChanged(object? sender, PhoneConnectionEventArgs e)
        {
            if (!e.Connected)
            {
                disconnected = true;
                doneTcs.TrySetResult(false);
            }
        }

        _phoneEndpoint.AudioReceived += OnAudioReceived;
        _phoneEndpoint.ConnectionChanged += OnConnectionChanged;

        using var checkTimer = new Timer(_ =>
        {
            if (hasSpeech && silenceStopwatch.Elapsed >= silenceDuration)
            {
                doneTcs.TrySetResult(true);
            }
            else if (!hasSpeech && stopwatch.Elapsed >= initialTimeout)
            {
                doneTcs.TrySetResult(true);
            }

            if (stopwatch.Elapsed >= maxDuration)
            {
                doneTcs.TrySetResult(true);
            }
        }, null, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

        try
        {
            if (isRedictation)
            {
                _phoneEndpoint.SendStartRedictationCapture();
            }
            else
            {
                _phoneEndpoint.SendStartApprovalCapture();
            }

            using var reg = linkedCts.Token.Register(() => doneTcs.TrySetCanceled(linkedCts.Token));

            try
            {
                await doneTcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Array.Empty<byte>();
            }

            if (disconnected)
            {
                return Array.Empty<byte>();
            }

            lock (pcmStream)
            {
                return pcmStream.ToArray();
            }
        }
        finally
        {
            try
            {
                _phoneEndpoint.SendStopApprovalCapture();
            }
            catch
            {
            }

            _phoneEndpoint.AudioReceived -= OnAudioReceived;
            _phoneEndpoint.ConnectionChanged -= OnConnectionChanged;

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
            _isListening = false;
        }

        try
        {
            _phoneEndpoint.SendStopApprovalCapture();
        }
        catch
        {
        }
    }

    private static float ComputePeakPcm16(byte[] pcm)
    {
        if (pcm.Length < 2)
        {
            return 0f;
        }

        int count = pcm.Length / 2;
        float maxPeak = 0f;
        for (int i = 0; i < count; i++)
        {
            short sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            float val = Math.Abs(sample) / 32768f;
            if (val > maxPeak)
            {
                maxPeak = val;
            }
        }

        return maxPeak;
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
            _isListening = false;
        }

        try
        {
            _phoneEndpoint.SendStopApprovalCapture();
        }
        catch
        {
        }
    }
}