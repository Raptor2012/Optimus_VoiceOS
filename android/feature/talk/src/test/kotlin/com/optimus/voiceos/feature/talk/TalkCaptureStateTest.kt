package com.optimus.voiceos.feature.talk

import com.optimus.voiceos.core.transport.PcEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class TalkCaptureStateTest {
    @Test
    fun newCaptureClearsDraftButKeepsConversation() {
        val previous = TalkUiState(
            rawTranscript = "raw",
            cleanedDraft = "clean",
            timings = "fast",
            selectedDestinationId = "claude",
            sendSummary = "not sent"
        )

        val next = previous.forNewCapture()

        assertEquals("", next.rawTranscript)
        assertEquals("", next.cleanedDraft)
        assertEquals("", next.timings)
        assertEquals("", next.sendSummary)
        assertEquals("claude", next.selectedDestinationId)
    }

    @Test
    fun successfulSendConsumesDraftAndKeepsConversation() {
        val sending = TalkUiState(
            rawTranscript = "raw",
            cleanedDraft = "edited draft",
            timings = "fast",
            selectedDestinationId = "claude",
            sending = true
        )

        val sent = sending.withSendOutcome(PcEvent.SendOutcome(true, "Claude", "delivered"))

        assertEquals("", sent.cleanedDraft)
        assertEquals("claude", sent.selectedDestinationId)
        assertEquals("Sent to Claude", sent.sendSummary)
    }

    @Test
    fun failedSendKeepsExactDraftAndDestinationForRetry() {
        val sending = TalkUiState(
            cleanedDraft = "edited draft",
            selectedDestinationId = "claude",
            sending = true
        )

        val failed = sending.withSendOutcome(PcEvent.SendOutcome(false, "Claude", "focus failed"))

        assertEquals("edited draft", failed.cleanedDraft)
        assertEquals("claude", failed.selectedDestinationId)
        assertEquals("focus failed", failed.error)
    }
}
