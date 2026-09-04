package com.optimus.voiceos.core.security

import org.junit.Assert.assertEquals
import org.junit.Test

class SecurityPlaceholderTest {
    @Test
    fun placeholderOwnedByT021() {
        assertEquals("T021", SecurityPlaceholder.ownedBy)
    }
}
