package com.optimus.voiceos.feature.talk

import android.os.Handler
import android.os.Looper
import com.optimus.voiceos.core.audio.MicCapture
import com.optimus.voiceos.core.audio.TtsAudioPlayer
import com.optimus.voiceos.core.transport.PcEvent
import com.optimus.voiceos.core.transport.PhoneClient

/**
 * Wires the socket and the microphone to the Talk screen's state.
 *
 * All state is in memory and resets on disconnect. Reconnecting is just dialling the address
 * again, which is why nothing here tracks sessions or resume tokens.
 */
class TalkController(private val onState: (TalkUiState) -> Unit) {

    private val main = Handler(Looper.getMainLooper())
    private var state = TalkUiState()
    private val stateMachine = TalkRecordingStateMachine()

    private val client = PhoneClient { event -> main.post { handle(event) } }

    private val mic = MicCapture({ pcm, length ->
        // Straight out to the PC; nothing is accumulated on the phone.
        client.sendAudio(pcm, length)
    }, onSpeechStart = { preRoll ->
        if (player.isActive) interruptPlayback(preRoll)
    })

    private lateinit var player: TtsAudioPlayer

    init {
        player = TtsAudioPlayer(
            onActiveChanged = { active -> main.post {
                if (active) {
                    val action = stateMachine.onPlaybackStarted()
                    applyAction(action)
                    syncState { it.copy(status = "Speaking...") }
                } else {
                    val action = stateMachine.onPlaybackEnded()
                    applyAction(action)
                    syncState()
                }
            } },
            onDrained = client::playbackDrained,
            onFailure = { message -> main.post { update { it.copy(error = message, status = "Playback failed") } } }
        )
    }

    var onProjectsReceived: ((List<com.optimus.voiceos.core.transport.PcProjectItem>) -> Unit)? = null

    fun currentState(): TalkUiState = state

    fun setHost(host: String) = update { it.copy(host = host) }

    fun setPort(port: String) = update { it.copy(port = port.filter(Char::isDigit)) }

    fun requestProjects() = client.requestProjects()

    /** The user edits the draft here; this exact text is what gets confirmed. */
    fun setDraft(text: String) {
        update { it.copy(cleanedDraft = text, sendSummary = "") }
        client.editDraft(text)
    }

    /** Destination choice is always an explicit tap. Nothing is preselected. */
    fun selectDestination(id: String) {
        update { it.copy(selectedDestinationId = id, error = "") }
        client.selectDestination(id)
    }

    fun refreshDestinations() = client.refreshDestinations()

    fun setNarrationMode(mode: String) {
        update { it.copy(narrationMode = mode) }
        client.setNarrationSettings(mode, state.narrateToolsAndSkills)
    }

    fun setNarrateToolsAndSkills(enabled: Boolean) {
        update { it.copy(narrateToolsAndSkills = enabled) }
        client.setNarrationSettings(state.narrationMode, enabled)
    }

    /**
     * Sends the draft exactly as displayed, to the destination the user picked.
     */
    fun confirm() {
        val destination = state.selectedDestinationId
        val text = state.cleanedDraft

        if (destination.isNullOrBlank()) {
            update { it.copy(error = "Choose a destination first") }
            return
        }

        if (text.isBlank()) {
            update { it.copy(error = "Nothing to send") }
            return
        }

        update { it.copy(sending = true, error = "", sendSummary = "", status = "Sending...") }
        client.confirm(destination, text)
    }

    fun cancelDraft() {
        client.cancel()
        update {
            it.copy(
                rawTranscript = "",
                cleanedDraft = "",
                timings = "",
                sendSummary = "",
                sending = false,
                error = "",
                status = "Cancelled"
            )
        }
    }

    fun connect() {
        val port = state.port.toIntOrNull()
        if (port == null || port !in 1..65535) {
            update { it.copy(error = "Port must be between 1 and 65535") }
            return
        }

        update { it.copy(error = "", connectionLabel = "Connecting to ${state.host}:$port...") }
        client.connect(state.host.trim(), port)
    }

    fun disconnect() {
        onGestureCancel()
        endConversation()
        client.disconnect()
        stateMachine.onDisconnect()
        syncState {
            it.copy(
                connected = false,
                connectionLabel = "Not connected",
                status = "Disconnected"
            )
        }
    }

