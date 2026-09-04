package com.optimus.voiceos.core.protocol

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream

/** What a frame carries. Must match the PC's `PhoneFrameKind`. */
enum class PhoneFrameKind(val id: Byte) {
    JSON(1),
    AUDIO(2),
    TTS_AUDIO(3);

    companion object {
        fun fromId(id: Byte): PhoneFrameKind? = entries.firstOrNull { it.id == id }
    }
}

data class PhoneFrame(val kind: PhoneFrameKind, val payload: ByteArray) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PhoneFrame) return false
        return kind == other.kind && payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int = 31 * kind.hashCode() + payload.contentHashCode()
}

/**
 * The whole wire format: one byte of kind, four bytes of big-endian length, then the payload.
 *
 * Both ends ship together, so there is exactly one format and nothing to negotiate.
 */
object PhoneFraming {
    const val HEADER_BYTES = 5
    const val MAX_PAYLOAD_BYTES = 1024 * 1024

    fun encode(kind: PhoneFrameKind, payload: ByteArray): ByteArray {
        require(payload.size <= MAX_PAYLOAD_BYTES) {
            "Payload of ${payload.size} bytes exceeds the $MAX_PAYLOAD_BYTES limit."
        }

        val frame = ByteArray(HEADER_BYTES + payload.size)
        frame[0] = kind.id
        val length = payload.size
        frame[1] = (length ushr 24).toByte()
        frame[2] = (length ushr 16).toByte()
        frame[3] = (length ushr 8).toByte()
        frame[4] = length.toByte()
        payload.copyInto(frame, HEADER_BYTES)
        return frame
    }

    fun write(output: OutputStream, kind: PhoneFrameKind, payload: ByteArray) {
        output.write(encode(kind, payload))
        output.flush()
    }

    /** Reads exactly one frame, or returns null when the peer closed cleanly. */
    fun read(input: InputStream): PhoneFrame? {
        val header = ByteArray(HEADER_BYTES)
        if (!readFully(input, header)) return null

        val kind = PhoneFrameKind.fromId(header[0])
            ?: throw IllegalStateException("Unknown frame kind ${header[0]}.")

        val length = ((header[1].toInt() and 0xFF) shl 24) or
            ((header[2].toInt() and 0xFF) shl 16) or
            ((header[3].toInt() and 0xFF) shl 8) or
            (header[4].toInt() and 0xFF)

        if (length < 0 || length > MAX_PAYLOAD_BYTES) {
            throw IllegalStateException("Frame length $length is out of range.")
        }

        val payload = ByteArray(length)
        if (length > 0 && !readFully(input, payload)) throw EOFException("Truncated frame body.")

        return PhoneFrame(kind, payload)
    }

    private fun readFully(input: InputStream, buffer: ByteArray): Boolean {
        var offset = 0
        while (offset < buffer.size) {
            val read = input.read(buffer, offset, buffer.size - offset)
            if (read < 0) return false
            offset += read
        }
        return true
    }
}
