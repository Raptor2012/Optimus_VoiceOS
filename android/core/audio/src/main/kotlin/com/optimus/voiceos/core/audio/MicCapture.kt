package com.optimus.voiceos.core.audio

import android.annotation.SuppressLint
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.util.Log
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

/**
 * Microphone capture producing the canonical 16 kHz mono PCM16 the PC pipeline expects.
 *
 * The device records directly at 16 kHz rather than resampling later, so the bytes leaving the
 * phone are already in the one format the rest of the system uses. Audio is streamed straight
 * out and never buffered to storage.
 */
class MicCapture(private val onPcm: (ByteArray, Int) -> Unit) {

    private companion object {
        const val TAG = "MicCapture"
        const val SAMPLE_RATE = 16000
        /** 20 ms at 16 kHz mono PCM16. */
        const val CHUNK_BYTES = 640
    }

    private val running = AtomicBoolean(false)
    private var record: AudioRecord? = null

    val isRecording: Boolean get() = running.get()

    /** Caller must already hold RECORD_AUDIO. */
    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        if (running.get()) return true

        val minBuffer = AudioRecord.getMinBufferSize(
            SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT
        )

        if (minBuffer <= 0) {
            Log.e(TAG, "getMinBufferSize returned $minBuffer")
            return false
        }

        val bufferBytes = maxOf(minBuffer, CHUNK_BYTES * 8)

        val r = try {
            AudioRecord(
                MediaRecorder.AudioSource.VOICE_RECOGNITION,
                SAMPLE_RATE,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT,
                bufferBytes
            )
        } catch (e: Exception) {
            Log.e(TAG, "AudioRecord construction failed", e)
            return false
        }

        if (r.state != AudioRecord.STATE_INITIALIZED) {
            Log.e(TAG, "AudioRecord not initialized (state ${r.state})")
            r.release()
            return false
        }

        record = r
        running.set(true)
        r.startRecording()

        thread(name = "mic-capture", isDaemon = true) {
            val buffer = ByteArray(CHUNK_BYTES)
            while (running.get()) {
                val read = r.read(buffer, 0, buffer.size)
                if (read > 0) {
                    onPcm(buffer, read)
                } else if (read < 0) {
                    Log.e(TAG, "AudioRecord.read returned $read")
                    break
                }
            }
        }

        return true
    }

    fun stop() {
        if (!running.getAndSet(false)) return

        val r = record
        record = null
        try {
            r?.stop()
        } catch (e: IllegalStateException) {
            Log.w(TAG, "stop failed", e)
        }
        r?.release()
    }
}
