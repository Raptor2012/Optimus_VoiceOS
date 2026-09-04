namespace Optimus.Core.Tests;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimus.Core.Audio.Wasapi;
using Xunit;

/// <summary>
/// Covers the packet-release half of the S001 blocker: every successfully acquired non-empty
/// packet must be released, including silent ones, or the capture stream wedges.
/// </summary>
public class WasapiPacketReaderTests : IDisposable
{
    private readonly FakePacketSource _source = new();

    public void Dispose()
    {
        _source.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Drain_ReleasesEveryAcquiredPacket()
    {
        _source.AddPacket(Filled(4, 0x11));
        _source.AddPacket(Filled(4, 0x22));
        _source.AddPacket(Filled(4, 0x33));

        PacketDrainResult result = WasapiPacketReader.Drain(_source, bytesPerFrame: 4, (_, _) => { });

        Assert.Equal(3, result.PacketsAcquired);
        Assert.Equal(3, result.PacketsReleased);
        Assert.Equal(new uint[] { 1, 1, 1 }, _source.ReleasedFrames);
    }

    /// <summary>The regression that stalled capture: a silent packet is still acquired.</summary>
    [Fact]
    public void Drain_ReleasesSilentPackets()
    {
        _source.AddPacket(Filled(4, 0x11));
        _source.AddSilentPacket(frames: 1);
        _source.AddPacket(Filled(4, 0x33));

        PacketDrainResult result = WasapiPacketReader.Drain(_source, bytesPerFrame: 4, (_, _) => { });

        Assert.Equal(3, result.PacketsAcquired);
        Assert.Equal(3, result.PacketsReleased);
        Assert.Equal(1, result.SilentPackets);
    }

    /// <summary>A driver may report a silent packet with a null data pointer.</summary>
    [Fact]
    public void Drain_ReleasesSilentPacketWithNullPointer()
    {
        _source.AddRawPacket(IntPtr.Zero, frames: 2, flags: (uint)AudclntBufferFlags.Silent, hr: 0);

        PacketDrainResult result = WasapiPacketReader.Drain(_source, bytesPerFrame: 4, (_, _) => { });

        Assert.Equal(1, result.PacketsAcquired);
        Assert.Equal(1, result.PacketsReleased);
        Assert.Equal(new uint[] { 2 }, _source.ReleasedFrames);
    }

    /// <summary>A silent packet still contributes its frames, as zeroed samples.</summary>
    [Fact]
    public void Drain_SilentPacketWritesZeroedSamplesOfCorrectLength()
    {
        _source.AddSilentPacket(frames: 3);

        byte[]? written = null;
        int writtenCount = 0;
        WasapiPacketReader.Drain(_source, bytesPerFrame: 4, (buffer, count) =>
        {
            written = buffer;
            writtenCount = count;
        });

        Assert.NotNull(written);
        Assert.Equal(12, writtenCount);
        Assert.All(written!, b => Assert.Equal(0, b));
    }

    /// <summary>
    /// AUDCLNT_BUFFERFLAGS_SILENT is 0x2. The original code tested 0x1, which is
    /// DATA_DISCONTINUITY, so it blanked real audio whenever a discontinuity was reported.
    /// </summary>
    [Fact]
    public void Drain_DiscontinuityPacketKeepsItsAudio()
    {
        _source.AddPacket(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, flags: (uint)AudclntBufferFlags.DataDiscontinuity);

        byte[]? written = null;
        PacketDrainResult result = WasapiPacketReader.Drain(_source, bytesPerFrame: 4, (buffer, _) => written = buffer);

        Assert.Equal(1, result.DiscontinuityPackets);
        Assert.Equal(0, result.SilentPackets);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, written);
        Assert.Equal(1, result.PacketsReleased);
    }

    /// <summary>AUDCLNT_S_BUFFER_EMPTY is a success code that acquires nothing.</summary>
    [Fact]
    public void Drain_DoesNotReleaseOnBufferEmpty()
    {
        _source.AddRawPacket(IntPtr.Zero, frames: 0, flags: 0, hr: AudclntHResults.SBufferEmpty);

        PacketDrainResult result = WasapiPacketReader.Drain(_source, bytesPerFrame: 4, (_, _) => { });

        Assert.Equal(0, result.PacketsAcquired);
        Assert.Equal(0, result.PacketsReleased);
        Assert.Empty(_source.ReleasedFrames);
    }

