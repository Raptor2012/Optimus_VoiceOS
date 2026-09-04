package com.optimus.voiceos.core.audio

import com.optimus.voiceos.core.protocol.PcmEncoding
import com.optimus.voiceos.core.protocol.TtsAudioSegment
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Collections
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class TtsAudioPlayerTest {
    private class FakeSink(override val sampleRate: Int, override val channels: Int) : StreamingPcmSink {
        val writes = Collections.synchronizedList(mutableListOf<Byte>())
        @Volatile var drained = false
        @Volatile var stopped = false
        override fun write(pcm: ByteArray) { writes += pcm.first() }
        override fun drain() { drained = true }
        override fun stop() { stopped = true }
    }

    @Test fun preservesSegmentOrderAndSignalsOnlyAfterDrain() {
        lateinit var sink: FakeSink
        val drained = CountDownLatch(1)
        var callbackSawDrain = false
        val player = TtsAudioPlayer(
            StreamingPcmSinkFactory { rate, channels -> FakeSink(rate, channels).also { sink = it } },
            {},
            { callbackSawDrain = sink.drained; drained.countDown() }
        )
        player.start(4)
        player.enqueue(segment(4, 0, 10))
        player.enqueue(segment(4, 1, 20))
        player.finish(4)

        assertTrue(drained.await(2, TimeUnit.SECONDS))
        assertEquals(listOf<Byte>(10, 20), sink.writes)
        assertTrue(callbackSawDrain)
        assertFalse(player.isActive)
        player.close()
    }

    @Test fun cancelDiscardsQueuedAndStaleGenerationAudio() {
        val sinks = Collections.synchronizedList(mutableListOf<FakeSink>())
        val drained = CountDownLatch(1)
        val player = TtsAudioPlayer(
            StreamingPcmSinkFactory { rate, channels -> FakeSink(rate, channels).also(sinks::add) },
            {}, { drained.countDown() }
        )
        player.start(8)
        player.cancel(8)
        player.enqueue(segment(8, 0, 1))
        player.finish(8)
        player.start(9)
        player.enqueue(segment(8, 0, 2))
        player.enqueue(segment(9, 0, 3))
        player.finish(9)

        assertTrue(drained.await(2, TimeUnit.SECONDS))
        assertEquals(listOf<Byte>(3), sinks.flatMap { it.writes })
        player.close()
    }

    @Test fun activePlaybackBlocksRecordingBoundaryUntilDrain() {
        val active = Collections.synchronizedList(mutableListOf<Boolean>())
        val drained = CountDownLatch(1)
        val player = TtsAudioPlayer(
            StreamingPcmSinkFactory { rate, channels -> FakeSink(rate, channels) },
            active::add, { drained.countDown() }
        )
        player.start(2)
        assertTrue(player.isActive)
        player.enqueue(segment(2, 0, 1))
        player.finish(2)
        assertTrue(drained.await(2, TimeUnit.SECONDS))
        assertEquals(listOf(true, false), active)
        player.close()
    }

    private fun segment(generation: Long, sequence: Int, marker: Byte) = TtsAudioSegment(
        generation, sequence, 22050, 1, PcmEncoding.PCM16_LE, byteArrayOf(marker, 0)
    )
}
