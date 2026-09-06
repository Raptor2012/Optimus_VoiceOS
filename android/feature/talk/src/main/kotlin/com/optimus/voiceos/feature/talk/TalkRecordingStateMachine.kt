package com.optimus.voiceos.feature.talk

/**
 * Output actions driven by the state machine to control audio hardware and network protocol.
 */
sealed interface TalkRecordingAction {
    data object None : TalkRecordingAction
    data object StartUtteranceCapture : TalkRecordingAction
    data class SubmitUtterance(val retainConversation: Boolean) : TalkRecordingAction
    data class DiscardUtterance(val retainConversation: Boolean) : TalkRecordingAction
    data object EndConversation : TalkRecordingAction
    data object StartEchoCancellationMic : TalkRecordingAction
    data object StopEchoCancellationMic : TalkRecordingAction
}

/**
 * State machine managing gesture interactions and the 5 explicit voice interaction states:
 * 1. [conversationEnabled]: Continuous conversation mode is active.
 * 2. [audioDeviceRunning]: Local microphone hardware is actively capturing audio.
 * 3. [userUtteranceInProgress]: The user is currently speaking / recording an utterance.
 * 4. [playbackActive]: Assistant TTS playback is currently playing.
 * 5. [desktopRequestRunning]: An utterance or prompt has been submitted and is awaiting desktop response.
 */
class TalkRecordingStateMachine(
    initialConversationEnabled: Boolean = false,
    initialAudioDeviceRunning: Boolean = false,
    initialUserUtteranceInProgress: Boolean = false,
    initialPlaybackActive: Boolean = false,
    initialDesktopRequestRunning: Boolean = false
) {
    var conversationEnabled: Boolean = initialConversationEnabled
        private set
    var audioDeviceRunning: Boolean = initialAudioDeviceRunning
        private set
    var userUtteranceInProgress: Boolean = initialUserUtteranceInProgress
        private set
    var playbackActive: Boolean = initialPlaybackActive
        private set
    var desktopRequestRunning: Boolean = initialDesktopRequestRunning
        private set

    var isGestureActive: Boolean = false
        private set
    private var gestureDownTimeMs: Long = 0L

    companion object {
        const val HOLD_THRESHOLD_MS = 300L
    }

    /**
     * 1) Pointer-down starts temporary utterance capture.
     * 8) Recording-state update or recomposition cannot restart gesture ownership.
     * 9) Each gesture issues one start and one applicable end/cancel event.
     */
    fun onGestureDown(isConnected: Boolean, currentTimeMs: Long = System.currentTimeMillis()): TalkRecordingAction {
        if (!isConnected) return TalkRecordingAction.None
        if (isGestureActive) {
            // Already tracking this gesture; ignore redundant down events (e.g. multi-touch)
            return TalkRecordingAction.None
        }

        isGestureActive = true
        gestureDownTimeMs = currentTimeMs

        // If assistant speech was playing, interrupting it starts this temporary utterance capture
        playbackActive = false
        userUtteranceInProgress = true
        audioDeviceRunning = true
        desktopRequestRunning = false

        return TalkRecordingAction.StartUtteranceCapture
    }

    /**
     * 2) Release within 300ms starts or retains continuous conversation mode.
     * 3) Release after 300ms ends that held utterance.
     * 5) Later held utterance inside conversation mode submits on release then returns to conversational listening.
     * 9) Each gesture issues one start and one applicable end/cancel event.
     */
    fun onGestureUp(elapsedMs: Long? = null, currentTimeMs: Long = System.currentTimeMillis()): TalkRecordingAction {
        if (!isGestureActive) return TalkRecordingAction.None
        isGestureActive = false

        val duration = elapsedMs ?: (currentTimeMs - gestureDownTimeMs)
        userUtteranceInProgress = false

        return if (duration <= HOLD_THRESHOLD_MS) {
            // Tap release (<= 300ms): starts or retains continuous conversation mode
            conversationEnabled = true
            audioDeviceRunning = true
            TalkRecordingAction.None
        } else {
            // Held release (> 300ms): ends that held utterance and submits to desktop
            desktopRequestRunning = true
            if (conversationEnabled) {
                // Returns to conversational listening; microphone stays active
                audioDeviceRunning = true
                TalkRecordingAction.SubmitUtterance(retainConversation = true)
            } else {
                audioDeviceRunning = false
                TalkRecordingAction.SubmitUtterance(retainConversation = false)
            }
        }
    }

    /**
     * 6) Gesture cancellation discards temporary utterance without ending active conversation.
     * 9) Each gesture issues one start and one applicable end/cancel event.
     */
    fun onGestureCancel(): TalkRecordingAction {
        if (!isGestureActive) return TalkRecordingAction.None
        isGestureActive = false
        userUtteranceInProgress = false
        desktopRequestRunning = false

        return if (conversationEnabled) {
            // Active conversation is retained; audio device remains active for conversational listening
            audioDeviceRunning = true
            TalkRecordingAction.DiscardUtterance(retainConversation = true)
        } else {
            audioDeviceRunning = false
            TalkRecordingAction.DiscardUtterance(retainConversation = false)
        }
    }

    /**
     * 4) Separate End action ends continuous conversation mode.
     */
    fun onEndConversation(): TalkRecordingAction {
        conversationEnabled = false
        isGestureActive = false
        userUtteranceInProgress = false
        audioDeviceRunning = false
        desktopRequestRunning = false
        return TalkRecordingAction.EndConversation
    }

    /**
     * Assistant playback started.
     * Microphone is kept running for acoustic echo cancellation and interruption detection,
     * but must NOT display "Recording your message" (userUtteranceInProgress is false).
     */
    fun onPlaybackStarted(): TalkRecordingAction {
        playbackActive = true
        userUtteranceInProgress = false
        audioDeviceRunning = true
        desktopRequestRunning = false
        return TalkRecordingAction.StartEchoCancellationMic
    }

    /**
     * Assistant playback ended.
     */
    fun onPlaybackEnded(): TalkRecordingAction {
        playbackActive = false
        return if (conversationEnabled) {
            audioDeviceRunning = true
            TalkRecordingAction.None
        } else {
            audioDeviceRunning = false
            TalkRecordingAction.StopEchoCancellationMic
        }
    }

    /**
     * User interrupted playback by speaking.
     */
    fun onPlaybackInterrupted(): TalkRecordingAction {
        playbackActive = false
        userUtteranceInProgress = true
        audioDeviceRunning = true
        desktopRequestRunning = false
        return TalkRecordingAction.StartUtteranceCapture
    }

    /**
     * Desktop finished processing the request (draft received or sent).
     */
    fun onDesktopResponseReceived() {
        desktopRequestRunning = false
    }

    /**
     * Disconnected from PC.
     */
    fun onDisconnect() {
        isGestureActive = false
        conversationEnabled = false
        audioDeviceRunning = false
        userUtteranceInProgress = false
        playbackActive = false
        desktopRequestRunning = false
    }
}
