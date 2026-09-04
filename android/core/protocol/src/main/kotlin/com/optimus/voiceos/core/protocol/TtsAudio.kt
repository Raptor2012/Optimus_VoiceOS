package com.optimus.voiceos.core.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder

enum class PcmEncoding(val id: Byte) {
    PCM16_LE(1);

    companion object {
        fun fromId(id: Byte): PcmEncoding = entries.firstOrNull { it.id == id }
            ?: throw IllegalArgumentException("Unsupported PCM encoding $id")
    }
}

data class TtsAudioSegment(
    val generation: Long,
    val sequence: Int,
    val sampleRate: Int,
    val channels: Int,
    val encoding: PcmEncoding,
    val pcm: ByteArray
) {
    override fun equals(other: Any?): Boolean = other is TtsAudioSegment &&
        generation == other.generation && sequence == other.sequence &&
        sampleRate == other.sampleRate && channels == other.channels &&
        encoding == other.encoding && pcm.contentEquals(other.pcm)

    override fun hashCode(): Int = 31 * sequence + pcm.contentHashCode()
}

object TtsAudioCodec {
    const val HEADER_BYTES = 18

    fun encode(segment: TtsAudioSegment): ByteArray {
        validate(segment)
        return ByteBuffer.allocate(HEADER_BYTES + segment.pcm.size)
            .order(ByteOrder.BIG_ENDIAN)
            .putLong(segment.generation)
            .putInt(segment.sequence)
            .putInt(segment.sampleRate)
            .put(segment.channels.toByte())
            .put(segment.encoding.id)
            .put(segment.pcm)
            .array()
    }

    fun decode(payload: ByteArray): TtsAudioSegment {
        require(payload.size >= HEADER_BYTES) { "TTS audio metadata is truncated" }
        val buffer = ByteBuffer.wrap(payload).order(ByteOrder.BIG_ENDIAN)
        val generation = buffer.long
        val sequence = buffer.int
        val sampleRate = buffer.int
        val channels = buffer.get().toInt() and 0xff
        val encoding = PcmEncoding.fromId(buffer.get())
        val pcm = ByteArray(buffer.remaining()).also(buffer::get)
        return TtsAudioSegment(generation, sequence, sampleRate, channels, encoding, pcm)
            .also(::validate)
    }

    private fun validate(segment: TtsAudioSegment) {
        require(segment.generation >= 0) { "Generation cannot be negative" }
        require(segment.sequence >= 0) { "Sequence cannot be negative" }
        require(segment.sampleRate in 8000..192000) { "Sample rate is unsupported" }
        require(segment.channels in 1..2) { "Only mono or stereo PCM is supported" }
        require(segment.encoding == PcmEncoding.PCM16_LE) { "PCM encoding is unsupported" }
        require(segment.pcm.isNotEmpty() && segment.pcm.size % (segment.channels * 2) == 0) {
            "PCM payload is empty or not frame-aligned"
        }
        require(HEADER_BYTES + segment.pcm.size <= PhoneFraming.MAX_PAYLOAD_BYTES) {
            "TTS audio payload is too large"
        }
    }
}
