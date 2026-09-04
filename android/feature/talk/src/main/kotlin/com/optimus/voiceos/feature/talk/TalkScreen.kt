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
import androidx.compose.material3.Button
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

        if (state.rawTranscript.isNotBlank()) {
            Labelled("RAW TRANSCRIPT", state.rawTranscript)
        }

        if (state.cleanedDraft.isNotBlank()) {
            Labelled("CLEANED DRAFT", state.cleanedDraft)
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