    /**
     * 1) Pointer-down starts temporary utterance capture.
     * 8) Recording-state update or recomposition cannot restart gesture ownership.
     * 9) Each gesture issues one start and one applicable end/cancel event.
     */
    fun onGestureDown() {
        if (!client.isConnected) {
            update { it.copy(error = "Not connected") }
            return
        }

        val action = stateMachine.onGestureDown(client.isConnected)
        if (action == TalkRecordingAction.StartUtteranceCapture) {
            update { it.forNewCapture() }
            applyAction(action)
        }
        syncState()
    }

    /**
     * 2) Release within 300ms starts or retains continuous conversation mode.
     * 3) Release after 300ms ends that held utterance.
     * 5) Later held utterance inside conversation mode submits on release then returns to conversational listening.
     */
    fun onGestureUp(elapsedMs: Long) {
        val action = stateMachine.onGestureUp(elapsedMs)
        applyAction(action)
        syncState()
    }

    /**
     * 6) Gesture cancellation discards temporary utterance without ending active conversation.
     */
    fun onGestureCancel() {
        val action = stateMachine.onGestureCancel()
        applyAction(action)
        syncState()
    }

    /**
     * 4) Separate End action ends continuous conversation mode.
     */
    fun endConversation() {
        val action = stateMachine.onEndConversation()
        applyAction(action)
        syncState { it.copy(status = "Conversation ended") }
    }

    fun onPermissionGranted() {
        update {
            it.copy(
                error = "",
                status = if (it.connected) "Hold to speak, or tap for continuous conversation" else "Enter the PC address and connect"
            )
        }
    }

    fun onPermissionDenied() {
        update { it.copy(error = "Microphone permission required") }
    }

    // Backwards-compatible methods
    fun startCapture() = onGestureDown()
    fun stopCapture() = onGestureUp(TalkRecordingStateMachine.HOLD_THRESHOLD_MS + 100)

    private fun applyAction(action: TalkRecordingAction) {
        when (action) {
            is TalkRecordingAction.StartUtteranceCapture -> {
                client.startCapture()
                if (!mic.start()) {
                    stateMachine.onGestureCancel()
                    update { it.copy(error = "Microphone unavailable") }
                }
            }
            is TalkRecordingAction.SubmitUtterance -> {
                client.stopCapture()
                if (!action.retainConversation) {
                    mic.stop()
                }
            }
            is TalkRecordingAction.DiscardUtterance -> {
                client.cancel()
                if (!action.retainConversation) {
                    mic.stop()
                }
            }
            is TalkRecordingAction.EndConversation -> {
                mic.stop()
                client.stopCapture()
            }
            is TalkRecordingAction.StartEchoCancellationMic -> {
                if (!mic.isRecording) mic.start()
            }
            is TalkRecordingAction.StopEchoCancellationMic -> {
                mic.stop()
            }
            is TalkRecordingAction.None -> Unit
        }
    }

    private fun syncState(transform: (TalkUiState) -> TalkUiState = { it }) {
        update { current ->
            val defaultStatus = when {
                current.error.isNotBlank() -> current.error
                stateMachine.playbackActive -> "Speaking..."
                stateMachine.userUtteranceInProgress -> "Recording..."
                stateMachine.desktopRequestRunning -> "Sent to PC, waiting for draft..."
                stateMachine.conversationEnabled -> "Conversation active — listening"
                current.sending -> "Sending..."
                current.hasDraft -> "Draft ready"
                current.connected -> "Hold to speak, or tap for continuous conversation"
                else -> "Enter the PC address and connect"
            }
            val synced = current.copy(
                conversationEnabled = stateMachine.conversationEnabled,
                audioDeviceRunning = stateMachine.audioDeviceRunning,
                userUtteranceInProgress = stateMachine.userUtteranceInProgress,
                playbackActive = stateMachine.playbackActive,
                desktopRequestRunning = stateMachine.desktopRequestRunning,
                capturing = stateMachine.userUtteranceInProgress,
                status = defaultStatus
            )
            transform(synced)
        }
    }

    private fun startApprovalCapture() {
        if (!client.isConnected) return
        if (!mic.start()) {
            update { it.copy(error = "Microphone unavailable") }
            return
        }
        update { it.copy(capturing = true, error = "", status = "Listening for approval...") }
    }

    private fun stopApprovalCapture() {
        mic.stop()
        update { it.copy(capturing = false, status = "Processing approval...") }
    }

    private fun startRedictationCapture() {
        if (!client.isConnected) return
        if (!mic.start()) {
            update { it.copy(error = "Microphone unavailable") }
            return
        }
        update { it.copy(capturing = true, error = "", status = "Listening for new draft... speak now") }
    }

