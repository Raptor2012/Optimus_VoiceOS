package com.optimus.voiceos.feature.talk

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TalkRecordingStateMachineTest {

    @Test
    fun pointerDownStartsTemporaryUtteranceCapture() {
        val sm = TalkRecordingStateMachine()
        val action = sm.onGestureDown(isConnected = true)

        assertEquals(TalkRecordingAction.StartUtteranceCapture, action)
        assertTrue(sm.isGestureActive)
        assertTrue(sm.userUtteranceInProgress)
        assertTrue(sm.audioDeviceRunning)
        assertFalse(sm.desktopRequestRunning)
        assertFalse(sm.conversationEnabled)
    }

    @Test
    fun releaseWithin300MsStartsContinuousConversationMode() {
        val sm = TalkRecordingStateMachine()
        sm.onGestureDown(isConnected = true)

        val action = sm.onGestureUp(elapsedMs = 200)

        assertEquals(TalkRecordingAction.None, action)
        assertFalse(sm.isGestureActive)
        assertTrue(sm.conversationEnabled)
        assertTrue(sm.audioDeviceRunning)
        assertFalse(sm.userUtteranceInProgress)
    }

    @Test
    fun releaseWithin300MsRetainsExistingContinuousConversationMode() {
        val sm = TalkRecordingStateMachine(initialConversationEnabled = true, initialAudioDeviceRunning = true)
        sm.onGestureDown(isConnected = true)

        val action = sm.onGestureUp(elapsedMs = 100)

        assertEquals(TalkRecordingAction.None, action)
        assertFalse(sm.isGestureActive)
        assertTrue(sm.conversationEnabled)
        assertTrue(sm.audioDeviceRunning)
        assertFalse(sm.userUtteranceInProgress)
    }

    @Test
    fun releaseAfter300MsEndsHeldUtteranceAndSubmits() {
        val sm = TalkRecordingStateMachine()
        sm.onGestureDown(isConnected = true)

        val action = sm.onGestureUp(elapsedMs = 850)

        assertEquals(TalkRecordingAction.SubmitUtterance(retainConversation = false), action)
        assertFalse(sm.isGestureActive)
        assertFalse(sm.userUtteranceInProgress)
        assertTrue(sm.desktopRequestRunning)
        assertFalse(sm.audioDeviceRunning)
        assertFalse(sm.conversationEnabled)
    }

    @Test
    fun laterHeldUtteranceInsideConversationModeSubmitsOnReleaseThenReturnsToConversationalListening() {
        // Continuous conversation mode active
        val sm = TalkRecordingStateMachine(initialConversationEnabled = true, initialAudioDeviceRunning = true)

        // 1) Pointer-down starts temporary utterance
        val downAction = sm.onGestureDown(isConnected = true)
        assertEquals(TalkRecordingAction.StartUtteranceCapture, downAction)
        assertTrue(sm.userUtteranceInProgress)
        assertTrue(sm.audioDeviceRunning)

        // 5) Later held utterance inside conversation mode submits on release then returns to conversational listening
        val upAction = sm.onGestureUp(elapsedMs = 1200)
        assertEquals(TalkRecordingAction.SubmitUtterance(retainConversation = true), upAction)
        assertFalse(sm.userUtteranceInProgress)
        assertTrue(sm.desktopRequestRunning)
        assertTrue(sm.conversationEnabled)
        assertTrue(sm.audioDeviceRunning) // Microphone ready for conversational listening
    }

    @Test
    fun gestureCancellationDiscardsTemporaryUtteranceWithoutEndingActiveConversation() {
        val sm = TalkRecordingStateMachine(initialConversationEnabled = true, initialAudioDeviceRunning = true)
        sm.onGestureDown(isConnected = true)

        val action = sm.onGestureCancel()

        assertEquals(TalkRecordingAction.DiscardUtterance(retainConversation = true), action)
        assertFalse(sm.isGestureActive)
        assertFalse(sm.userUtteranceInProgress)
        assertFalse(sm.desktopRequestRunning)
        assertTrue(sm.conversationEnabled) // Active conversation is retained!
        assertTrue(sm.audioDeviceRunning)
    }

    @Test
    fun gestureCancellationOutsideConversationStopsAudioDevice() {
        val sm = TalkRecordingStateMachine()
        sm.onGestureDown(isConnected = true)

        val action = sm.onGestureCancel()

        assertEquals(TalkRecordingAction.DiscardUtterance(retainConversation = false), action)
        assertFalse(sm.isGestureActive)
        assertFalse(sm.userUtteranceInProgress)
        assertFalse(sm.desktopRequestRunning)
        assertFalse(sm.conversationEnabled)
        assertFalse(sm.audioDeviceRunning)
    }

    @Test
    fun separateEndActionEndsContinuousConversationMode() {
        val sm = TalkRecordingStateMachine(initialConversationEnabled = true, initialAudioDeviceRunning = true)

        val action = sm.onEndConversation()

        assertEquals(TalkRecordingAction.EndConversation, action)
        assertFalse(sm.conversationEnabled)
        assertFalse(sm.audioDeviceRunning)
        assertFalse(sm.userUtteranceInProgress)
        assertFalse(sm.desktopRequestRunning)
    }

    @Test
    fun eachGestureIssuesOneStartAndOneApplicableEndOrCancelEvent() {
        val sm = TalkRecordingStateMachine()

        // Down starts gesture
        val action1 = sm.onGestureDown(isConnected = true)
        assertEquals(TalkRecordingAction.StartUtteranceCapture, action1)

        // Redundant down while active is ignored
        val action2 = sm.onGestureDown(isConnected = true)
        assertEquals(TalkRecordingAction.None, action2)

        // Release finishes gesture
        val action3 = sm.onGestureUp(elapsedMs = 500)
        assertEquals(TalkRecordingAction.SubmitUtterance(retainConversation = false), action3)

        // Duplicate release or cancel is ignored
        val action4 = sm.onGestureUp(elapsedMs = 500)
        assertEquals(TalkRecordingAction.None, action4)
        val action5 = sm.onGestureCancel()
        assertEquals(TalkRecordingAction.None, action5)
    }

    @Test
    fun assistantSpeechMicrophoneForAecDoesNotShowUserUtteranceInProgress() {
        val sm = TalkRecordingStateMachine()

        val action = sm.onPlaybackStarted()
        assertEquals(TalkRecordingAction.StartEchoCancellationMic, action)
        assertTrue(sm.playbackActive)
        assertTrue(sm.audioDeviceRunning)
        assertFalse(sm.userUtteranceInProgress) // MUST NOT be true

        // Playback finishes outside conversation
        val endAction = sm.onPlaybackEnded()
        assertEquals(TalkRecordingAction.StopEchoCancellationMic, endAction)
        assertFalse(sm.playbackActive)
        assertFalse(sm.audioDeviceRunning)
    }

    @Test
    fun assistantSpeechInsideConversationModeReturnsToListeningOnEnd() {
        val sm = TalkRecordingStateMachine(initialConversationEnabled = true, initialAudioDeviceRunning = true)

        sm.onPlaybackStarted()
        assertTrue(sm.playbackActive)
        assertTrue(sm.audioDeviceRunning)
        assertFalse(sm.userUtteranceInProgress)

        sm.onPlaybackEnded()
        assertFalse(sm.playbackActive)
        assertTrue(sm.audioDeviceRunning) // Keeps mic running for conversational listening
        assertTrue(sm.conversationEnabled)
    }

    @Test
    fun assistantSpeechInterruptedBySpeechStartsUtteranceCapture() {
        val sm = TalkRecordingStateMachine()
        sm.onPlaybackStarted()

        val action = sm.onPlaybackInterrupted()
        assertEquals(TalkRecordingAction.StartUtteranceCapture, action)
        assertFalse(sm.playbackActive)
        assertTrue(sm.userUtteranceInProgress)
        assertTrue(sm.audioDeviceRunning)
    }

    @Test
    fun disconnectedResetsAllInteractionStates() {
        val sm = TalkRecordingStateMachine(
            initialConversationEnabled = true,
            initialAudioDeviceRunning = true,
            initialUserUtteranceInProgress = true,
            initialPlaybackActive = true,
            initialDesktopRequestRunning = true
        )

        sm.onDisconnect()

        assertFalse(sm.isGestureActive)
        assertFalse(sm.conversationEnabled)
        assertFalse(sm.audioDeviceRunning)
        assertFalse(sm.userUtteranceInProgress)
        assertFalse(sm.playbackActive)
        assertFalse(sm.desktopRequestRunning)
    }

    @Test
    fun gestureDownWhenDisconnectedIsNoOp() {
        val sm = TalkRecordingStateMachine()
        val action = sm.onGestureDown(isConnected = false)

        assertEquals(TalkRecordingAction.None, action)
        assertFalse(sm.isGestureActive)
        assertFalse(sm.userUtteranceInProgress)
    }
}
