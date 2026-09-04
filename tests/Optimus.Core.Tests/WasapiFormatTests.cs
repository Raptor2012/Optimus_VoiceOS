namespace Optimus.Core.Tests;

using System;
using System.Runtime.InteropServices;
using Optimus.Core.Audio;
using Optimus.Core.Audio.Wasapi;
using Xunit;

/// <summary>
/// Covers the S001 WASAPI blocker: native struct layout, extensible float/PCM recognition,
/// and the guarantee that capture output is 16 kHz mono PCM16.
/// </summary>
public class WasapiFormatTests
{
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// mmreg.h declares WAVEFORMATEX under pshpack1.h, so it is exactly 18 bytes. Default
    /// CLR packing yields 20, which was the root cause of the blocker.
    /// </summary>
    [Fact]
    public void WaveFormatEx_HasNativeSizeOf18Bytes()
    {
        Assert.Equal(18, Marshal.SizeOf<WAVEFORMATEX>());
    }

    /// <summary>18 + 2 + 4 + 16 = 40. Default packing yields 44.</summary>
    [Fact]
    public void WaveFormatExtensible_HasNativeSizeOf40Bytes()
    {
        Assert.Equal(40, Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
    }

    /// <summary>
    /// The SubFormat GUID must sit at offset 24. At the mispacked offset 28 the parser reads
    /// four bytes of channel mask plus twelve of GUID and never matches a known subtype.
    /// </summary>
    [Fact]
    public void WaveFormatExtensible_SubFormatSitsAtOffset24()
    {
        Assert.Equal(24, (int)Marshal.OffsetOf<WAVEFORMATEXTENSIBLE>(nameof(WAVEFORMATEXTENSIBLE.SubFormat)));
        Assert.Equal(18, (int)Marshal.OffsetOf<WAVEFORMATEXTENSIBLE>(nameof(WAVEFORMATEXTENSIBLE.wValidBitsPerSample)));
        Assert.Equal(20, (int)Marshal.OffsetOf<WAVEFORMATEXTENSIBLE>(nameof(WAVEFORMATEXTENSIBLE.dwChannelMask)));
    }

    [Fact]
    public void WaveFormatEx_FieldOffsetsMatchNativeLayout()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<WAVEFORMATEX>(nameof(WAVEFORMATEX.wFormatTag)));
        Assert.Equal(2, (int)Marshal.OffsetOf<WAVEFORMATEX>(nameof(WAVEFORMATEX.nChannels)));
        Assert.Equal(4, (int)Marshal.OffsetOf<WAVEFORMATEX>(nameof(WAVEFORMATEX.nSamplesPerSec)));
        Assert.Equal(8, (int)Marshal.OffsetOf<WAVEFORMATEX>(nameof(WAVEFORMATEX.nAvgBytesPerSec)));
        Assert.Equal(12, (int)Marshal.OffsetOf<WAVEFORMATEX>(nameof(WAVEFORMATEX.nBlockAlign)));
        Assert.Equal(14, (int)Marshal.OffsetOf<WAVEFORMATEX>(nameof(WAVEFORMATEX.wBitsPerSample)));
        Assert.Equal(16, (int)Marshal.OffsetOf<WAVEFORMATEX>(nameof(WAVEFORMATEX.cbSize)));
    }

    /// <summary>The shared-mode mix format a typical Windows microphone reports.</summary>
    [Fact]
    public void Parse_ExtensibleFloat32_IsRecognizedAsFloat()
    {
        byte[] blob = BuildExtensible(0xFFFE, channels: 2, sampleRate: 48000, bits: 32, SubtypeFloat);

        WaveFormatInfo info = WaveFormatParser.Parse(blob);

        Assert.Equal(WaveSampleFormat.Float32, info.SampleFormat);
        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(2, info.Channels);
        Assert.Equal(32, info.BitsPerSample);
        Assert.Equal(8, info.BytesPerFrame);
        Assert.True(info.IsFloat);
    }

    [Fact]
    public void Parse_ExtensiblePcm16_IsRecognizedAsPcm()
    {
        byte[] blob = BuildExtensible(0xFFFE, channels: 1, sampleRate: 44100, bits: 16, SubtypePcm);

        WaveFormatInfo info = WaveFormatParser.Parse(blob);

        Assert.Equal(WaveSampleFormat.Pcm16, info.SampleFormat);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(2, info.BytesPerFrame);
        Assert.False(info.IsFloat);
    }

    [Fact]
    public void Parse_PlainIeeeFloat_IsRecognizedAsFloat()
    {
        byte[] blob = BuildWaveFormatEx(0x0003, channels: 2, sampleRate: 48000, bits: 32, cbSize: 0);

        Assert.Equal(WaveSampleFormat.Float32, WaveFormatParser.Parse(blob).SampleFormat);
    }

    [Fact]
    public void Parse_PlainPcm16_IsRecognizedAsPcm()
    {
        byte[] blob = BuildWaveFormatEx(0x0001, channels: 2, sampleRate: 16000, bits: 16, cbSize: 0);

        Assert.Equal(WaveSampleFormat.Pcm16, WaveFormatParser.Parse(blob).SampleFormat);
    }

    /// <summary>An unknown subtype must fail loudly, not be silently treated as PCM.</summary>
    [Fact]
    public void Parse_ExtensibleWithUnknownSubFormat_Throws()
    {
        byte[] blob = BuildExtensible(0xFFFE, 2, 48000, 32, new Guid("12345678-0000-0010-8000-00AA00389B71"));

        Assert.Throws<NotSupportedException>(() => WaveFormatParser.Parse(blob));
    }

    [Fact]
    public void Parse_ExtensibleWithTruncatedCbSize_Throws()
    {
        byte[] blob = BuildExtensible(0xFFFE, 2, 48000, 32, SubtypeFloat);
        // Claim no extensible tail while still declaring the extensible tag.
        BitConverter.GetBytes((ushort)0).CopyTo(blob, 16);

        Assert.Throws<NotSupportedException>(() => WaveFormatParser.Parse(blob));
    }

    [Fact]
    public void Parse_UnsupportedBitDepth_Throws()
    {
        byte[] blob = BuildWaveFormatEx(0x0001, channels: 2, sampleRate: 48000, bits: 24, cbSize: 0);

        Assert.Throws<NotSupportedException>(() => WaveFormatParser.Parse(blob));
    }

    [Fact]
    public void Parse_BlobShorterThanHeader_Throws()
    {
        Assert.Throws<NotSupportedException>(() => WaveFormatParser.Parse(new byte[10]));
    }

    /// <summary>
    /// The end-to-end format guarantee: whatever the device delivers, StopCapture's converter
    /// yields 16 kHz mono PCM16 — two bytes per sample, one channel, 16000 samples per second.
    /// </summary>
    [Theory]
    [InlineData(48000, 2, true)]
    [InlineData(44100, 2, true)]
    [InlineData(16000, 1, true)]
    [InlineData(48000, 2, false)]
    [InlineData(44100, 1, false)]
    public void ConvertToTargetPcm_AlwaysProduces16kMonoPcm16(int rate, int channels, bool isFloat)
    {
        WaveSampleFormat sampleFormat = isFloat ? WaveSampleFormat.Float32 : WaveSampleFormat.Pcm16;
        int bits = isFloat ? 32 : 16;
        var format = new WaveFormatInfo(rate, channels, bits, sampleFormat);

        // Exactly one second of device audio.
        int frames = rate;
        byte[] raw = new byte[frames * format.BytesPerFrame];
        for (int i = 0; i < frames; i++)
        {
            float s = (float)Math.Sin(2.0 * Math.PI * 440.0 * i / rate);
            for (int ch = 0; ch < channels; ch++)
            {
                int frameOffset = (i * format.BytesPerFrame) + (ch * (bits / 8));
                if (sampleFormat == WaveSampleFormat.Float32)
                {
                    BitConverter.GetBytes(s).CopyTo(raw, frameOffset);
                }
                else
                {
                    BitConverter.GetBytes((short)(s * 32767)).CopyTo(raw, frameOffset);
                }
            }
        }

        byte[] pcm = WasapiAudioCapture.ConvertToTargetPcm(raw, format);

        // 1 second at 16 kHz mono PCM16 == 16000 samples == 32000 bytes.
        Assert.Equal(AudioResampler.TargetSampleRate * sizeof(short), pcm.Length);
        Assert.Equal(0, pcm.Length % 2);

        // The signal survived: a 440 Hz tone is not silence.
        bool hasNonZero = false;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            if (BitConverter.ToInt16(pcm, i) != 0)
            {
                hasNonZero = true;
                break;
            }
        }

        Assert.True(hasNonZero, "Converted PCM16 was entirely silent.");
    }

    private static byte[] BuildWaveFormatEx(ushort tag, ushort channels, uint sampleRate, ushort bits, ushort cbSize)
    {
        ushort blockAlign = (ushort)(channels * (bits / 8));
        byte[] blob = new byte[18 + cbSize];
        BitConverter.GetBytes(tag).CopyTo(blob, 0);
        BitConverter.GetBytes(channels).CopyTo(blob, 2);
        BitConverter.GetBytes(sampleRate).CopyTo(blob, 4);
        BitConverter.GetBytes(sampleRate * blockAlign).CopyTo(blob, 8);
        BitConverter.GetBytes(blockAlign).CopyTo(blob, 12);
        BitConverter.GetBytes(bits).CopyTo(blob, 14);
        BitConverter.GetBytes(cbSize).CopyTo(blob, 16);
        return blob;
    }

    private static byte[] BuildExtensible(ushort tag, ushort channels, uint sampleRate, ushort bits, Guid subFormat)
    {
        byte[] blob = BuildWaveFormatEx(tag, channels, sampleRate, bits, cbSize: 22);
        BitConverter.GetBytes(bits).CopyTo(blob, 18);        // wValidBitsPerSample
        BitConverter.GetBytes((uint)0x3).CopyTo(blob, 20);   // dwChannelMask
        subFormat.ToByteArray().CopyTo(blob, 24);            // SubFormat at native offset 24
        return blob;
    }
}
