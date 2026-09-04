package com.optimus.voiceos.feature.pairing

import org.junit.Assert.assertEquals
import org.junit.Test

class PairingPlaceholderTest {
    @Test
    fun placeholderOwnedByT021() {
        assertEquals("T021", PairingPlaceholder.ownedBy)
    }
}
