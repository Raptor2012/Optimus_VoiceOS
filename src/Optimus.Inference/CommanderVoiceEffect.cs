namespace Optimus.Inference;

using System;
using System.Buffers.Binary;

/// <summary>Lightweight lower-register / metallic coloration, not a cloned speaker model.</summary>
public static class CommanderVoiceEffect
{
    public static byte[] Apply(byte[] pcm, int sampleRate, double pitchRatio, double resonance)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (sampleRate <= 0 || pitchRatio is < 0.8 or > 1.2 || resonance is < 0 or > 0.2)
            throw new ArgumentOutOfRangeException(nameof(pitchRatio));
        int samples = pcm.Length / 2;
        if (samples == 0) return [];
        int count = (int)(samples / pitchRatio);
        byte[] result = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            double position = i * pitchRatio;
            int left = Math.Min((int)position, samples - 1);
            int right = Math.Min(left + 1, samples - 1);
            double fraction = position - left;
            short a = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(left * 2, 2));
            short b = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(right * 2, 2));
            double value = a + (b - a) * fraction;
            // A small amplitude-modulated layer adds machine texture without obscuring consonants.
            double modulation = 1 - resonance + resonance * Math.Cos(2 * Math.PI * 44 * i / sampleRate);
            short output = (short)Math.Clamp(value * modulation, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2, 2), output);
        }
        return result;
    }
}
