namespace Optimus.Core.Audio.Wasapi;

using System;
using System.Runtime.InteropServices;

/// <summary>
/// The slice of <c>IAudioCaptureClient</c> the drain loop needs, so the loop can be unit
/// tested without a live audio endpoint.
/// </summary>
internal interface IWasapiPacketSource
{
    int GetNextPacketSize(out uint framesInNextPacket);

    int GetBuffer(out IntPtr data, out uint framesRead, out uint flags);

    int ReleaseBuffer(uint framesRead);
}

/// <summary>Counters describing one drain pass, used by tests and diagnostics.</summary>
internal sealed class PacketDrainResult
{
    public int PacketsAcquired { get; set; }

    public int PacketsReleased { get; set; }

    public int SilentPackets { get; set; }

    public int DiscontinuityPackets { get; set; }

    public long BytesWritten { get; set; }
}

/// <summary>
/// Drains all currently available WASAPI capture packets.
/// </summary>
/// <remarks>
/// The release contract is the whole point of this type. WASAPI requires that every packet
/// acquired by a successful <c>GetBuffer</c> that reported frames is handed back with a
/// matching <c>ReleaseBuffer</c>. Skipping one wedges the capture stream: the endpoint keeps
/// the packet checked out, <c>GetNextPacketSize</c> keeps returning the same packet, and the
/// buffer fills until capture stalls. A silent packet is still an acquired packet, and a
/// driver is allowed to report a silent packet with a null data pointer, so neither silence
/// nor a null pointer may short-circuit the release.
/// </remarks>
internal static class WasapiPacketReader
{
    public static PacketDrainResult Drain(
        IWasapiPacketSource source,
        int bytesPerFrame,
        Action<byte[], int> sink)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerFrame);

        var result = new PacketDrainResult();

        while (true)
        {
            int hr = source.GetNextPacketSize(out uint framesInNextPacket);
            if (hr < 0 || framesInNextPacket == 0)
            {
                break;
            }

            hr = source.GetBuffer(out IntPtr data, out uint framesRead, out uint flags);

            // A negative HRESULT means nothing was acquired, so nothing may be released.
            if (hr < 0)
            {
                break;
            }

            // AUDCLNT_S_BUFFER_EMPTY is a success code that acquires no packet. Per the
            // WASAPI contract the caller must not release in that case.
            if (hr == AudclntHResults.SBufferEmpty || framesRead == 0)
            {
                break;
            }

            result.PacketsAcquired++;

            try
            {
                var bufferFlags = (AudclntBufferFlags)flags;
                bool isSilent = (bufferFlags & AudclntBufferFlags.Silent) != 0;

                if (isSilent)
                {
                    result.SilentPackets++;
                }

                if ((bufferFlags & AudclntBufferFlags.DataDiscontinuity) != 0)
                {
                    result.DiscontinuityPackets++;
                }

                int byteCount = checked((int)framesRead * bytesPerFrame);
                byte[] buffer = new byte[byteCount];

                // A silent packet may legitimately carry a null pointer; treat both a silent
                // flag and a null pointer as silence rather than reading unmapped memory.
                if (!isSilent && data != IntPtr.Zero)
                {
                    Marshal.Copy(data, buffer, 0, byteCount);
                }

                sink(buffer, byteCount);
                result.BytesWritten += byteCount;
            }
            finally
            {
                // Runs for silent packets, null-pointer packets, and any sink exception.
                source.ReleaseBuffer(framesRead);
                result.PacketsReleased++;
            }
        }

        return result;
    }
}
