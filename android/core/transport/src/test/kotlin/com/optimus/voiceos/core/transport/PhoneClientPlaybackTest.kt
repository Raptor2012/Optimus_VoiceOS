package com.optimus.voiceos.core.transport

import com.optimus.voiceos.core.protocol.PcmEncoding
import com.optimus.voiceos.core.protocol.PhoneFrameKind
import com.optimus.voiceos.core.protocol.PhoneFraming
import com.optimus.voiceos.core.protocol.TtsAudioCodec
import com.optimus.voiceos.core.protocol.TtsAudioSegment
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.ServerSocket
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

class PhoneClientPlaybackTest {
    @Test fun decodesPlaybackStreamInWireOrder() {
        ServerSocket(0).use { server ->
            val events = CopyOnWriteArrayList<PcEvent>()
            val received = CountDownLatch(4)
            val client = PhoneClient { event ->
                if (event is PcEvent.Playback || event is PcEvent.TtsAudio || event is PcEvent.Chime) {
                    events += event
                    received.countDown()
                }
            }
            thread(isDaemon = true) {
                server.accept().use { socket ->
                    PhoneFraming.read(socket.getInputStream()) // hello
                    val output = socket.getOutputStream()
                    json(output, JSONObject().put("t", "playback").put("generation", 5).put("state", "start"))
                    val segment = TtsAudioSegment(5, 0, 22050, 1, PcmEncoding.PCM16_LE, byteArrayOf(1, 0))
                    PhoneFraming.write(output, PhoneFrameKind.TTS_AUDIO, TtsAudioCodec.encode(segment))
                    json(output, JSONObject().put("t", "chime").put("generation", 5))
                    json(output, JSONObject().put("t", "playback").put("generation", 5).put("state", "end"))
                    Thread.sleep(100)
                }
            }

            client.connect("127.0.0.1", server.localPort)
            assertTrue(received.await(5, TimeUnit.SECONDS))
            assertEquals("start", (events[0] as PcEvent.Playback).state)
            assertEquals(22050, (events[1] as PcEvent.TtsAudio).segment.sampleRate)
            assertTrue(events[2] is PcEvent.Chime)
            assertEquals("end", (events[3] as PcEvent.Playback).state)
            client.disconnect()
        }
    }

    @Test fun decodesApprovalCaptureAndDestinationEvents() {
        ServerSocket(0).use { server ->
            val events = CopyOnWriteArrayList<PcEvent>()
            val received = CountDownLatch(5)
            val client = PhoneClient { event ->
                if (event !is PcEvent.Connected) {
                    events += event
                    received.countDown()
                }
            }
            thread(isDaemon = true) {
                server.accept().use { socket ->
                    PhoneFraming.read(socket.getInputStream()) // hello
                    val output = socket.getOutputStream()
                    json(output, JSONObject().put("t", "startApprovalCapture"))
                    json(output, JSONObject().put("t", "stopApprovalCapture"))
                    json(output, JSONObject().put("t", "startRedictationCapture"))
                    json(output, JSONObject().put("t", "destinationSelected").put("destinationId", "claude"))
                    json(output, JSONObject().put("t", "draft").put("raw", "r").put("clean", "c").put("timings", "t").put("destinationId", "claude"))
                    Thread.sleep(100)
                }
            }

            client.connect("127.0.0.1", server.localPort)
            assertTrue(received.await(5, TimeUnit.SECONDS))
            assertTrue(events[0] is PcEvent.StartApprovalCapture)
            assertTrue(events[1] is PcEvent.StopApprovalCapture)
            assertTrue(events[2] is PcEvent.StartRedictationCapture)
            assertEquals("claude", (events[3] as PcEvent.DestinationSelected).destinationId)
            assertEquals("claude", (events[4] as PcEvent.Draft).destinationId)
            client.disconnect()
        }
    }

    @Test fun sendsEditDraftEventToServer() {
        ServerSocket(0).use { server ->
            val received = CountDownLatch(1)
            var receivedJson = ""
            thread(isDaemon = true) {
                server.accept().use { socket ->
                    PhoneFraming.read(socket.getInputStream()) // hello
                    val frame = PhoneFraming.read(socket.getInputStream())
                    if (frame != null) {
                        receivedJson = String(frame.payload, Charsets.UTF_8)
                    }
                    received.countDown()
                }
            }

            val connected = CountDownLatch(1)
            val client = PhoneClient { event ->
                if (event is PcEvent.Connected) connected.countDown()
            }
            client.connect("127.0.0.1", server.localPort)
            assertTrue(connected.await(5, TimeUnit.SECONDS))
            client.editDraft("new edited text")
            assertTrue(received.await(5, TimeUnit.SECONDS))
            val obj = JSONObject(receivedJson)
            assertEquals("editDraft", obj.getString("t"))
            assertEquals("new edited text", obj.getString("text"))
            client.disconnect()
        }
    }

    private fun json(output: java.io.OutputStream, value: JSONObject) =
        PhoneFraming.write(output, PhoneFrameKind.JSON, value.toString().toByteArray(Charsets.UTF_8))
}
