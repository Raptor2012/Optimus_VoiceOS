namespace Optimus.Core.Phone;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>What a frame carries.</summary>
public enum PhoneFrameKind : byte
{
    Json = 1,
    Audio = 2,
    TtsAudio = 3
}

/// <summary>
/// The whole wire format: one byte of kind, four bytes of big-endian length, then the payload.
/// </summary>
/// <remarks>
/// A raw socket with this header, rather than WebSocket or HTTP, because it needs no handshake,
/// no library on either side, and no version negotiation — all of which this slice rules out.
/// Both ends are shipped together, so there is exactly one format and nothing to negotiate.
/// </remarks>
public static class PhoneFraming
{
    public const int HeaderBytes = 5;

    /// <summary>Guards against a corrupt length turning into a huge allocation.</summary>
    public const int MaxPayloadBytes = 1024 * 1024;

    public static byte[] Encode(PhoneFrameKind kind, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException($"Payload of {payload.Length} bytes exceeds the {MaxPayloadBytes} limit.", nameof(payload));
        }

        byte[] frame = new byte[HeaderBytes + payload.Length];
        frame[0] = (byte)kind;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    /// <summary>
    /// Reads exactly one frame. Returns null on a clean end of stream.
    /// </summary>
    public static async Task<(PhoneFrameKind Kind, byte[] Payload)?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderBytes];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var kind = (PhoneFrameKind)header[0];
        if (kind is not (PhoneFrameKind.Json or PhoneFrameKind.Audio or PhoneFrameKind.TtsAudio))
        {
            throw new InvalidDataException($"Unknown frame kind {header[0]}.");
        }

        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1, 4));
        if (length < 0 || length > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Frame length {length} is out of range.");
        }

        byte[] payload = new byte[length];
        if (length > 0 && !await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (kind, payload);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return false; // Peer closed.
            }

            offset += read;
        }

        return true;
    }
}
