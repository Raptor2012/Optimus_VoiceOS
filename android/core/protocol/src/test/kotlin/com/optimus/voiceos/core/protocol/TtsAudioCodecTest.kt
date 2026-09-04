package com.optimus.voiceos.core.protocol

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class TtsAudioCodecTest {
    @Test fun roundTripsMetadataAndPcm() {
        val original = TtsAudioSegment(7, 2, 22050, 1, PcmEncoding.PCM16_LE, byteArrayOf(1, 2, 3, 4))
        val decoded = TtsAudioCodec.decode(TtsAudioCodec.encode(original))
        assertEquals(7, decoded.generation)
        assertEquals(2, decoded.sequence)
        assertEquals(22050, decoded.sampleRate)
        assertEquals(1, decoded.channels)
        assertEquals(PcmEncoding.PCM16_LE, decoded.encoding)
        assertArrayEquals(original.pcm, decoded.pcm)
    }

    @Test fun rejectsUnalignedPcm() {
        val bad = TtsAudioSegment(1, 0, 22050, 1, PcmEncoding.PCM16_LE, byteArrayOf(1))
        assertThrows(IllegalArgumentException::class.java) { TtsAudioCodec.encode(bad) }
    }

    @Test fun framingPreservesTtsKind() {
        val payload = TtsAudioCodec.encode(
            TtsAudioSegment(1, 0, 48000, 2, PcmEncoding.PCM16_LE, byteArrayOf(1, 2, 3, 4))
        )
        val decoded = PhoneFraming.read(PhoneFraming.encode(PhoneFrameKind.TTS_AUDIO, payload).inputStream())!!
        assertEquals(PhoneFrameKind.TTS_AUDIO, decoded.kind)
    }
}
