package com.optimus.voiceos.core.transport

import android.util.Log
import com.optimus.voiceos.core.protocol.PhoneFrame
import com.optimus.voiceos.core.protocol.PhoneFrameKind
import com.optimus.voiceos.core.protocol.PhoneFraming
import com.optimus.voiceos.core.protocol.TtsAudioCodec
import com.optimus.voiceos.core.protocol.TtsAudioSegment
import org.json.JSONObject
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

/** One destination the PC offers, with why it can or cannot send. */
data class PcDestination(val id: String, val name: String, val ready: Boolean, val detail: String)

/** What the PC told us. */
sealed interface PcEvent {
    data class Connected(val address: String) : PcEvent
    data class Disconnected(val reason: String) : PcEvent
    data class Status(val state: String, val line: String) : PcEvent
    data class Draft(val raw: String, val clean: String, val timings: String) : PcEvent
    data class Destinations(val destinations: List<PcDestination>) : PcEvent
    data class SendOutcome(val ok: Boolean, val destination: String, val detail: String) : PcEvent
    data class Failure(val message: String) : PcEvent
    data class TtsAudio(val segment: TtsAudioSegment) : PcEvent
    data class Playback(val generation: Long, val state: String) : PcEvent
    data class Chime(val generation: Long) : PcEvent
}

/**
 * One direct TCP connection to the PC, dialled at a manually configured address.
 *
 * Connection state lives only in memory. There is no pairing, no handshake and no resume: a
 * dropped link is fixed by dialling again, which is all this slice needs.
 */
class PhoneClient(private val onEvent: (PcEvent) -> Unit) {

    private companion object {
        const val TAG = "PhoneClient"
        const val CONNECT_TIMEOUT_MS = 5000
    }

    private val running = AtomicBoolean(false)
    private var socket: Socket? = null
    private var output: OutputStream? = null
    private val writeLock = Any()

    /**
     * Every socket write happens here.
     *
     * Android's StrictMode throws NetworkOnMainThreadException for socket I/O on the UI thread,
     * so calling startCapture() straight from a button tap killed the connection. A single
     * worker also preserves ordering between control messages and audio: startCapture is
     * queued before the first frame, stopCapture after the last.
     */
    private var writer: ExecutorService = Executors.newSingleThreadExecutor { r ->
        Thread(r, "phone-client-writer").apply { isDaemon = true }
    }

    val isConnected: Boolean
        get() = socket?.isConnected == true && socket?.isClosed == false

