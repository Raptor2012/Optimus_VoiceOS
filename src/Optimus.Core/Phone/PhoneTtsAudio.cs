namespace Optimus.Core.Phone;

using System;
using System.Buffers.Binary;
using System.IO;

public enum PhonePcmEncoding : byte
{
    Pcm16LittleEndian = 1
}

/// <summary>One ordered piece of synthesized speech sent to the phone.</summary>
public sealed record PhoneTtsAudioSegment(
    long Generation,
    int Sequence,
    int SampleRate,
    byte Channels,
    PhonePcmEncoding Encoding,
    byte[] Pcm);

/// <summary>Compact metadata header followed by PCM. Both apps ship together.</summary>
public static class PhoneTtsAudio
{
    public const int HeaderBytes = 18;

    public static byte[] Encode(PhoneTtsAudioSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        Validate(segment.Generation, segment.Sequence, segment.SampleRate, segment.Channels,
            segment.Encoding, segment.Pcm.Length);

        byte[] payload = new byte[HeaderBytes + segment.Pcm.Length];
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(0, 8), segment.Generation);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8, 4), segment.Sequence);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(12, 4), segment.SampleRate);
        payload[16] = segment.Channels;
        payload[17] = (byte)segment.Encoding;
        segment.Pcm.CopyTo(payload, HeaderBytes);
        return payload;
    }

    public static PhoneTtsAudioSegment Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderBytes)
        {
            throw new InvalidDataException("TTS audio metadata is truncated.");
        }

        long generation = BinaryPrimitives.ReadInt64BigEndian(payload[..8]);
        int sequence = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(8, 4));
        int sampleRate = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(12, 4));
        byte channels = payload[16];
        var encoding = (PhonePcmEncoding)payload[17];
        int pcmBytes = payload.Length - HeaderBytes;
        Validate(generation, sequence, sampleRate, channels, encoding, pcmBytes);
        return new PhoneTtsAudioSegment(generation, sequence, sampleRate, channels, encoding,
            payload[HeaderBytes..].ToArray());
    }

    private static void Validate(long generation, int sequence, int sampleRate, byte channels,
        PhonePcmEncoding encoding, int pcmBytes)
    {
        if (generation < 0) throw new InvalidDataException("Generation cannot be negative.");
        if (sequence < 0) throw new InvalidDataException("Sequence cannot be negative.");
        if (sampleRate is < 8000 or > 192000) throw new InvalidDataException("Sample rate is unsupported.");
        if (channels is not (1 or 2)) throw new InvalidDataException("Only mono or stereo PCM is supported.");
        if (encoding != PhonePcmEncoding.Pcm16LittleEndian) throw new InvalidDataException("PCM encoding is unsupported.");
        if (pcmBytes == 0 || pcmBytes % (channels * 2) != 0) throw new InvalidDataException("PCM payload is empty or not frame-aligned.");
        if (HeaderBytes + pcmBytes > PhoneFraming.MaxPayloadBytes) throw new InvalidDataException("TTS audio payload is too large.");
    }
}
