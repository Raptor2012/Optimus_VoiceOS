namespace Optimus.Core.Audio;

using System;
using System.Diagnostics;
using Optimus.Core.Hotkeys;

public sealed class PushToTalkController : IDisposable
{
    /// <summary>
    /// A press shorter than this is a tap, not a hold, and carries no dictation.
    /// </summary>
    /// <remarks>
    /// The two gestures share one key: holding speaks, tapping ends an open continuous session.
    /// 350 ms sits well above an intentional tap and well below the shortest useful hold, so the
    /// audio discarded on a tap is only the fragment recorded before the user let go.
    /// </remarks>
    public static readonly TimeSpan DefaultTapThreshold = TimeSpan.FromMilliseconds(350);

    private readonly IHotkeyService _hotkeyService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly object _lock = new();
    private readonly Stopwatch _pressDuration = new();
    private readonly TimeSpan _tapThreshold;
    private bool _isCapturing;
    private bool _disposed;

    public bool IsCapturing
    {
        get
        {
            lock (_lock)
            {
                return _isCapturing;
            }
        }
    }

    public byte[]? LastCapturedAudio { get; private set; }

    public IAudioCaptureService AudioCaptureService => _audioCaptureService;

    public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;
    public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;
    public event EventHandler<byte[]>? AudioCaptured;

    /// <summary>Raised when the key was pressed and released too quickly to be dictation.</summary>
    public event EventHandler? Tapped;

    /// <summary>
    /// Raised immediately before the hotkey claims the microphone, so anything already listening
    /// on the shared capture device can release it first.
    /// </summary>
    public event EventHandler? CapturePreparing;

    /// <param name="tapThreshold">
    /// Presses shorter than this are taps rather than dictation. Pass <see cref="TimeSpan.Zero"/>
    /// to treat every press as a hold, which is what a test simulating instant key events wants.
    /// </param>
    public PushToTalkController(
        IHotkeyService hotkeyService,
        IAudioCaptureService audioCaptureService,
        TimeSpan? tapThreshold = null)
    {
        _tapThreshold = tapThreshold ?? DefaultTapThreshold;
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _audioCaptureService = audioCaptureService ?? throw new ArgumentNullException(nameof(audioCaptureService));

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += OnHotkeyReleased;
        _hotkeyService.RegistrationFailed += OnHotkeyRegistrationFailed;

        _audioCaptureService.StateChanged += OnAudioStateChanged;
        _audioCaptureService.ErrorOccurred += OnAudioErrorOccurred;
    }

    public void Start()
    {
        _hotkeyService.Register();
    }

    public void Stop()
    {
        _hotkeyService.Unregister();
        if (_isCapturing)
        {
            _audioCaptureService.StopCapture();
            _isCapturing = false;
        }
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_isCapturing || _disposed)
            {
                return;
            }

            _isCapturing = true;
            _pressDuration.Restart();
            CapturePreparing?.Invoke(this, EventArgs.Empty);
            try
            {
                _audioCaptureService.StartCapture();
            }
            catch (Exception ex)
            {
                _isCapturing = false;
                ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs($"Failed to start audio capture: {ex.Message}", ex));
            }
        }
    }

    private void OnHotkeyReleased(object? sender, EventArgs e)
    {
        byte[] capturedBytes;
        bool wasTap;
        lock (_lock)
        {
            if (!_isCapturing || _disposed)
            {
                return;
            }

            _isCapturing = false;
            _pressDuration.Stop();
            wasTap = _tapThreshold > TimeSpan.Zero && _pressDuration.Elapsed < _tapThreshold;
            try
            {
                capturedBytes = _audioCaptureService.StopCapture();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs($"Failed to stop audio capture: {ex.Message}", ex));
                return;
            }
        }

        if (wasTap)
        {
            // The fragment is discarded rather than transcribed: a tap is a gesture, not speech.
            Tapped?.Invoke(this, EventArgs.Empty);
            return;
        }

        LastCapturedAudio = capturedBytes;
        AudioCaptured?.Invoke(this, capturedBytes);
    }

    private void OnHotkeyRegistrationFailed(object? sender, string message)
    {
        ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs($"Hotkey registration error: {message}"));
    }

    private void OnAudioStateChanged(object? sender, CaptureStateChangedEventArgs e)
    {
        StateChanged?.Invoke(this, e);
    }

    private void OnAudioErrorOccurred(object? sender, CaptureErrorEventArgs e)
    {
        lock (_lock)
        {
            _isCapturing = false;
        }
        ErrorOccurred?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
            _hotkeyService.HotkeyReleased -= OnHotkeyReleased;
            _hotkeyService.RegistrationFailed -= OnHotkeyRegistrationFailed;
            _audioCaptureService.StateChanged -= OnAudioStateChanged;
            _audioCaptureService.ErrorOccurred -= OnAudioErrorOccurred;

            Stop();
        }
    }
}
