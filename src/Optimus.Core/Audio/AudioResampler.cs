namespace Optimus.Core.Audio;

using System;
using System.IO;

/// <summary>
/// Converts arbitrary audio formats (typically WASAPI 48kHz/44.1kHz float stereo)
/// to canonical 16kHz mono 16-bit PCM for local speech recognition.
/// All processing is in-memory; no disk writes are performed.
/// </summary>
public static class AudioResampler
{
    public const int TargetSampleRate = 16000;
    public const int TargetBitsPerSample = 16;
    public const int TargetChannels = 1;

    /// <summary>
    /// Resamples IEEE 32-bit float samples to 16kHz 16-bit signed PCM mono bytes.
    /// </summary>
    public static byte[] ResampleFloatToPcm16Mono(
        ReadOnlySpan<float> inputSamples,
        int sourceSampleRate,
        int sourceChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceChannels);

        if (inputSamples.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        int frameCount = inputSamples.Length / sourceChannels;
        if (frameCount == 0)
        {
            return Array.Empty<byte>();
        }

        // Step 1: Downmix channels to mono float
        float[] monoFloat = new float[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            float sum = 0f;
            int offset = i * sourceChannels;
            for (int ch = 0; ch < sourceChannels; ch++)
            {
                sum += inputSamples[offset + ch];
            }
            monoFloat[i] = sum / sourceChannels;
        }

        // Step 2: Resample to 16kHz if needed
        float[] resampled;
        if (sourceSampleRate == TargetSampleRate)
        {
            resampled = monoFloat;
        }
        else
        {
            double ratio = (double)TargetSampleRate / sourceSampleRate;
            int targetFrames = (int)Math.Round(frameCount * ratio);
            if (targetFrames <= 0)
            {
                return Array.Empty<byte>();
            }

            resampled = new float[targetFrames];
            for (int i = 0; i < targetFrames; i++)
            {
                double srcIndex = i / ratio;
                int idx0 = (int)Math.Floor(srcIndex);
                int idx1 = Math.Min(idx0 + 1, frameCount - 1);
                double frac = srcIndex - idx0;

                float sample0 = monoFloat[idx0];
                float sample1 = monoFloat[idx1];
                resampled[i] = (float)(sample0 + frac * (sample1 - sample0));
            }
        }

        // Step 3: Convert float (-1.0 to 1.0) to 16-bit signed PCM little-endian
        byte[] pcmBytes = new byte[resampled.Length * sizeof(short)];
        for (int i = 0; i < resampled.Length; i++)
        {
            float s = Math.Clamp(resampled[i], -1.0f, 1.0f);
            short sample16 = (short)Math.Round(s * 32767.0f);
            pcmBytes[i * 2] = (byte)(sample16 & 0xFF);
            pcmBytes[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
        }

        return pcmBytes;
    }

    /// <summary>
    /// Resamples 16-bit signed integer PCM samples to 16kHz mono 16-bit PCM bytes.
    /// </summary>
    public static byte[] ResamplePcm16ToPcm16Mono(
        ReadOnlySpan<byte> inputPcmBytes,
        int sourceSampleRate,
        int sourceChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceChannels);

        int totalSamples = inputPcmBytes.Length / sizeof(short);
        if (totalSamples == 0)
        {
            return Array.Empty<byte>();
        }

        float[] floatSamples = new float[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            short s = (short)(inputPcmBytes[i * 2] | (inputPcmBytes[i * 2 + 1] << 8));
            floatSamples[i] = s / 32768.0f;
        }

        return ResampleFloatToPcm16Mono(floatSamples, sourceSampleRate, sourceChannels);
    }
}
