package com.optimus.voiceos.core.transport

import com.optimus.voiceos.core.protocol.PhoneFrame
import com.optimus.voiceos.core.protocol.PhoneFrameKind
import com.optimus.voiceos.core.protocol.PhoneFraming
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.net.ServerSocket
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

/**
 * Drives [PhoneClient] against a real loopback socket.
 *
 * The interesting property is that audio queued from a buffer the caller immediately reuses
 * still arrives intact, because writes are serialized on a background thread well after the
 * call returns.
 */
class PhoneClientAudioTest {

    private lateinit var server: ServerSocket
    private val frames = CopyOnWriteArrayList<PhoneFrame>()
    private var client: PhoneClient? = null

    @Before
    fun setUp() {
        server = ServerSocket(0)
    }

    @After
    fun tearDown() {
        client?.disconnect()
        server.close()
    }

    private fun acceptInto(expected: Int): CountDownLatch {
        val latch = CountDownLatch(expected)
        thread(isDaemon = true) {
            try {
                val socket = server.accept()
                val input = socket.getInputStream()
                while (true) {
                    val frame = PhoneFraming.read(input) ?: break
                    frames.add(frame)
                    latch.countDown()
                }
            } catch (_: Exception) {
                // Socket closed at teardown.
            }
        }
        return latch
    }

    private fun connectClient(): PhoneClient {
        val connected = CountDownLatch(1)
        val c = PhoneClient { event -> if (event is PcEvent.Connected) connected.countDown() }
        client = c
        c.connect("127.0.0.1", server.localPort)
        assertTrue("client never connected", connected.await(5, TimeUnit.SECONDS))
        return c
    }

    /**
     * The regression: audio was queued without copying whenever the read filled the buffer, so
     * the capture loop's next chunk overwrote bytes that had not reached the socket yet. Both
     * frames used to arrive carrying the second chunk's contents.
     */
    @Test
    fun sendAudio_snapshotsABufferTheCallerImmediatelyReuses() {
        val latch = acceptInto(expected = 3) // hello + two audio frames
        val c = connectClient()

        // One buffer, refilled straight after each call, exactly like the capture loop.
        val buffer = ByteArray(640)

        buffer.fill(0x11)
        c.sendAudio(buffer, buffer.size)
        buffer.fill(0x22)
        c.sendAudio(buffer, buffer.size)
        buffer.fill(0x33) // must not reach either frame

        assertTrue("frames did not arrive", latch.await(5, TimeUnit.SECONDS))

        val audio = frames.filter { it.kind == PhoneFrameKind.AUDIO }
        assertEquals(2, audio.size)
        assertArrayEquals(ByteArray(640) { 0x11 }, audio[0].payload)
        assertArrayEquals(ByteArray(640) { 0x22 }, audio[1].payload)
    }

    /** A partial read must send only the bytes actually captured. */
    @Test
    fun sendAudio_sendsOnlyTheCapturedPrefix() {
        val latch = acceptInto(expected = 2) // hello + one audio frame
        val c = connectClient()

        val buffer = ByteArray(640)
        buffer.fill(0x7F)
        c.sendAudio(buffer, 100)

        assertTrue(latch.await(5, TimeUnit.SECONDS))

        val audio = frames.single { it.kind == PhoneFrameKind.AUDIO }
        assertEquals(100, audio.payload.size)
        assertArrayEquals(ByteArray(100) { 0x7F }, audio.payload)
    }

    /**
     * Control messages and audio share one writer, so a stopCapture queued after the last chunk
     * cannot overtake it. This is what makes "no audio after stopCapture" hold end to end once
     * MicCapture.stop joins its capture thread.
     */
    @Test
    fun controlAndAudioKeepSubmissionOrder() {
        val latch = acceptInto(expected = 5) // hello, startCapture, 2 audio, stopCapture
        val c = connectClient()

        c.startCapture()

        val buffer = ByteArray(640)
        buffer.fill(1)
        c.sendAudio(buffer, buffer.size)
        buffer.fill(2)
        c.sendAudio(buffer, buffer.size)

        c.stopCapture()

        assertTrue("frames did not arrive", latch.await(5, TimeUnit.SECONDS))

        val kinds = frames.map { it.kind }
        assertEquals(
            listOf(
                PhoneFrameKind.JSON,  // hello
                PhoneFrameKind.JSON,  // startCapture
                PhoneFrameKind.AUDIO,
                PhoneFrameKind.AUDIO,
                PhoneFrameKind.JSON   // stopCapture
            ),
            kinds
        )

        // And the last control frame really is stopCapture, after both chunks.
        assertTrue(String(frames.last().payload, Charsets.UTF_8).contains("stopCapture"))
    }

    @Test
    fun sendAudio_rejectsALengthOutsideTheBuffer() {
        val c = connectClient()
        val buffer = ByteArray(64)

        var threw = false
        try {
            c.sendAudio(buffer, 65)
        } catch (_: IllegalArgumentException) {
            threw = true
        }

        assertTrue("length beyond the buffer should be rejected", threw)
    }
}
