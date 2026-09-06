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

    private val client = PhoneClient { event -> main.post { handle(event) } }

    private val mic = MicCapture { pcm, length ->
        // Straight out to the PC; nothing is accumulated on the phone.
        client.sendAudio(pcm, length)
    }

    private val player = TtsAudioPlayer(
        onActiveChanged = { active -> main.post {
            if (active) {
                mic.stop()
                update { it.copy(capturing = false, status = "Speaking...") }
            }
        } },
        onDrained = client::playbackDrained,
        onFailure = { message -> main.post { update { it.copy(error = message, status = "Playback failed") } } }
    )

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
     *
     * Refuses rather than guessing when no destination is chosen or the draft is empty, and the
     * screen only shows Sent once the PC confirms the adapter actually delivered it.
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
        stopCapture()
        client.disconnect()
        update {
            it.copy(
                connected = false,
                connectionLabel = "Not connected",
                status = "Disconnected"
            )
        }
    }

    fun startCapture() {
        if (player.isActive) {
            update { it.copy(error = "Wait for speech playback to finish") }
            return
        }
        if (!client.isConnected) {
            update { it.copy(error = "Not connected") }
            return
        }

        client.startCapture()

        if (!mic.start()) {
            client.stopCapture()
            update { it.copy(error = "Microphone unavailable", capturing = false) }
            return
        }

        update {
            it.forNewCapture().copy(
                capturing = true,
                error = "",
                status = "Recording..."
            )
        }
    }

    fun stopCapture() {
        if (!state.capturing) return

        mic.stop()
        client.stopCapture()
        update { it.copy(capturing = false, status = "Sent to PC, waiting for draft...") }
    }

    private fun startApprovalCapture() {
        if (player.isActive) return
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
        if (player.isActive) return
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
                update {
                    it.copy(
                        connected = true,
                        connectionLabel = "Connected to ${event.address}",
                        error = ""
                    )
                }
            }

            is PcEvent.Projects -> onProjectsReceived?.invoke(event.projects)

            is PcEvent.Disconnected -> {
                mic.stop()
                player.reset()
                update {
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

            is PcEvent.SendOutcome -> update { it.withSendOutcome(event) }

            is PcEvent.Draft -> update {
                val destId = event.destinationId ?: it.selectedDestinationId
                it.copy(
                    rawTranscript = event.raw,
                    cleanedDraft = event.clean,
                    timings = event.timings,
                    selectedDestinationId = destId,
                    status = "Draft ready"
                )
            }

            is PcEvent.Failure -> update { it.copy(error = event.message) }

            is PcEvent.TtsAudio -> player.enqueue(event.segment)
            is PcEvent.Playback -> when (event.state) {
                "start" -> player.start(event.generation)
                "end" -> player.finish(event.generation)
                "cancel" -> player.cancel(event.generation)
            }
            is PcEvent.Chime -> player.chime(event.generation)
            is PcEvent.StartApprovalCapture -> startApprovalCapture()
            is PcEvent.StopApprovalCapture -> stopApprovalCapture()
            is PcEvent.StartRedictationCapture -> startRedictationCapture()
            is PcEvent.DestinationSelected -> update { it.copy(selectedDestinationId = event.destinationId) }
            is PcEvent.Projects -> Unit
            is PcEvent.AgentUpdate -> update { it.copy(status = event.text) }
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
