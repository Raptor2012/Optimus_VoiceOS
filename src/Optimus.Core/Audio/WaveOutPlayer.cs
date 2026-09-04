namespace Optimus.Core.Audio;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Streams 16-bit mono PCM to the default output device and reports when it has finished.
/// </summary>
/// <remarks>
/// Uses WinMM <c>waveOut</c> directly rather than an audio library, for the same reason capture
/// uses WASAPI directly: one small, dependency-free path that this app fully controls.
/// <para>
/// Buffers are queued, so a segment can start playing while the next one is still being
/// synthesized. <see cref="WaitForDrain"/> returns once the device has actually played
/// everything queued, which is what the approval flow needs — the microphone must not open until
/// the speaker is genuinely silent, or the app would hear itself.
/// </para>
/// </remarks>
public sealed class WaveOutPlayer : IDisposable
{
    private readonly object _lock = new();
    private readonly List<IntPtr> _headers = new();
    private readonly int _sampleRate;

    private IntPtr _device = IntPtr.Zero;
    private int _queuedBuffers;
    private bool _disposed;

    public WaveOutPlayer(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _sampleRate = sampleRate;
    }

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _device != IntPtr.Zero;
            }
        }
    }

    /// <summary>True while the device still has audio queued or playing.</summary>
    public bool IsPlaying
    {
        get
        {
            lock (_lock)
            {
                ReapCompletedBuffers();
                return _queuedBuffers > 0;
            }
        }
    }

    public void Open()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_device != IntPtr.Zero)
            {
                return;
            }

            var format = new WAVEFORMATEX
            {
                wFormatTag = NativeAudio.WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = (uint)_sampleRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = (uint)(_sampleRate * 2),
                cbSize = 0
            };

            int result = NativeAudio.waveOutOpen(
                out IntPtr device, NativeAudio.WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0);

            if (result != NativeAudio.MMSYSERR_NOERROR)
            {
                throw new InvalidOperationException($"Could not open the audio output device (MMSYSERR {result}).");
            }

            _device = device;
        }
    }

    /// <summary>Queues one buffer. Returns immediately; playback continues in the background.</summary>
    public void Queue(byte[] pcm, int count)
    {
        ArgumentNullException.ThrowIfNull(pcm);

        if (count <= 0)
        {
            return;
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Open();
            ReapCompletedBuffers();

            IntPtr data = Marshal.AllocHGlobal(count);
            Marshal.Copy(pcm, 0, data, count);

            var header = new WAVEHDR
            {
                lpData = data,
                dwBufferLength = (uint)count,
                dwFlags = 0
            };

            IntPtr headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            Marshal.StructureToPtr(header, headerPtr, fDeleteOld: false);

            int prepared = NativeAudio.waveOutPrepareHeader(_device, headerPtr, Marshal.SizeOf<WAVEHDR>());
            if (prepared != NativeAudio.MMSYSERR_NOERROR)
            {
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(headerPtr);
                throw new InvalidOperationException($"Could not prepare an audio buffer (MMSYSERR {prepared}).");
            }

            int written = NativeAudio.waveOutWrite(_device, headerPtr, Marshal.SizeOf<WAVEHDR>());
            if (written != NativeAudio.MMSYSERR_NOERROR)
            {
                _ = NativeAudio.waveOutUnprepareHeader(_device, headerPtr, Marshal.SizeOf<WAVEHDR>());
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(headerPtr);
                throw new InvalidOperationException($"Could not queue audio for playback (MMSYSERR {written}).");
            }

            _headers.Add(headerPtr);
            _queuedBuffers++;
        }
    }

    /// <summary>
    /// Blocks until every queued buffer has finished playing, or the token is cancelled.
    /// </summary>
    /// <returns>True when playback drained normally; false when cancelled.</returns>
    public bool WaitForDrain(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            lock (_lock)
            {
                if (_disposed || _device == IntPtr.Zero)
                {
                    return true;
                }

                ReapCompletedBuffers();
                if (_queuedBuffers == 0)
                {
                    return true;
                }
            }

            // 5 ms keeps the post-playback handoff well inside the 200 ms readiness target
            // without spinning a core.
            Thread.Sleep(5);
        }
    }

    /// <summary>Stops immediately and drops anything queued.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_device == IntPtr.Zero)
            {
                return;
            }

            _ = NativeAudio.waveOutReset(_device);
            ReapCompletedBuffers(force: true);
        }
    }

    /// <summary>Frees headers the device has finished with. Must be called under the lock.</summary>
    private void ReapCompletedBuffers(bool force = false)
    {
        for (int i = _headers.Count - 1; i >= 0; i--)
        {
            IntPtr headerPtr = _headers[i];
            var header = Marshal.PtrToStructure<WAVEHDR>(headerPtr);

            bool done = (header.dwFlags & NativeAudio.WHDR_DONE) != 0;
            if (!done && !force)
            {
                continue;
            }

            _ = NativeAudio.waveOutUnprepareHeader(_device, headerPtr, Marshal.SizeOf<WAVEHDR>());
            Marshal.FreeHGlobal(header.lpData);
            Marshal.FreeHGlobal(headerPtr);

            _headers.RemoveAt(i);
            _queuedBuffers = Math.Max(0, _queuedBuffers - 1);
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

            if (_device != IntPtr.Zero)
            {
                _ = NativeAudio.waveOutReset(_device);
                ReapCompletedBuffers(force: true);
                _ = NativeAudio.waveOutClose(_device);
                _device = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private static class NativeAudio
    {
        public const int MMSYSERR_NOERROR = 0;
        public const int WAVE_MAPPER = -1;
        public const ushort WAVE_FORMAT_PCM = 1;
        public const uint WHDR_DONE = 0x00000001;

        [DllImport("winmm.dll")]
        public static extern int waveOutOpen(
            out IntPtr phwo, int uDeviceID, ref WAVEFORMATEX pwfx,
            IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

        [DllImport("winmm.dll")]
        public static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        public static extern int waveOutClose(IntPtr hwo);
    }
}
