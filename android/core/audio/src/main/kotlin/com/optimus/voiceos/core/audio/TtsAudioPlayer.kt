package com.optimus.voiceos.core.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import com.optimus.voiceos.core.protocol.PcmEncoding
import com.optimus.voiceos.core.protocol.TtsAudioSegment
import java.util.concurrent.Executors
import kotlin.math.PI
import kotlin.math.sin

internal interface StreamingPcmSink {
    val sampleRate: Int
    val channels: Int
    fun write(pcm: ByteArray)
    fun drain()
    fun stop()
}

internal fun interface StreamingPcmSinkFactory {
    fun create(sampleRate: Int, channels: Int): StreamingPcmSink
}

/** Ordered, generation-aware playback for speech streamed by the PC. */
class TtsAudioPlayer internal constructor(
    private val sinkFactory: StreamingPcmSinkFactory,
    private val onActiveChanged: (Boolean) -> Unit,
    private val onDrained: (Long) -> Unit,
    private val onFailure: (String) -> Unit = {}
) {
    constructor(
        onActiveChanged: (Boolean) -> Unit,
        onDrained: (Long) -> Unit,
        onFailure: (String) -> Unit = {}
    ) : this(
        StreamingPcmSinkFactory(::AndroidAudioTrackSink), onActiveChanged, onDrained, onFailure
    )

    private val worker = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "phone-tts-player").apply { isDaemon = true }
    }
    private val lock = Any()
    @Volatile private var generation = -1L
    @Volatile private var sink: StreamingPcmSink? = null
    @Volatile var isActive = false
        private set
    private var nextSequence = 0

    fun start(newGeneration: Long) {
        require(newGeneration >= 0)
        val oldSink: StreamingPcmSink?
        synchronized(lock) {
            if (newGeneration < generation) return
            oldSink = sink
            sink = null
            generation = newGeneration
            nextSequence = 0
            setActive(true)
        }
        oldSink?.stop()
    }

    fun enqueue(segment: TtsAudioSegment) {
        require(segment.encoding == PcmEncoding.PCM16_LE)
        worker.execute {
            try {
                val current: StreamingPcmSink
                synchronized(lock) {
                    if (segment.generation != generation || !isActive) return@execute
                    if (segment.sequence != nextSequence) {
                        cancelLocked(segment.generation)
                        return@execute
                    }
                    nextSequence++
                    var selected = sink
                    if (selected == null || selected.sampleRate != segment.sampleRate || selected.channels != segment.channels) {
                        selected?.stop()
                        selected = sinkFactory.create(segment.sampleRate, segment.channels)
                        sink = selected
                    }
                    current = selected
                }
                // Do not hold lock during a blocking AudioTrack write: cancel must be able to stop it.
                current.write(segment.pcm)
            } catch (error: Exception) {
                fail(segment.generation, error)
            }
        }
    }

    /** Adds the approval-ready cue to the same ordered audio queue. */
    fun chime(forGeneration: Long) {
        worker.execute {
            try {
                val current: StreamingPcmSink
                synchronized(lock) {
                    if (forGeneration != generation || !isActive) return@execute
                    var selected = sink
                    if (selected == null) {
                        selected = sinkFactory.create(22050, 1)
                        sink = selected
                    }
                    current = selected
                }
                current.write(makeChime(current.sampleRate, current.channels))
            } catch (error: Exception) {
                fail(forGeneration, error)
            }
        }
    }

    /** Drained means the hardware playback head consumed all queued frames, not merely write(). */
    fun finish(forGeneration: Long) {
        worker.execute {
            val current: StreamingPcmSink?
            synchronized(lock) {
                if (forGeneration != generation || !isActive) return@execute
                current = sink
            }
            try {
                current?.drain()
            } catch (error: Exception) {
                fail(forGeneration, error)
                return@execute
            }
            synchronized(lock) {
                if (forGeneration != generation || !isActive) return@execute
                current?.stop()
                sink = null
                setActive(false)
            }
            onDrained(forGeneration)
        }
    }

    fun cancel(forGeneration: Long) {
        synchronized(lock) {
            if (forGeneration != generation) return
            cancelLocked(forGeneration)
        }
    }

    fun reset() {
        val oldSink: StreamingPcmSink?
        synchronized(lock) {
            oldSink = sink
            sink = null
            generation = -1L
            nextSequence = 0
            setActive(false)
        }
        try { oldSink?.stop() } catch (_: Exception) { }
    }

    private fun cancelLocked(forGeneration: Long) {
        if (forGeneration != generation) return
        generation++ // invalidates already queued work immediately
        try { sink?.stop() } catch (_: Exception) { }
        sink = null
        setActive(false)
    }

    private fun fail(forGeneration: Long, error: Exception) {
        synchronized(lock) {
            if (forGeneration != generation) return
            cancelLocked(forGeneration)
        }
        onFailure(error.message ?: "Phone audio playback failed")
    }

    private fun setActive(value: Boolean) {
        if (isActive == value) return
        isActive = value
        onActiveChanged(value)
    }

    fun close() {
        synchronized(lock) {
            try { sink?.stop() } catch (_: Exception) { }
            sink = null
            generation++
            setActive(false)
        }
        worker.shutdownNow()
    }

    private fun makeChime(sampleRate: Int, channels: Int): ByteArray {
        val frames = sampleRate * 80 / 1000
        val pcm = ByteArray(frames * channels * 2)
        for (frame in 0 until frames) {
            val fade = 1.0 - frame.toDouble() / frames
            val sample = (sin(2.0 * PI * 880.0 * frame / sampleRate) * 7000 * fade).toInt().toShort()
            for (channel in 0 until channels) {
                val offset = (frame * channels + channel) * 2
                pcm[offset] = (sample.toInt() and 0xff).toByte()
                pcm[offset + 1] = (sample.toInt() ushr 8).toByte()
            }
        }
        return pcm
    }
}

