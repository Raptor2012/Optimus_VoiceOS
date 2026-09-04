namespace Optimus.Core.Audio;

using System;

public class CaptureStateChangedEventArgs : EventArgs
{
    public bool IsCapturing { get; }
    public string? StatusMessage { get; }

    public CaptureStateChangedEventArgs(bool isCapturing, string? statusMessage = null)
    {
        IsCapturing = isCapturing;
        StatusMessage = statusMessage;
    }
}

public class CaptureErrorEventArgs : EventArgs
{
    public string Message { get; }
    public Exception? Exception { get; }

    public CaptureErrorEventArgs(string message, Exception? exception = null)
    {
        Message = message;
        Exception = exception;
    }
}

public interface IAudioCaptureService : IDisposable
{
    bool IsCapturing { get; }
    void StartCapture();
    byte[] StopCapture();
    event EventHandler<CaptureStateChangedEventArgs>? StateChanged;
    event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;
}
