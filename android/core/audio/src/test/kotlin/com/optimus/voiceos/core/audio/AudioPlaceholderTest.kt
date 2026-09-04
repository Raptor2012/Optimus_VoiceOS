package com.optimus.voiceos.core.audio

import org.junit.Assert.assertEquals
import org.junit.Test

class AudioPlaceholderTest {
    @Test
    fun placeholderOwnedByT022() {
        assertEquals("T022", AudioPlaceholder.ownedBy)
    }
}
