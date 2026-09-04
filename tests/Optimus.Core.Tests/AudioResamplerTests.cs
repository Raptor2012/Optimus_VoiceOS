namespace Optimus.Core.Tests;

using System;
using Optimus.Core.Audio;
using Xunit;

public class AudioResamplerTests
{
    [Fact]
    public void ResampleFloatToPcm16Mono_ProducesCorrectLength()
    {
        // 48kHz, 2 channels, 1 second = 48,000 frames = 96,000 float samples
        const int sourceRate = 48000;
        const int sourceChannels = 2;
        float[] stereoSamples = new float[sourceRate * sourceChannels];

        // Fill with 440 Hz sine wave
        for (int i = 0; i < sourceRate; i++)
        {
            float s = (float)Math.Sin(2.0 * Math.PI * 440.0 * i / sourceRate);
            stereoSamples[i * 2] = s;
            stereoSamples[i * 2 + 1] = s;
        }

        byte[] pcmBytes = AudioResampler.ResampleFloatToPcm16Mono(stereoSamples, sourceRate, sourceChannels);

        // 16kHz mono 16-bit = 16,000 samples * 2 bytes = 32,000 bytes
        Assert.Equal(16000 * 2, pcmBytes.Length);
    }

    [Fact]
    public void ResampleFloatToPcm16Mono_SilenceProducesSilence()
    {
        float[] silentSamples = new float[48000 * 2]; // 1s of silence
        byte[] pcmBytes = AudioResampler.ResampleFloatToPcm16Mono(silentSamples, 48000, 2);

        Assert.Equal(32000, pcmBytes.Length);
        foreach (byte b in pcmBytes)
        {
            Assert.Equal(0, b);
        }
    }

    [Fact]
    public void ResampleFloatToPcm16Mono_EmptyInputReturnsEmpty()
    {
        byte[] result = AudioResampler.ResampleFloatToPcm16Mono(ReadOnlySpan<float>.Empty, 48000, 2);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(48000, 0)]
    [InlineData(48000, -1)]
    public void ResampleFloatToPcm16Mono_InvalidParamsThrows(int rate, int channels)
    {
        float[] data = new float[100];
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioResampler.ResampleFloatToPcm16Mono(data, rate, channels));
    }
}
