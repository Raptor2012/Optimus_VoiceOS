namespace Optimus.Core.Audio;

using System;
using Optimus.Core.Hotkeys;

public sealed class PushToTalkController : IDisposable
{
    private readonly IHotkeyService _hotkeyService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly object _lock = new();
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

    public PushToTalkController(IHotkeyService hotkeyService, IAudioCaptureService audioCaptureService)
    {
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
        lock (_lock)
        {
            if (!_isCapturing || _disposed)
            {
                return;
            }

            _isCapturing = false;
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
