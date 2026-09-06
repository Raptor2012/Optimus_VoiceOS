package com.optimus.voiceos.feature.talk

import com.optimus.voiceos.core.transport.PcDestination
import com.optimus.voiceos.core.ui.VoiceOrbState
import org.junit.Assert.assertEquals
import org.junit.Test

class VoiceOrbStateMappingTest {

    @Test
    fun errorStateOverridesOtherStates() {
        val state = TalkUiState(error = "Microphone unavailable", capturing = true)
        assertEquals(VoiceOrbState.Error, state.toVoiceOrbState())
    }

    @Test
    fun sendingStateMappedCorrectly() {
        val state = TalkUiState(sending = true, cleanedDraft = "test")
        assertEquals(VoiceOrbState.Sending, state.toVoiceOrbState())
    }

    @Test
    fun capturingMappedToListening() {
        val state = TalkUiState(capturing = true, connected = true)
        assertEquals(VoiceOrbState.Listening, state.toVoiceOrbState())
    }

    @Test
    fun sentSummaryMappedToSent() {
        val state = TalkUiState(sendSummary = "Sent to Claude")
        assertEquals(VoiceOrbState.Sent, state.toVoiceOrbState())
    }

    @Test
    fun speakingStatusMappedToReadingDraft() {
        val state = TalkUiState(status = "Speaking draft...")
        assertEquals(VoiceOrbState.ReadingDraft, state.toVoiceOrbState())
    }

    @Test
    fun approvalStatusMappedToAwaitingApproval() {
        val state = TalkUiState(status = "Listening for approval...")
        assertEquals(VoiceOrbState.AwaitingApproval, state.toVoiceOrbState())
    }

    @Test
    fun draftReadyMappedToConfirm() {
        val state = TalkUiState(cleanedDraft = "Ship this vertical slice", connected = true)
        assertEquals(VoiceOrbState.Confirm, state.toVoiceOrbState())
    }

    @Test
    fun idleStateWhenConnectedWithNoDraft() {
        val state = TalkUiState(connected = true)
        assertEquals(VoiceOrbState.Idle, state.toVoiceOrbState())
    }

    @Test
    fun selectedDestinationNameResolvesAccurately() {
        val withPcDest = TalkUiState(
            selectedDestinationId = "claude",
            destinations = listOf(PcDestination("claude", "Claude Desktop", true, "Ready"))
        )
        assertEquals("Claude Desktop", withPcDest.selectedDestinationName)

        val withOnlyId = TalkUiState(selectedDestinationId = "antigravity")
        assertEquals("Antigravity", withOnlyId.selectedDestinationName)

        val withNone = TalkUiState()
        assertEquals("Voice Destination", withNone.selectedDestinationName)
    }

    @Test
    fun playbackActiveMappedToReadingDraftEvenWhenAudioDeviceRunning() {
        // Microphone running for echo cancellation during assistant speech must map to ReadingDraft, not Listening
        val state = TalkUiState(
            playbackActive = true,
            audioDeviceRunning = true,
            userUtteranceInProgress = false
        )
        assertEquals(VoiceOrbState.ReadingDraft, state.toVoiceOrbState())
    }

    @Test
    fun userUtteranceInProgressMappedToListening() {
        val state = TalkUiState(
            userUtteranceInProgress = true,
            audioDeviceRunning = true
        )
        assertEquals(VoiceOrbState.Listening, state.toVoiceOrbState())
    }

    @Test
    fun continuousConversationActiveMappedToSessionListening() {
        val state = TalkUiState(
            conversationEnabled = true,
            audioDeviceRunning = true,
            userUtteranceInProgress = false
        )
        assertEquals(VoiceOrbState.SessionListening, state.toVoiceOrbState())
    }

    @Test
    fun desktopRequestRunningMappedToProcessing() {
        val state = TalkUiState(
            desktopRequestRunning = true,
            userUtteranceInProgress = false
        )
        assertEquals(VoiceOrbState.Processing, state.toVoiceOrbState())
    }
}
