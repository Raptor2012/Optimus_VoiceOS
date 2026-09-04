namespace Optimus.Core.Audio;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Optimus.Core.Audio.Wasapi;

/// <summary>
/// In-memory audio capture using Windows WASAPI (CoreAudio).
/// Captures from the default Windows microphone and resamples to 16kHz mono PCM16.
/// Audio is kept strictly in-memory and never written to disk.
/// </summary>
public sealed class WasapiAudioCapture : IAudioCaptureService
{
    private readonly object _lock = new();
    private MemoryStream? _inMemoryBuffer;
    private Thread? _captureThread;
    private AutoResetEvent? _audioEvent;
    private ManualResetEventSlim? _stopSignal;
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private IntPtr _mixFormatPtr = IntPtr.Zero;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private int _bitsPerSample = 32;
    private bool _isFloat = true;
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

    public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;
    public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

    public void StartCapture()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_isCapturing)
            {
                return;
            }

            try
            {
                InitializeWasapi();
                _inMemoryBuffer = new MemoryStream();
                _stopSignal = new ManualResetEventSlim(false);
                _isCapturing = true;

                _captureThread = new Thread(CaptureLoop)
                {
                    Name = "Optimus.WasapiCapture",
                    IsBackground = true
                };
                _captureThread.Start();

                StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(true, "Recording audio in-memory..."));
            }
            catch (Exception ex)
            {
                _isCapturing = false;
                CleanupWasapiResources();
                ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs($"Failed to initialize microphone capture: {ex.Message}", ex));
            }
        }
    }

    public byte[] StopCapture()
    {
        MemoryStream? bufferToProcess;

        lock (_lock)
        {
            if (!_isCapturing)
            {
                return Array.Empty<byte>();
            }

            _isCapturing = false;
            _stopSignal?.Set();
            bufferToProcess = _inMemoryBuffer;
            _inMemoryBuffer = null;
        }

        try
        {
            _captureThread?.Join(1000);
        }
        catch
        {
            // Thread join timeout or interruption
        }

        lock (_lock)
        {
            CleanupWasapiResources();
            StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(false, "Capture stopped"));
        }

        if (bufferToProcess == null || bufferToProcess.Length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] rawBytes = bufferToProcess.ToArray();
        bufferToProcess.Dispose();

        try
        {
            if (_isFloat)
            {
                int floatCount = rawBytes.Length / sizeof(float);
                float[] floatSamples = new float[floatCount];
                Buffer.BlockCopy(rawBytes, 0, floatSamples, 0, rawBytes.Length);
                return AudioResampler.ResampleFloatToPcm16Mono(floatSamples, _sampleRate, _channels);
            }
            else if (_bitsPerSample == 16)
            {
                return AudioResampler.ResamplePcm16ToPcm16Mono(rawBytes, _sampleRate, _channels);
            }
            else
            {
                return rawBytes;
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs($"Audio resampling failed: {ex.Message}", ex));
            return Array.Empty<byte>();
        }
    }

    private void InitializeWasapi()
    {
        var enumeratorObj = new MMDeviceEnumeratorComObject();
        var enumerator = (IMMDeviceEnumerator)enumeratorObj;

        int hr = enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out var device);
        if (hr != 0 || device == null)
        {
            throw new InvalidOperationException($"Default audio recording device not found (HRESULT: 0x{hr:X8}). Please check microphone connection.");
        }

        Guid iidAudioClient = WasapiGuids.IID_IAudioClient;
        hr = device.Activate(ref iidAudioClient, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var clientObj);
        if (hr != 0 || clientObj == null)
        {
            throw new InvalidOperationException($"Could not activate audio client (HRESULT: 0x{hr:X8}).");
        }

        _audioClient = (IAudioClient)clientObj;

        hr = _audioClient.GetMixFormat(out _mixFormatPtr);
        if (hr != 0 || _mixFormatPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Failed to obtain device mix format (HRESULT: 0x{hr:X8}).");
        }

        ParseWaveFormat(_mixFormatPtr);

        long bufferDurationHns = 10_000_000; // 1 second buffer in 100ns units
        Guid emptySession = Guid.Empty;

        hr = _audioClient.Initialize(
            AudclntShareMode.Shared,
            AudclntStreamFlags.EventCallback,
            bufferDurationHns,
            0,
            _mixFormatPtr,
            ref emptySession);

        if (hr != 0)
        {
            throw new InvalidOperationException($"Failed to initialize WASAPI stream (HRESULT: 0x{hr:X8}).");
        }

        _audioEvent = new AutoResetEvent(false);
        hr = _audioClient.SetEventHandle(_audioEvent.SafeWaitHandle.DangerousGetHandle());
        if (hr != 0)
        {
            throw new InvalidOperationException($"Failed to set event handle on audio client (HRESULT: 0x{hr:X8}).");
        }

        Guid iidCaptureClient = WasapiGuids.IID_IAudioCaptureClient;
        hr = _audioClient.GetService(ref iidCaptureClient, out var captureObj);
        if (hr != 0 || captureObj == null)
        {
            throw new InvalidOperationException($"Failed to get audio capture client (HRESULT: 0x{hr:X8}).");
        }

        _captureClient = (IAudioCaptureClient)captureObj;
        hr = _audioClient.Start();
        if (hr != 0)
        {
            throw new InvalidOperationException($"Failed to start audio client (HRESULT: 0x{hr:X8}).");
        }
    }

    private void ParseWaveFormat(IntPtr formatPtr)
    {
        var waveFormat = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
        _sampleRate = (int)waveFormat.nSamplesPerSec;
        _channels = waveFormat.nChannels;
        _bitsPerSample = waveFormat.wBitsPerSample;

        if (waveFormat.wFormatTag == 3 /* WAVE_FORMAT_IEEE_FLOAT */)
        {
            _isFloat = true;
        }
        else if (waveFormat.wFormatTag == 0xFFFE /* WAVE_FORMAT_EXTENSIBLE */)
        {
            var ext = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(formatPtr);
            _isFloat = ext.SubFormat == WasapiGuids.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        }
        else
        {
            _isFloat = false;
        }
    }

    private void CaptureLoop()
    {
        var handles = new WaitHandle[] { _stopSignal!.WaitHandle, _audioEvent! };

        while (_isCapturing)
        {
            int index = WaitHandle.WaitAny(handles, 100);
            if (index == 0)
            {
                // Stop signal received
                break;
            }

            ReadAvailablePackets();
        }

        // Final read of remaining packets
        ReadAvailablePackets();
    }

    private void ReadAvailablePackets()
    {
        if (_captureClient == null || _inMemoryBuffer == null)
        {
            return;
        }

        while (true)
        {
            int hr = _captureClient.GetNextPacketSize(out uint packetFrames);
            if (hr != 0 || packetFrames == 0)
            {
                break;
            }

            hr = _captureClient.GetBuffer(out IntPtr dataPtr, out uint framesRead, out uint flags, out _, out _);
            if (hr != 0 || dataPtr == IntPtr.Zero || framesRead == 0)
            {
                break;
            }

            try
            {
                int bytesPerFrame = _channels * (_bitsPerSample / 8);
                int byteCount = (int)framesRead * bytesPerFrame;
                byte[] tempBuffer = new byte[byteCount];

                if ((flags & 0x01 /* AUDCLNT_BUFFERFLAGS_SILENT */) != 0)
                {
                    Array.Clear(tempBuffer, 0, tempBuffer.Length);
                }
                else
                {
                    Marshal.Copy(dataPtr, tempBuffer, 0, byteCount);
                }

                lock (_lock)
                {
                    _inMemoryBuffer?.Write(tempBuffer, 0, tempBuffer.Length);
                }
            }
            finally
            {
                _captureClient.ReleaseBuffer(framesRead);
            }
        }
    }

    private void CleanupWasapiResources()
    {
        try
        {
            _audioClient?.Stop();
        }
        catch
        {
            // Ignore on cleanup
        }

        _captureClient = null;
        _audioClient = null;

        if (_mixFormatPtr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_mixFormatPtr);
            _mixFormatPtr = IntPtr.Zero;
        }

        _audioEvent?.Dispose();
        _audioEvent = null;

        _stopSignal?.Dispose();
        _stopSignal = null;
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
            _isCapturing = false;
        }

        try
        {
            _stopSignal?.Set();
            _captureThread?.Join(500);
        }
        catch
        {
            // Ignore
        }

        lock (_lock)
        {
            _inMemoryBuffer?.Dispose();
            _inMemoryBuffer = null;
            CleanupWasapiResources();
        }
    }
}
