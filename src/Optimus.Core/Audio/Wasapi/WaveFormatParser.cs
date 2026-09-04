namespace Optimus.Core.Audio.Wasapi;

using System;
using System.Runtime.InteropServices;

/// <summary>How the device delivers each sample.</summary>
internal enum WaveSampleFormat
{
    Pcm16,
    Float32
}

/// <summary>The parsed shared-mode mix format of the capture endpoint.</summary>
internal sealed record WaveFormatInfo(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    WaveSampleFormat SampleFormat)
{
    public int BytesPerFrame => Channels * (BitsPerSample / 8);

    public bool IsFloat => SampleFormat == WaveSampleFormat.Float32;

    public override string ToString() =>
        $"{SampleRate} Hz, {Channels} ch, {BitsPerSample}-bit {SampleFormat}";
}

/// <summary>
/// Reads a native <c>WAVEFORMATEX</c> / <c>WAVEFORMATEXTENSIBLE</c> blob as returned by
/// <c>IAudioClient::GetMixFormat</c>.
/// </summary>
/// <remarks>
/// Reads through the interop structs themselves, so the <c>Pack = 1</c> layout in
/// <see cref="WAVEFORMATEX"/> and <see cref="WAVEFORMATEXTENSIBLE"/> is exercised by every
/// call rather than being asserted only in a size test.
/// </remarks>
internal static class WaveFormatParser
{
    public static WaveFormatInfo Parse(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < WaveFormatSizes.WaveFormatEx)
        {
            throw new NotSupportedException(
                $"Mix format blob is {blob.Length} bytes; a WAVEFORMATEX needs at least {WaveFormatSizes.WaveFormatEx}.");
        }

        WAVEFORMATEX format = MemoryMarshal.Read<WAVEFORMATEX>(blob);

        if (format.nChannels == 0)
        {
            throw new NotSupportedException("Mix format reports zero channels.");
        }

        if (format.nSamplesPerSec == 0)
        {
            throw new NotSupportedException("Mix format reports a zero sample rate.");
        }

        ushort effectiveTag = format.wFormatTag;
        if (effectiveTag == WaveFormatTag.Extensible)
        {
            effectiveTag = ResolveExtensibleTag(blob, format);
        }

        WaveSampleFormat sampleFormat = effectiveTag switch
        {
            WaveFormatTag.IeeeFloat when format.wBitsPerSample == 32 => WaveSampleFormat.Float32,
            WaveFormatTag.Pcm when format.wBitsPerSample == 16 => WaveSampleFormat.Pcm16,
            WaveFormatTag.IeeeFloat => throw new NotSupportedException(
                $"IEEE float capture at {format.wBitsPerSample} bits is not supported; only 32-bit float is."),
            WaveFormatTag.Pcm => throw new NotSupportedException(
                $"Integer PCM capture at {format.wBitsPerSample} bits is not supported; only 16-bit PCM is."),
            _ => throw new NotSupportedException(
                $"Unsupported wave format tag 0x{effectiveTag:X4}.")
        };

        return new WaveFormatInfo(
            (int)format.nSamplesPerSec,
            format.nChannels,
            format.wBitsPerSample,
            sampleFormat);
    }

    /// <summary>
    /// Maps a <c>WAVE_FORMAT_EXTENSIBLE</c> blob onto the plain tag its SubFormat GUID denotes.
    /// </summary>
    private static ushort ResolveExtensibleTag(ReadOnlySpan<byte> blob, in WAVEFORMATEX format)
    {
        if (format.cbSize < WaveFormatSizes.ExtensibleCbSize)
        {
            throw new NotSupportedException(
                $"WAVE_FORMAT_EXTENSIBLE declares cbSize {format.cbSize}; at least {WaveFormatSizes.ExtensibleCbSize} is required to read the SubFormat GUID.");
        }

        if (blob.Length < WaveFormatSizes.WaveFormatExtensible)
        {
            throw new NotSupportedException(
                $"WAVE_FORMAT_EXTENSIBLE blob is {blob.Length} bytes; {WaveFormatSizes.WaveFormatExtensible} are required.");
        }

        WAVEFORMATEXTENSIBLE extensible = MemoryMarshal.Read<WAVEFORMATEXTENSIBLE>(blob);

        if (extensible.SubFormat == WasapiGuids.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)
        {
            return WaveFormatTag.IeeeFloat;
        }

        if (extensible.SubFormat == WasapiGuids.KSDATAFORMAT_SUBTYPE_PCM)
        {
            return WaveFormatTag.Pcm;
        }

        throw new NotSupportedException(
            $"Unsupported WAVE_FORMAT_EXTENSIBLE SubFormat {extensible.SubFormat}.");
    }
}