private class AndroidAudioTrackSink(
    override val sampleRate: Int,
    override val channels: Int
) : StreamingPcmSink {
    private val channelMask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
    private val bytesPerFrame = channels * 2
    private val track: AudioTrack
    private var bytesWritten = 0L

    init {
        val minimum = AudioTrack.getMinBufferSize(sampleRate, channelMask, AudioFormat.ENCODING_PCM_16BIT)
        require(minimum > 0) { "AudioTrack does not support $sampleRate Hz / $channels channels" }
        track = AudioTrack(
            AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_ASSISTANCE_ACCESSIBILITY)
                .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build(),
            AudioFormat.Builder().setSampleRate(sampleRate).setChannelMask(channelMask)
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT).build(),
            minimum.coerceAtLeast(sampleRate * bytesPerFrame / 5),
            AudioTrack.MODE_STREAM,
            AudioManager.AUDIO_SESSION_ID_GENERATE
        )
        check(track.state == AudioTrack.STATE_INITIALIZED) { "AudioTrack initialization failed" }
        track.play()
    }

    override fun write(pcm: ByteArray) {
        var offset = 0
        while (offset < pcm.size) {
            val count = track.write(pcm, offset, pcm.size - offset, AudioTrack.WRITE_BLOCKING)
            check(count > 0) { "AudioTrack write failed: $count" }
            offset += count
            bytesWritten += count
        }
    }

    override fun drain() {
        val framesWritten = bytesWritten / bytesPerFrame
        val deadline = System.currentTimeMillis() + (framesWritten * 1000 / sampleRate) + 2000
        while (unsignedPlaybackHead() < framesWritten && System.currentTimeMillis() < deadline) {
            Thread.sleep(5)
        }
        check(unsignedPlaybackHead() >= framesWritten) { "AudioTrack playback did not drain" }
    }

    private fun unsignedPlaybackHead(): Long = track.playbackHeadPosition.toLong() and 0xffffffffL

    override fun stop() {
        try { track.pause() } catch (_: IllegalStateException) { }
        track.flush()
        track.release()
    }
}
