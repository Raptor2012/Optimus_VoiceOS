package com.optimus.voiceos.feature.talk

import android.os.Handler
import android.os.Looper
import com.optimus.voiceos.core.audio.MicCapture
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

    fun currentState(): TalkUiState = state

    fun setHost(host: String) = update { it.copy(host = host) }

    fun setPort(port: String) = update { it.copy(port = port.filter(Char::isDigit)) }

    /** The user edits the draft here; this exact text is what gets confirmed. */
    fun setDraft(text: String) = update { it.copy(cleanedDraft = text, sendSummary = "") }

    /** Destination choice is always an explicit tap. Nothing is preselected. */
    fun selectDestination(id: String) = update { it.copy(selectedDestinationId = id, error = "") }

    fun refreshDestinations() = client.refreshDestinations()

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
            it.copy(
                capturing = true,
                error = "",
                rawTranscript = "",
                cleanedDraft = "",
                timings = "",
                sendSummary = "",
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

    private fun handle(event: PcEvent) {
        when (event) {
            is PcEvent.Connected -> update {
                it.copy(
                    connected = true,
                    connectionLabel = "Connected to ${event.address}",
                    error = ""
                )
            }

            is PcEvent.Disconnected -> {
                mic.stop()
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

            is PcEvent.SendOutcome -> update {
                it.copy(
                    sending = false,
                    sendSummary = if (event.ok) {
                        "Sent to " + event.destination
                    } else {
                        "NOT sent to " + event.destination + ": " + event.detail
                    },
                    error = if (event.ok) "" else event.detail,
                    status = if (event.ok) "Sent" else "Not sent"
                )
            }

            is PcEvent.Draft -> update {
                it.copy(
                    rawTranscript = event.raw,
                    cleanedDraft = event.clean,
                    timings = event.timings,
                    status = "Draft ready"
                )
            }

            is PcEvent.Failure -> update { it.copy(error = event.message) }
        }
    }

    private fun update(transform: (TalkUiState) -> TalkUiState) {
        state = transform(state)
        onState(state)
    }

    fun dispose() {
        mic.stop()
        client.disconnect()
    }
}
