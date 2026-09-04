package com.optimus.voiceos.feature.talk

import org.junit.Assert.assertEquals
import org.junit.Test

class TalkPlaceholderTest {
    @Test
    fun placeholderOwnedByT022() {
        assertEquals("T022", TalkPlaceholder.ownedBy)
    }
}