    fun connect(host: String, port: Int) {
        if (running.get()) disconnect()

        running.set(true)
        thread(name = "phone-client", isDaemon = true) {
            try {
                val s = Socket()
                s.tcpNoDelay = true
                s.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)

                socket = s
                synchronized(writeLock) { output = s.getOutputStream() }

                onEvent(PcEvent.Connected("$host:$port"))
                sendJson(JSONObject().put("t", "hello").put("device", "pixel"))

                readLoop(s)
            } catch (e: Exception) {
                Log.w(TAG, "connect failed", e)
                onEvent(PcEvent.Failure(e.message ?: e.javaClass.simpleName))
                onEvent(PcEvent.Disconnected(e.message ?: "connect failed"))
            } finally {
                closeQuietly()
            }
        }
    }

    private fun readLoop(s: Socket) {
        val input = s.getInputStream()
        var reason = "closed by PC"
        try {
            while (running.get()) {
                val frame: PhoneFrame = PhoneFraming.read(input) ?: break
                when (frame.kind) {
                    PhoneFrameKind.JSON -> dispatch(String(frame.payload, Charsets.UTF_8))
                    PhoneFrameKind.TTS_AUDIO -> try {
                        onEvent(PcEvent.TtsAudio(TtsAudioCodec.decode(frame.payload)))
                    } catch (e: IllegalArgumentException) {
                        onEvent(PcEvent.Failure(e.message ?: "Invalid TTS audio"))
                    }
                    PhoneFrameKind.AUDIO -> Unit // PC never sends microphone audio back.
                }
            }
        } catch (e: Exception) {
            reason = e.message ?: e.javaClass.simpleName
            Log.w(TAG, "read loop ended", e)
        } finally {
            onEvent(PcEvent.Disconnected(reason))
        }
    }

    private fun dispatch(json: String) {
        try {
            val o = JSONObject(json)
            when (o.optString("t")) {
                "status" -> onEvent(PcEvent.Status(o.optString("state"), o.optString("line")))
                "draft" -> onEvent(
                    PcEvent.Draft(
                        o.optString("raw"),
                        o.optString("clean"),
                        o.optString("timings")
                    )
                )
                "destinations" -> {
                    val array = o.optJSONArray("destinations")
                    val list = buildList {
                        for (i in 0 until (array?.length() ?: 0)) {
                            val d = array!!.getJSONObject(i)
                            add(
                                PcDestination(
                                    d.optString("id"),
                                    d.optString("name"),
                                    d.optBoolean("ready"),
                                    d.optString("detail")
                                )
                            )
                        }
                    }
                    onEvent(PcEvent.Destinations(list))
                }
                "sendResult" -> onEvent(
                    PcEvent.SendOutcome(
                        o.optBoolean("ok"),
                        o.optString("destination"),
                        o.optString("detail")
                    )
                )
                "error" -> onEvent(PcEvent.Failure(o.optString("message")))
                "playback" -> onEvent(PcEvent.Playback(o.optLong("generation"), o.optString("state")))
                "chime" -> onEvent(PcEvent.Chime(o.optLong("generation")))
                else -> Unit
            }
        } catch (e: Exception) {
            Log.w(TAG, "bad json: $json", e)
        }
    }

    fun startCapture() = sendJson(JSONObject().put("t", "startCapture"))

    fun stopCapture() = sendJson(JSONObject().put("t", "stopCapture"))

    fun refreshDestinations() = sendJson(JSONObject().put("t", "refreshDestinations"))

    /**
     * Confirms the draft, sending the exact text currently on screen.
     *
     * The caller passes what the user is looking at, including any edits. Nothing on the PC
     * substitutes its own copy.
     */
    fun confirm(destinationId: String, text: String) = sendJson(
        JSONObject().put("t", "confirm").put("destinationId", destinationId).put("text", text)
    )

    fun cancel() = sendJson(JSONObject().put("t", "cancel"))

    fun playbackDrained(generation: Long) = sendJson(
        JSONObject().put("t", "playbackDrained").put("generation", generation)
    )

    fun setNarrationSettings(mode: String, narrateToolsAndSkills: Boolean) = sendJson(
        JSONObject().put("t", "narrationSettings")
            .put("mode", mode)
            .put("narrateToolsAndSkills", narrateToolsAndSkills)
    )

    /**
     * Queues 16 kHz mono PCM16, exactly the format the PC pipeline expects.
     *
     * Always snapshots. Writes are handed to the writer thread and serialized later, while the
     * caller's buffer is normally one array the capture loop refills every 20 ms. Passing that
     * array straight through — which the old `length == pcm.size` shortcut did on every full
     * read, i.e. almost always — let the next chunk overwrite the bytes before they reached the
     * socket.
     */
    fun sendAudio(pcm: ByteArray, length: Int) {
        require(length in 0..pcm.size) { "length $length is outside the ${pcm.size}-byte buffer" }
        write(PhoneFrameKind.AUDIO, pcm.copyOf(length))
    }

    private fun sendJson(o: JSONObject) = write(PhoneFrameKind.JSON, o.toString().toByteArray(Charsets.UTF_8))

    private fun write(kind: PhoneFrameKind, payload: ByteArray) {
        try {
            writer.execute {
                synchronized(writeLock) {
                    val out = output ?: return@execute
                    try {
                        PhoneFraming.write(out, kind, payload)
                    } catch (e: Exception) {
                        Log.w(TAG, "write failed", e)
                        onEvent(PcEvent.Disconnected(e.message ?: "write failed"))
                        closeQuietly()
                    }
                }
            }
        } catch (e: RejectedExecutionException) {
            Log.w(TAG, "write rejected; client is shutting down", e)
        }
    }

    fun disconnect() {
        running.set(false)
        closeQuietly()
        writer.shutdownNow()
        // A fresh worker so reconnecting after a disconnect works without rebuilding the client.
        writer = Executors.newSingleThreadExecutor { r ->
            Thread(r, "phone-client-writer").apply { isDaemon = true }
        }
    }

    private fun closeQuietly() {
        synchronized(writeLock) { output = null }
        try {
            socket?.close()
        } catch (_: Exception) {
        }
        socket = null
    }
}
