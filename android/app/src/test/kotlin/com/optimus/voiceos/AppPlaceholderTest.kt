package com.optimus.voiceos

import org.junit.Assert.assertEquals
import org.junit.Test

object AppPlaceholder {
    const val ownedBy: String = "T021"
}

class AppPlaceholderTest {
    @Test
    fun appPlaceholderOwnedByT021() {
        assertEquals("T021", AppPlaceholder.ownedBy)
    }
}
