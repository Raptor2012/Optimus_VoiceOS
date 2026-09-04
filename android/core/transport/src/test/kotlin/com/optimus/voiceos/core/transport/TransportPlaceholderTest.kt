package com.optimus.voiceos.core.transport

import org.junit.Assert.assertEquals
import org.junit.Test

class TransportPlaceholderTest {
    @Test
    fun placeholderOwnedByT021() {
        assertEquals("T021", TransportPlaceholder.ownedBy)
    }
}
