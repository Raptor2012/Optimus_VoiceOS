package com.optimus.voiceos.feature.sessions

import org.junit.Assert.assertEquals
import org.junit.Test

class SessionsPlaceholderTest {
    @Test
    fun placeholderOwnedByT023() {
        assertEquals("T023", SessionsPlaceholder.ownedBy)
    }
}
