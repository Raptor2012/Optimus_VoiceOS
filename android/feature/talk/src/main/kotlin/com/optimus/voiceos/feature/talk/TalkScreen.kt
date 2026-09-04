package com.optimus.voiceos.feature.talk

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.selection.selectable
import androidx.compose.material3.Button
import androidx.compose.material3.RadioButton
import androidx.compose.material3.TextButton
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.optimus.voiceos.core.transport.PcDestination

val TalkUiState.hasDraft: Boolean get() = cleanedDraft.isNotBlank()

val TalkUiState.selectedDestination: PcDestination?
    get() = destinations.firstOrNull { it.id == selectedDestinationId }

/** Confirm is offered only with a draft and an explicitly chosen, ready destination. */
val TalkUiState.canConfirm: Boolean
    get() = connected && !sending && hasDraft && selectedDestination?.ready == true

/** Everything the Talk screen renders. Held by [TalkController]. */
data class TalkUiState(
    val host: String = "",
    val port: String = "8770",
    val connected: Boolean = false,
    val connectionLabel: String = "Not connected",
    val capturing: Boolean = false,
    val status: String = "Enter the PC address and connect",
    val rawTranscript: String = "",
    val cleanedDraft: String = "",
    val timings: String = "",
    val destinations: List<PcDestination> = emptyList(),
    val selectedDestinationId: String? = null,
    val sending: Boolean = false,
    /** The final summary: what actually happened to the confirmed prompt. */
    val sendSummary: String = "",
    val error: String = ""
)

@Composable
fun TalkScreen(
    state: TalkUiState,
    onHostChange: (String) -> Unit,
    onPortChange: (String) -> Unit,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    onStartCapture: () -> Unit,
    onStopCapture: () -> Unit,
    onDraftChange: (String) -> Unit = {},
    onSelectDestination: (String) -> Unit = {},
    onRefreshDestinations: () -> Unit = {},
    onConfirm: () -> Unit = {},
    onCancel: () -> Unit = {},
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier
            .fillMaxSize()
            // targetSdk 35 draws edge-to-edge on Android 15; without this the address field
            // sits under the status bar and the talk button under the navigation bar.
            .safeDrawingPadding()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text("Optimus Voice OS", fontSize = 22.sp, fontWeight = FontWeight.SemiBold)

        // The PC address is always typed in by hand; nothing is discovered or guessed.
        Row(
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            OutlinedTextField(
                value = state.host,
                onValueChange = onHostChange,
                label = { Text("PC address") },
                singleLine = true,
                enabled = !state.connected,
                modifier = Modifier.weight(2f)
            )
            OutlinedTextField(
                value = state.port,
                onValueChange = onPortChange,
                label = { Text("Port") },
                singleLine = true,
                enabled = !state.connected,
                modifier = Modifier.weight(1f)
            )
        }

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(
                onClick = onConnect,
                enabled = !state.connected && state.host.isNotBlank(),
                modifier = Modifier.weight(1f)
            ) { Text("Connect") }

            Button(
                onClick = onDisconnect,
                enabled = state.connected,
                modifier = Modifier.weight(1f)
            ) { Text("Disconnect") }
        }

        Text(state.connectionLabel, fontSize = 13.sp, color = MaterialTheme.colorScheme.primary)

        // Push-to-talk: held down, exactly like the desktop hotkey.
        Button(
            onClick = { if (state.capturing) onStopCapture() else onStartCapture() },
            enabled = state.connected,
            modifier = Modifier
                .fillMaxWidth()
                .height(96.dp)
        ) {
            Text(
                if (state.capturing) "Recording — tap to stop" else "Tap to talk",
                fontSize = 18.sp
            )
        }

        Text(state.status, fontSize = 15.sp)

        if (state.timings.isNotBlank()) {
            Text(
                state.timings,
                fontSize = 12.sp,
                fontFamily = FontFamily.Monospace,
                color = MaterialTheme.colorScheme.primary
            )
        }

        if (state.sendSummary.isNotBlank()) {
            Card(modifier = Modifier.fillMaxWidth()) {
                Text(
                    state.sendSummary,
                    modifier = Modifier.padding(12.dp),
                    fontSize = 14.sp,
                    fontWeight = FontWeight.SemiBold
                )
            }
        }

        if (state.rawTranscript.isNotBlank()) {
            Labelled("RAW TRANSCRIPT", state.rawTranscript)
        }

        if (state.hasDraft) {
            Text("CLEANED DRAFT (editable)", fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
            OutlinedTextField(
                value = state.cleanedDraft,
                onValueChange = onDraftChange,
                enabled = !state.sending,
                modifier = Modifier.fillMaxWidth(),
                minLines = 2
            )

            // Destination is always an explicit choice; nothing is preselected.
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("DESTINATION", fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
                TextButton(onClick = onRefreshDestinations) { Text("Refresh", fontSize = 12.sp) }
            }

            if (state.destinations.isEmpty()) {
                Text("No destinations reported by the PC.", fontSize = 13.sp)
            } else {
                state.destinations.forEach { destination ->
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .selectable(
                                selected = destination.id == state.selectedDestinationId,
                                enabled = !state.sending,
                                onClick = { onSelectDestination(destination.id) }
                            )
                            .padding(vertical = 4.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        RadioButton(
                            selected = destination.id == state.selectedDestinationId,
                            enabled = !state.sending,
                            onClick = { onSelectDestination(destination.id) }
                        )
                        Column(modifier = Modifier.padding(start = 4.dp)) {
                            Text(destination.name, fontSize = 14.sp)
                            Text(
                                destination.detail,
                                fontSize = 11.sp,
                                color = if (destination.ready) {
                                    MaterialTheme.colorScheme.primary
                                } else {
                                    MaterialTheme.colorScheme.error
                                }
                            )
                        }
                    }
                }
            }

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = onCancel,
                    enabled = !state.sending,
                    modifier = Modifier.weight(1f)
                ) { Text("Cancel") }

                Button(
                    onClick = onConfirm,
                    enabled = state.canConfirm,
                    modifier = Modifier.weight(1f)
                ) { Text(if (state.sending) "Sending..." else "Confirm & Send") }
            }

            // Say why Confirm is unavailable rather than leaving a dead button.
            val chosen = state.selectedDestination
            if (!state.sending) {
                when {
                    chosen == null -> Text("Choose a destination to enable Confirm.", fontSize = 12.sp)
                    !chosen.ready -> Text(
                        "${chosen.name} is not ready: ${chosen.detail}",
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.error
                    )
                    else -> Unit
                }
            }
        }

        if (state.error.isNotBlank()) {
            Card(modifier = Modifier.fillMaxWidth()) {
                Text(
                    state.error,
                    modifier = Modifier.padding(12.dp),
                    color = MaterialTheme.colorScheme.error,
                    fontSize = 13.sp
                )
            }
        }
    }
}

@Composable
private fun Labelled(label: String, value: String) {
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(label, fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
        Card(modifier = Modifier.fillMaxWidth()) {
            Text(value, modifier = Modifier.padding(12.dp), fontSize = 15.sp)
        }
    }
}
