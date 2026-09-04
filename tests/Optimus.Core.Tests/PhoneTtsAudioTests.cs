namespace Optimus.Core.Tests;

using System.IO;
using Optimus.Core.Phone;
using Xunit;

public sealed class PhoneTtsAudioTests
{
    [Fact]
    public void Segment_round_trips_metadata_and_pcm()
    {
        var original = new PhoneTtsAudioSegment(42, 3, 22050, 1,
            PhonePcmEncoding.Pcm16LittleEndian, new byte[] { 1, 2, 3, 4 });

        PhoneTtsAudioSegment decoded = PhoneTtsAudio.Decode(PhoneTtsAudio.Encode(original));

        Assert.Equal(original.Generation, decoded.Generation);
        Assert.Equal(original.Sequence, decoded.Sequence);
        Assert.Equal(original.SampleRate, decoded.SampleRate);
        Assert.Equal(original.Channels, decoded.Channels);
        Assert.Equal(original.Encoding, decoded.Encoding);
        Assert.Equal(original.Pcm, decoded.Pcm);
    }

    [Theory]
    [InlineData(7999, 1, 2)]
    [InlineData(22050, 0, 2)]
    [InlineData(22050, 1, 3)]
    public void Invalid_audio_metadata_is_rejected(int sampleRate, byte channels, int pcmBytes)
    {
        var segment = new PhoneTtsAudioSegment(1, 0, sampleRate, channels,
            PhonePcmEncoding.Pcm16LittleEndian, new byte[pcmBytes]);

        Assert.Throws<InvalidDataException>(() => PhoneTtsAudio.Encode(segment));
    }

    [Fact]
    public void Tts_audio_uses_its_own_frame_kind()
    {
        byte[] payload = PhoneTtsAudio.Encode(new PhoneTtsAudioSegment(1, 0, 22050, 1,
            PhonePcmEncoding.Pcm16LittleEndian, new byte[] { 0, 0 }));

        byte[] frame = PhoneFraming.Encode(PhoneFrameKind.TtsAudio, payload);

        Assert.Equal((byte)PhoneFrameKind.TtsAudio, frame[0]);
    }
}
