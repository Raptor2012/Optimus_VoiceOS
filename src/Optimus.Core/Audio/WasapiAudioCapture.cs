namespace Optimus.Core.Audio;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Optimus.Core.Audio.Wasapi;

/// <summary>
/// In-memory audio capture using Windows WASAPI (CoreAudio).
/// Captures from the default Windows microphone and resamples to 16 kHz mono PCM16.
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
    private WaveFormatInfo _format = new(48000, 2, 32, WaveSampleFormat.Float32);
    private volatile bool _isCapturing;
    private bool _disposed;

    public bool IsCapturing => _isCapturing;

    /// <summary>The device mix format of the most recent capture, for diagnostics.</summary>
    public string LastCaptureFormatDescription
    {
        get
        {
            lock (_lock)
            {
                return _format.ToString();
            }
        }
    }

    /// <summary>Packet accounting for the most recent capture, for diagnostics.</summary>
    public int LastPacketsAcquired { get; private set; }

    public int LastPacketsReleased { get; private set; }

    public int LastSilentPackets { get; private set; }

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
                LastPacketsAcquired = 0;
                LastPacketsReleased = 0;
                LastSilentPackets = 0;
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

    /// <summary>
    /// Stops capture and returns the utterance as 16 kHz mono PCM16, the only format this
    /// method ever returns.
    /// </summary>
    public byte[] StopCapture()
    {
        MemoryStream? bufferToProcess;
        WaveFormatInfo format;

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
            format = _format;
        }

        try
        {
            _captureThread?.Join(1000);
        }
        catch (ThreadStateException)
        {
            // Thread was never started; nothing to join.
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
            return ConvertToTargetPcm(rawBytes, format);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs($"Audio resampling failed: {ex.Message}", ex));
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Converts a raw device-format buffer to 16 kHz mono PCM16. Both supported device
    /// formats funnel through <see cref="AudioResampler"/>, so the return value is canonical
    /// regardless of what the endpoint delivered.
    /// </summary>
    internal static byte[] ConvertToTargetPcm(byte[] rawBytes, WaveFormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(rawBytes);
        ArgumentNullException.ThrowIfNull(format);

        if (rawBytes.Length == 0)
        {
            return Array.Empty<byte>();
        }

        switch (format.SampleFormat)
        {
            case WaveSampleFormat.Float32:
                int floatCount = rawBytes.Length / sizeof(float);
                if (floatCount == 0)
                {
                    return Array.Empty<byte>();
                }

                float[] floatSamples = new float[floatCount];
                Buffer.BlockCopy(rawBytes, 0, floatSamples, 0, floatCount * sizeof(float));
                return AudioResampler.ResampleFloatToPcm16Mono(floatSamples, format.SampleRate, format.Channels);

            case WaveSampleFormat.Pcm16:
                return AudioResampler.ResamplePcm16ToPcm16Mono(rawBytes, format.SampleRate, format.Channels);

            default:
                throw new NotSupportedException($"Unsupported capture sample format {format.SampleFormat}.");
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

        _format = WaveFormatParser.Parse(ReadMixFormatBlob(_mixFormatPtr));

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

    /// <summary>
    /// Copies the variable-length mix-format blob out of unmanaged memory. Its total size is
    /// the fixed 18-byte header plus the <c>cbSize</c> the header declares.
    /// </summary>
    private static byte[] ReadMixFormatBlob(IntPtr formatPtr)
    {
        byte[] header = new byte[WaveFormatSizes.WaveFormatEx];
        Marshal.Copy(formatPtr, header, 0, header.Length);

        ushort cbSize = BitConverter.ToUInt16(header, WaveFormatSizes.WaveFormatEx - sizeof(ushort));
        if (cbSize == 0)
        {
            return header;
        }

        byte[] full = new byte[WaveFormatSizes.WaveFormatEx + cbSize];
        Marshal.Copy(formatPtr, full, 0, full.Length);
        return full;
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
        IAudioCaptureClient? client = _captureClient;
        if (client == null)
        {
            return;
        }

        try
        {
            var source = new AudioCaptureClientSource(client);
            PacketDrainResult drain = WasapiPacketReader.Drain(
                source,
                _format.BytesPerFrame,
                (buffer, count) =>
                {
                    lock (_lock)
                    {
                        _inMemoryBuffer?.Write(buffer, 0, count);
                    }
                });

            LastPacketsAcquired += drain.PacketsAcquired;
            LastPacketsReleased += drain.PacketsReleased;
            LastSilentPackets += drain.SilentPackets;
        }
        catch (COMException ex)
        {
            ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs($"Audio packet read failed: {ex.Message}", ex));
        }
        catch (InvalidCastException ex)
        {
            ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs($"Audio packet read failed: {ex.Message}", ex));
        }
    }

    private void CleanupWasapiResources()
    {
        try
        {
            _audioClient?.Stop();
        }
        catch (InvalidCastException)
        {
            // COM object already torn down.
        }
        catch (COMException)
        {
            // Endpoint already gone.
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
        catch (ThreadStateException)
        {
            // Never started.
        }
        catch (ObjectDisposedException)
        {
            // Signal already disposed.
        }

        lock (_lock)
        {
            _inMemoryBuffer?.Dispose();
            _inMemoryBuffer = null;
            CleanupWasapiResources();
        }
    }

    /// <summary>Adapts the COM capture client to the drain loop's narrow interface.</summary>
    private sealed class AudioCaptureClientSource : IWasapiPacketSource
    {
        private readonly IAudioCaptureClient _client;

        public AudioCaptureClientSource(IAudioCaptureClient client) => _client = client;

        public int GetNextPacketSize(out uint framesInNextPacket) =>
            _client.GetNextPacketSize(out framesInNextPacket);

        public int GetBuffer(out IntPtr data, out uint framesRead, out uint flags) =>
            _client.GetBuffer(out data, out framesRead, out flags, out _, out _);

        public int ReleaseBuffer(uint framesRead) => _client.ReleaseBuffer(framesRead);
    }
}
