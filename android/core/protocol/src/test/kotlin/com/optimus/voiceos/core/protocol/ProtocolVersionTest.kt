package com.optimus.voiceos.core.protocol

import org.junit.Assert.assertEquals
import org.junit.Test

class ProtocolVersionTest {
    @Test
    fun versionMatchesExpectedFormat() {
        assertEquals("${ProtocolVersion.major}.${ProtocolVersion.minor}", ProtocolVersion.current)
        assertEquals("optimus.v1", ProtocolVersion.subProtocol)
    }
}
