namespace Optimus.Core.Audio;

using System;
using System.Runtime.InteropServices;

/// <summary>
/// WebRTC Audio Processing Module echo canceller for the desktop voice path.
/// </summary>
/// <remarks>
/// The native bridge is optional at development time.  If the bridge is not beside the
/// executable (for example when running unit tests), audio is passed through unchanged.  This
/// keeps the capture path usable while making a missing native deployment visible through
/// <see cref="IsAvailable"/> instead of failing a microphone start.
/// </remarks>
public sealed class EchoCanceller : IDisposable
{
    public const int SampleRate = 16_000;
    public const int FrameMilliseconds = 10;
    public const int SamplesPerFrame = SampleRate * FrameMilliseconds / 1000;
    public const int BytesPerFrame = SamplesPerFrame * sizeof(short);

    private readonly object _lock = new();
    private IntPtr _handle;
    private bool _nativeAvailable;
    private bool _disposed;
    private byte[] _captureRemainder = Array.Empty<byte>();
    private byte[] _reverseRemainder = Array.Empty<byte>();

    public EchoCanceller()
    {
        try
        {
            _handle = Native.Create(SampleRate);
            _nativeAvailable = _handle != IntPtr.Zero;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    public bool IsAvailable => _nativeAvailable;

    /// <summary>Feeds the samples actually rendered by the assistant to WebRTC as far-end audio.</summary>
    public void RenderPlayback(ReadOnlySpan<byte> pcm16)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_nativeAvailable || pcm16.IsEmpty) return;

            byte[] input = Combine(_reverseRemainder, pcm16);
            int completeBytes = input.Length - input.Length % BytesPerFrame;
            int remainderBytes = input.Length - completeBytes;
            _reverseRemainder = remainderBytes == 0 ? Array.Empty<byte>() : input[^remainderBytes..];
            for (int offset = 0; offset < completeBytes; offset += BytesPerFrame)
            {
                Native.ProcessReverse(_handle, input.AsSpan(offset, BytesPerFrame), SamplesPerFrame);
            }
        }
    }

    /// <summary>
    /// Processes microphone PCM16 before it is published to VAD, transcription, or the utterance
    /// buffer.  WebRTC APM consumes 10 ms mono frames; partial frames are retained in memory.
    /// </summary>
    public byte[] ProcessCapture(ReadOnlySpan<byte> pcm16)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (pcm16.IsEmpty) return Array.Empty<byte>();
            if (!_nativeAvailable) return pcm16.ToArray();

            byte[] input = CombineRemainder(pcm16);
            int completeBytes = input.Length - input.Length % BytesPerFrame;
            int remainderBytes = input.Length - completeBytes;
            _captureRemainder = remainderBytes == 0 ? Array.Empty<byte>() : input[^remainderBytes..];

            if (completeBytes == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] output = new byte[completeBytes];
            for (int offset = 0; offset < completeBytes; offset += BytesPerFrame)
            {
                Native.ProcessCapture(_handle, input.AsSpan(offset, BytesPerFrame), output.AsSpan(offset, BytesPerFrame), SamplesPerFrame);
            }

            return output;
        }
    }

    /// <summary>Flushes a final partial frame without allowing it to leak into the next utterance.</summary>
    public byte[] FlushCapture()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] remainder = _captureRemainder;
            _captureRemainder = Array.Empty<byte>();
            return remainder;
        }
    }

    private byte[] CombineRemainder(ReadOnlySpan<byte> pcm16) => Combine(_captureRemainder, pcm16);

    private static byte[] Combine(byte[] prefix, ReadOnlySpan<byte> suffix)
    {
        if (prefix.Length == 0) return suffix.ToArray();
        byte[] combined = new byte[prefix.Length + suffix.Length];
        Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
        suffix.CopyTo(combined.AsSpan(prefix.Length));
        return combined;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != IntPtr.Zero)
            {
                try { Native.Destroy(_handle); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
                _handle = IntPtr.Zero;
            }
            _nativeAvailable = false;
            _captureRemainder = Array.Empty<byte>();
            _reverseRemainder = Array.Empty<byte>();
        }
    }

    private static class Native
    {
        [DllImport("optimus_webrtc_aec", CallingConvention = CallingConvention.Cdecl, EntryPoint = "optimus_aec_create")]
        public static extern IntPtr Create(int sampleRate);

        [DllImport("optimus_webrtc_aec", CallingConvention = CallingConvention.Cdecl, EntryPoint = "optimus_aec_process_reverse")]
        public static extern void ProcessReverse(IntPtr handle, ReadOnlySpan<byte> pcm16, int samples);

        [DllImport("optimus_webrtc_aec", CallingConvention = CallingConvention.Cdecl, EntryPoint = "optimus_aec_process_capture")]
        public static extern void ProcessCapture(IntPtr handle, ReadOnlySpan<byte> input, Span<byte> output, int samples);

        [DllImport("optimus_webrtc_aec", CallingConvention = CallingConvention.Cdecl, EntryPoint = "optimus_aec_destroy")]
        public static extern void Destroy(IntPtr handle);
    }
}