    /// <summary>A failed GetBuffer acquired nothing, so releasing would be wrong.</summary>
    [Fact]
    public void Drain_DoesNotReleaseWhenGetBufferFails()
    {
        _source.AddRawPacket(IntPtr.Zero, frames: 0, flags: 0, hr: unchecked((int)0x88890004));

        PacketDrainResult result = WasapiPacketReader.Drain(_source, bytesPerFrame: 4, (_, _) => { });

        Assert.Equal(0, result.PacketsAcquired);
        Assert.Empty(_source.ReleasedFrames);
    }

    /// <summary>A throwing sink must not leak the packet.</summary>
    [Fact]
    public void Drain_ReleasesPacketEvenWhenSinkThrows()
    {
        _source.AddPacket(Filled(4, 0x11));

        Assert.Throws<InvalidOperationException>(() =>
            WasapiPacketReader.Drain(_source, 4, (_, _) => throw new InvalidOperationException("sink failed")));

        Assert.Equal(new uint[] { 1 }, _source.ReleasedFrames);
    }

    [Fact]
    public void Drain_StopsWhenNoPacketsRemain()
    {
        PacketDrainResult result = WasapiPacketReader.Drain(_source, 4, (_, _) => { });

        Assert.Equal(0, result.PacketsAcquired);
        Assert.Equal(0, result.PacketsReleased);
    }

    /// <summary>Multi-frame packets release the exact frame count they acquired.</summary>
    [Fact]
    public void Drain_ReleasesExactFrameCount()
    {
        _source.AddRawPacketWithAudio(new byte[16], frames: 4, flags: 0);

        PacketDrainResult result = WasapiPacketReader.Drain(_source, bytesPerFrame: 4, (_, _) => { });

        Assert.Equal(new uint[] { 4 }, _source.ReleasedFrames);
        Assert.Equal(16, result.BytesWritten);
    }

    private static byte[] Filled(int length, byte fill)
    {
        byte[] b = new byte[length];
        Array.Fill(b, fill);
        return b;
    }

    private sealed class FakePacketSource : IWasapiPacketSource, IDisposable
    {
        private readonly Queue<(IntPtr Data, uint Frames, uint Flags, int Hr)> _packets = new();
        private readonly List<IntPtr> _allocations = new();

        public List<uint> ReleasedFrames { get; } = new();

        /// <summary>Adds a single-frame packet carrying the given bytes.</summary>
        public void AddPacket(byte[] audio, uint flags = 0) =>
            AddRawPacketWithAudio(audio, frames: 1, flags: flags);

        public void AddRawPacketWithAudio(byte[] audio, uint frames, uint flags)
        {
            IntPtr p = Marshal.AllocHGlobal(audio.Length);
            Marshal.Copy(audio, 0, p, audio.Length);
            _allocations.Add(p);
            _packets.Enqueue((p, frames, flags, 0));
        }

        public void AddSilentPacket(uint frames) =>
            _packets.Enqueue((IntPtr.Zero, frames, (uint)AudclntBufferFlags.Silent, 0));

        public void AddRawPacket(IntPtr data, uint frames, uint flags, int hr) =>
            _packets.Enqueue((data, frames, flags, hr));

        public int GetNextPacketSize(out uint framesInNextPacket)
        {
            framesInNextPacket = _packets.Count == 0 ? 0u : Math.Max(_packets.Peek().Frames, 1u);
            return 0;
        }

        public int GetBuffer(out IntPtr data, out uint framesRead, out uint flags)
        {
            if (_packets.Count == 0)
            {
                data = IntPtr.Zero;
                framesRead = 0;
                flags = 0;
                return AudclntHResults.SBufferEmpty;
            }

            var packet = _packets.Dequeue();
            data = packet.Data;
            framesRead = packet.Frames;
            flags = packet.Flags;
            return packet.Hr;
        }

        public int ReleaseBuffer(uint framesRead)
        {
            ReleasedFrames.Add(framesRead);
            return 0;
        }

        public void Dispose()
        {
            foreach (IntPtr p in _allocations)
            {
                Marshal.FreeHGlobal(p);
            }

            _allocations.Clear();
        }
    }
}
