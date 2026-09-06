package com.optimus.voiceos.core.audio

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class InterruptionAudioTest {
    @Test
    fun vadRejectsSilenceAndDetectsSpeech() {
        val vad = VoiceActivityDetector(0.02f)
        assertFalse(vad.isSpeech(ByteArray(640)))

        val voice = ByteArray(640)
        for (i in voice.indices step 2) {
            voice[i] = 0x40
            voice[i + 1] = 0x1f // 8000 PCM amplitude
        }
        assertTrue(vad.isSpeech(voice))
    }

    @Test
    fun preRollIsBoundedTo300MillisecondsAndKeepsNewestSamples() {
        val buffer = PcmPreRollBuffer(sampleRate = 16_000, durationMs = 300)
        buffer.append(ByteArray(9_600) { 1 })
        buffer.append(ByteArray(640) { 2 })

        val result = buffer.snapshot()
        assertEquals(9_600, result.size)
        assertEquals(1, result[0].toInt())
        assertEquals(2, result.last().toInt())
    }

    @Test
    fun stalePlaybackGenerationCannotRemainCurrent() {
        val generations = CancellationGeneration()
        val first = generations.next()
        val second = generations.next()

        assertFalse(generations.isCurrent(first))
        assertTrue(generations.isCurrent(second))
        assertEquals(second + 1, generations.invalidate())
        assertFalse(generations.isCurrent(second))
    }
}