    private fun handle(event: PcEvent) {
        when (event) {
            is PcEvent.Connected -> {
                client.requestProjects()
                stateMachine.onDisconnect()
                update {
                    it.copy(
                        connected = true,
                        connectionLabel = "Connected to ${event.address}",
                        error = "",
                        status = "Hold to speak, or tap for continuous conversation"
                    )
                }
            }

            is PcEvent.Projects -> onProjectsReceived?.invoke(event.projects)

            is PcEvent.Disconnected -> {
                mic.stop()
                player.reset()
                stateMachine.onDisconnect()
                syncState {
                    it.copy(
                        connected = false,
                        capturing = false,
                        sending = false,
                        destinations = emptyList(),
                        connectionLabel = "Not connected (${event.reason})",
                        status = "Disconnected"
                    )
                }
            }

            is PcEvent.Status -> update { it.copy(status = event.line) }

            is PcEvent.Destinations -> update { current ->
                // Drop a selection that no longer exists rather than silently retargeting.
                val stillThere = current.selectedDestinationId?.takeIf { id ->
                    event.destinations.any { it.id == id }
                }
                current.copy(destinations = event.destinations, selectedDestinationId = stillThere)
            }

            is PcEvent.SendOutcome -> {
                stateMachine.onDesktopResponseReceived()
                syncState { it.withSendOutcome(event) }
            }

            is PcEvent.Draft -> {
                stateMachine.onDesktopResponseReceived()
                val destId = event.destinationId ?: state.selectedDestinationId
                syncState {
                    it.copy(
                        rawTranscript = event.raw,
                        cleanedDraft = event.clean,
                        timings = event.timings,
                        selectedDestinationId = destId,
                        status = "Draft ready"
                    )
                }
            }

            is PcEvent.Failure -> {
                stateMachine.onDesktopResponseReceived()
                syncState { it.copy(error = event.message) }
            }

            is PcEvent.TtsAudio -> player.enqueue(event.segment)
            is PcEvent.Playback -> when (event.state) {
                "start" -> {
                    player.start(event.generation)
                    applyAction(stateMachine.onPlaybackStarted())
                    syncState { it.copy(status = "Speaking...") }
                }
                "end" -> {
                    player.finish(event.generation)
                    applyAction(stateMachine.onPlaybackEnded())
                    syncState()
                }
                "cancel" -> {
                    player.cancel(event.generation)
                    applyAction(stateMachine.onPlaybackEnded())
                    syncState()
                }
            }
            is PcEvent.Chime -> player.chime(event.generation)
            is PcEvent.StartApprovalCapture -> startApprovalCapture()
            is PcEvent.StopApprovalCapture -> stopApprovalCapture()
            is PcEvent.StartRedictationCapture -> startRedictationCapture()
            is PcEvent.DestinationSelected -> update { it.copy(selectedDestinationId = event.destinationId) }
            is PcEvent.AgentUpdate -> update { it.copy(status = event.text) }
        }
    }

    private fun interruptPlayback(preRoll: ByteArray) {
        val generation = player.activeGeneration
        if (!player.isActive || generation < 0L || !client.isConnected) return

        // Invalidate local queued frames and the PC's wait for playback completion immediately.
        player.cancel(generation)
        client.cancelPlayback(generation)
        client.startCapture()
        client.sendAudio(preRoll, preRoll.size)
        stateMachine.onPlaybackInterrupted()
        main.post {
            syncState { it.copy(error = "", status = "Interrupted — listening...") }
        }
    }

    private fun update(transform: (TalkUiState) -> TalkUiState) {
        state = transform(state)
        onState(state)
    }

    fun dispose() {
        mic.stop()
        player.close()
        client.disconnect()
    }
}

/** Follow-ups retain the user's selected conversation until explicitly switched. */
internal fun TalkUiState.forNewCapture(): TalkUiState = copy(
    rawTranscript = "",
    cleanedDraft = "",
    timings = "",
    sendSummary = ""
)

/** Success consumes the draft; failure keeps the exact text and destination available to retry. */
internal fun TalkUiState.withSendOutcome(event: PcEvent.SendOutcome): TalkUiState = copy(
    sending = false,
    rawTranscript = if (event.ok) "" else rawTranscript,
    cleanedDraft = if (event.ok) "" else cleanedDraft,
    timings = if (event.ok) "" else timings,
    selectedDestinationId = selectedDestinationId,
    sendSummary = if (event.ok) {
        "Sent to " + event.destination
    } else {
        "NOT sent to " + event.destination + ": " + event.detail
    },
    error = if (event.ok) "" else event.detail,
    status = if (event.ok) "Sent" else "Not sent"
)
