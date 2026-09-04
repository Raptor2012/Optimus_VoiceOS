namespace Optimus.Core.Speech;

using System;

/// <summary>
/// Generates the short two-tone chime that marks "approval listening may begin".
/// </summary>
/// <remarks>
/// Synthesized rather than shipped as an audio file: it is a few lines of arithmetic, it matches
/// whatever sample rate the voice uses without resampling, and it keeps one less binary asset in
/// the repository.
/// </remarks>
public static class Chime
{
    private const double FirstToneHz = 660.0;
    private const double SecondToneHz = 880.0;
    private const double ToneSeconds = 0.055;
    private const double Amplitude = 0.22;

    /// <summary>Total chime length, useful for latency accounting.</summary>
    public static double DurationSeconds => ToneSeconds * 2;

    /// <summary>Builds the chime as 16-bit mono PCM at the given rate.</summary>
    public static byte[] Build(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        int perTone = (int)(sampleRate * ToneSeconds);
        var pcm = new byte[perTone * 2 * 2];
        int offset = 0;

        offset = AppendTone(pcm, offset, FirstToneHz, perTone, sampleRate);
        AppendTone(pcm, offset, SecondToneHz, perTone, sampleRate);

        return pcm;
    }

    private static int AppendTone(byte[] destination, int offset, double frequency, int samples, int sampleRate)
    {
        for (int i = 0; i < samples; i++)
        {
            // Raised-cosine envelope: a hard start or stop clicks, which sounds like a fault.
            double envelope = 0.5 - (0.5 * Math.Cos(2.0 * Math.PI * i / Math.Max(1, samples - 1)));
            double sample = Math.Sin(2.0 * Math.PI * frequency * i / sampleRate) * envelope * Amplitude;

            short value = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
            destination[offset++] = (byte)(value & 0xFF);
            destination[offset++] = (byte)((value >> 8) & 0xFF);
        }

        return offset;
    }
}
