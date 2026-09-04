namespace Optimus.Core.Audio;

using System;
using System.IO;

/// <summary>
/// In-memory audio capture implementation for testing and development environments
/// where physical microphone hardware is absent or simulated capture is desired.
/// Generates synthetic audio samples in memory; never writes to disk.
/// </summary>
public sealed class InMemoryAudioCapture : IAudioCaptureService
{
    private readonly MemoryStream _buffer = new();
    private readonly object _lock = new();
    private bool _isCapturing;
    private bool _disposed;

    public bool SimulateFailureOnStart { get; set; }
    public string SimulatedFailureMessage { get; set; } = "Simulated microphone unavailable";

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

    public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;
    public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

    public void StartCapture()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (SimulateFailureOnStart)
            {
                _isCapturing = false;
                ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs(SimulatedFailureMessage));
                return;
            }

            _buffer.SetLength(0);
            _isCapturing = true;
            StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(true, "Simulated in-memory recording started"));
        }
    }

    public void AppendSyntheticAudio(int sampleCount, float frequencyHz = 440f)
    {
        lock (_lock)
        {
            if (!_isCapturing)
            {
                return;
            }

            const int sampleRate = 16000;
            byte[] bytes = new byte[sampleCount * sizeof(short)];
            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / sampleRate;
                short sample = (short)(Math.Sin(2.0 * Math.PI * frequencyHz * t) * 16000);
                bytes[i * 2] = (byte)(sample & 0xFF);
                bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            _buffer.Write(bytes, 0, bytes.Length);
        }
    }

    public byte[] StopCapture()
    {
        lock (_lock)
        {
            if (!_isCapturing)
            {
                return Array.Empty<byte>();
            }

            _isCapturing = false;
            byte[] result = _buffer.ToArray();
            _buffer.SetLength(0);
            StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(false, "Simulated recording stopped"));
            return result;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _isCapturing = false;
            _buffer.Dispose();
        }
    }
}
