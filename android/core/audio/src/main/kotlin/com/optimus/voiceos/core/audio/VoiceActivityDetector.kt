package com.optimus.voiceos.core.audio

import kotlin.math.sqrt

/** Small RMS VAD used after acoustic echo cancellation and before a frame is sent to the PC. */
class VoiceActivityDetector(private val speechThreshold: Float = 0.02f) {
    init { require(speechThreshold >= 0f) }

    fun isSpeech(pcm: ByteArray, length: Int = pcm.size): Boolean {
        require(length in 0..pcm.size)
        if (length < 2) return false
        var sum = 0.0
        var samples = 0
        var offset = 0
        while (offset + 1 < length) {
            val sample = (pcm[offset].toInt() and 0xff) or (pcm[offset + 1].toInt() shl 8)
            val signed = if (sample and 0x8000 != 0) sample - 0x10000 else sample
            sum += signed.toDouble() * signed.toDouble()
            samples++
            offset += 2
        }
        return samples > 0 && sqrt(sum / samples) / 32768.0 >= speechThreshold
    }
}

/** Bounded, sample-aligned in-memory audio retained so VAD onset does not clip the first word. */
class PcmPreRollBuffer(sampleRate: Int = 16_000, durationMs: Int = 300) {
    private val maxBytes = (sampleRate * durationMs / 1000 * 2).coerceAtLeast(2)
    private val lock = Any()
    private val bytes = ArrayDeque<Byte>()

    fun append(pcm: ByteArray, length: Int = pcm.size) {
        require(length in 0..pcm.size)
        synchronized(lock) {
            for (i in 0 until length) bytes.addLast(pcm[i])
            while (bytes.size > maxBytes) bytes.removeFirst()
            if (bytes.size % 2 != 0) bytes.removeFirst()
        }
    }

    fun snapshot(): ByteArray = synchronized(lock) { bytes.toByteArray() }

    fun clear() = synchronized(lock) { bytes.clear() }

    val sizeBytes: Int get() = synchronized(lock) { bytes.size }
}

/** Generation gate shared by microphone interruption and queued playback work. */
class CancellationGeneration(initial: Long = 0L) {
    private val generation = java.util.concurrent.atomic.AtomicLong(initial)

    fun current(): Long = generation.get()
    fun next(): Long = generation.incrementAndGet()
    fun invalidate(): Long = next()
    fun isCurrent(candidate: Long): Boolean = generation.get() == candidate
}
