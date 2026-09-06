package com.optimus.voiceos.feature.talk

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.optimus.voiceos.core.transport.PcDestination
import com.optimus.voiceos.core.ui.VoiceOrb
import com.optimus.voiceos.core.ui.VoiceOrbState
import com.optimus.voiceos.ui.theme.OptimusTokens
import kotlinx.coroutines.launch

val TalkUiState.hasDraft: Boolean get() = cleanedDraft.isNotBlank()

val TalkUiState.selectedDestination: PcDestination?
    get() = destinations.firstOrNull { it.id == selectedDestinationId }

val TalkUiState.selectedDestinationName: String
    get() = selectedDestination?.name
        ?: selectedDestinationId?.replaceFirstChar { it.uppercase() }
        ?: "Voice Destination"

/** Confirm is offered only with a draft and an explicitly chosen, ready destination. */
val TalkUiState.canConfirm: Boolean
    get() = connected && !sending && hasDraft && (selectedDestination?.ready == true || (destinations.isEmpty() && selectedDestinationId != null))

fun TalkUiState.toVoiceOrbState(): VoiceOrbState {
    return when {
        error.isNotBlank() -> VoiceOrbState.Error
        sending -> VoiceOrbState.Sending
        playbackActive || status.contains("Speaking", ignoreCase = true) -> VoiceOrbState.ReadingDraft
        userUtteranceInProgress -> VoiceOrbState.Listening
        conversationEnabled && audioDeviceRunning -> VoiceOrbState.SessionListening
        capturing -> VoiceOrbState.Listening
        sendSummary.startsWith("Sent to") -> VoiceOrbState.Sent
        status.contains("approval", ignoreCase = true) -> VoiceOrbState.AwaitingApproval
        desktopRequestRunning ||
            status.contains("Processing", ignoreCase = true) ||
            status.contains("Transcribing", ignoreCase = true) ||
            status.contains("waiting for draft", ignoreCase = true) -> VoiceOrbState.Processing
        status.contains("new draft", ignoreCase = true) -> VoiceOrbState.Redictating
        hasDraft -> VoiceOrbState.Confirm
        connected -> VoiceOrbState.Idle
        else -> VoiceOrbState.Idle
    }
}

/** Everything the Talk screen renders. Held by TalkController. */
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
    val sendSummary: String = "",
    val error: String = "",
    val narrationMode: String = "concise",
    val narrateToolsAndSkills: Boolean = false,
    val conversationEnabled: Boolean = false,
    val audioDeviceRunning: Boolean = false,
    val userUtteranceInProgress: Boolean = false,
    val playbackActive: Boolean = false,
    val desktopRequestRunning: Boolean = false
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TalkScreen(
    state: TalkUiState,
    onHostChange: (String) -> Unit,
    onPortChange: (String) -> Unit,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    onStartCapture: () -> Unit = {},
    onStopCapture: () -> Unit = {},
    onGestureStart: () -> Unit = onStartCapture,
    onGestureEnd: (Long) -> Unit = { elapsed -> if (elapsed > 300) onStopCapture() },
    onGestureCancel: () -> Unit = onStopCapture,
    onEndConversation: () -> Unit = {},
    onDraftChange: (String) -> Unit = {},
    onSelectDestination: (String) -> Unit = {},
    onRefreshDestinations: () -> Unit = {},
    onConfirm: () -> Unit = {},
    onCancel: () -> Unit = {},
    onNarrationModeChange: (String) -> Unit = {},
    onNarrateToolsChange: (Boolean) -> Unit = {},
    modifier: Modifier = Modifier
) {
    var showConnectionDialog by rememberSaveable { mutableStateOf(false) }
    var showDestinationSheet by rememberSaveable { mutableStateOf(false) }
    var showRawTranscript by rememberSaveable { mutableStateOf(false) }
    val sheetState = rememberModalBottomSheetState()
    val coroutineScope = rememberCoroutineScope()

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(OptimusTokens.Background)
            .imePadding()
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 12.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(14.dp)
        ) {
            // TOP HEADER: Brand & Connection Status
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column {
                    Text(
                        text = "OPTIMUS",
                        fontSize = 20.sp,
                        letterSpacing = 2.sp,
                        fontWeight = FontWeight.Bold,
                        color = OptimusTokens.TextPrimary
                    )
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(6.dp)
                    ) {
                        Box(
                            modifier = Modifier
                                .size(7.dp)
                                .clip(CircleShape)
                                .background(if (state.connected) OptimusTokens.Success else OptimusTokens.TextTertiary)
                        )
                        Text(
                            text = if (state.connected) state.connectionLabel else "Offline",
                            fontSize = 11.sp,
                            color = if (state.connected) OptimusTokens.Accent else OptimusTokens.TextSecondary
                        )
                    }
                }

                IconButton(
                    onClick = { showConnectionDialog = true },
                    modifier = Modifier.size(OptimusTokens.MinTouchTarget)
                ) {
                    Icon(
                        imageVector = Icons.Default.Settings,
                        contentDescription = "Connection settings",
                        tint = OptimusTokens.TextSecondary
                    )
                }
            }

            // 1. LOCKED VOICE DESTINATION DISPLAY
            val destTitle = state.selectedDestination?.name
                ?: state.selectedDestinationId?.replaceFirstChar { it.uppercase() }
                ?: "No Destination Selected"
            val destDetail = state.selectedDestination?.detail
                ?: if (state.selectedDestinationId != null) "Explicitly locked destination" else "Tap to choose destination target"

            Card(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(OptimusTokens.CornerCard)
                    .border(1.dp, OptimusTokens.Border, OptimusTokens.CornerCard)
                    .clickable { showDestinationSheet = true },
                shape = OptimusTokens.CornerCard,
                colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(14.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                        modifier = Modifier.weight(1f)
                    ) {
                        Surface(
                            shape = CircleShape,
                            color = OptimusTokens.SurfaceRaised,
                            modifier = Modifier.size(36.dp)
                        ) {
                            Box(contentAlignment = Alignment.Center) {
                                Icon(
                                    imageVector = Icons.Default.Lock,
                                    contentDescription = null,
                                    tint = OptimusTokens.Accent,
                                    modifier = Modifier.size(16.dp)
                                )
                            }
                        }

                        Column {
                            Text(
                                text = "VOICE DESTINATION LOCKED",
                                fontSize = 10.sp,
                                fontWeight = FontWeight.Bold,
                                color = OptimusTokens.TextSecondary
                            )
                            Text(
                                text = destTitle,
                                fontSize = 15.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = OptimusTokens.TextPrimary
                            )
                            Text(
                                text = destDetail,
                                fontSize = 11.sp,
                                color = if (state.selectedDestination?.ready == false) OptimusTokens.Error else OptimusTokens.Accent
                            )
                        }
                    }

                    TextButton(
                        onClick = { showDestinationSheet = true },
                        modifier = Modifier.defaultMinSize(minHeight = OptimusTokens.MinTouchTarget)
                    ) {
                        Text("Change", fontSize = 12.sp, color = OptimusTokens.Accent)
                    }
                }
            }

            // 2. VOICE ORB (Animated, matching WPF states)
            Spacer(modifier = Modifier.height(6.dp))
            VoiceOrb(
                state = state.toVoiceOrbState(),
                size = 148.dp,
                isCapturing = state.userUtteranceInProgress,
                conversationEnabled = state.conversationEnabled,
                onGestureStart = onGestureStart,
                onGestureEnd = onGestureEnd,
                onGestureCancel = onGestureCancel
            )

            // If continuous conversation mode is enabled, show an End action
            if (state.conversationEnabled) {
                OutlinedButton(
                    onClick = onEndConversation,
                    modifier = Modifier
                        .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                    shape = OptimusTokens.CornerControl,
                    colors = ButtonDefaults.outlinedButtonColors(
                        contentColor = OptimusTokens.Error
                    )
                ) {
                    Icon(
                        imageVector = Icons.Default.Close,
                        contentDescription = "End conversation",
                        modifier = Modifier.size(16.dp),
                        tint = OptimusTokens.Error
                    )
                    Spacer(modifier = Modifier.width(6.dp))
                    Text(
                        text = "End Conversation",
                        fontSize = 13.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = OptimusTokens.Error
                    )
                }
            }

            // Current voice status prompt
            Text(
                text = when {
                    state.playbackActive || state.status.contains("Speaking", ignoreCase = true) -> "Speaking..."
                    state.userUtteranceInProgress -> "Recording — Release or tap to stop"
                    state.conversationEnabled -> "Conversation active — listening"
                    state.desktopRequestRunning -> "Sent to PC, waiting for draft..."
                    state.status.isNotBlank() &&
                        !state.status.equals("Listening...", ignoreCase = true) &&
                        !state.status.equals("Recording...", ignoreCase = true) -> state.status
                    state.connected -> "Hold to speak, or tap for continuous conversation"
                    else -> "Connect to PC to talk"
                },
                fontSize = 14.sp,
                fontWeight = FontWeight.Medium,
                color = when {
                    state.playbackActive -> OptimusTokens.Reading
                    state.userUtteranceInProgress -> OptimusTokens.Listening
                    state.conversationEnabled -> OptimusTokens.Success
                    else -> OptimusTokens.TextPrimary
                }
            )

            // 3. CURRENT DECISION CARD (If Pending)
            if (state.hasDraft || state.status.contains("approval", ignoreCase = true)) {
                Card(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(OptimusTokens.CornerCard)
                        .border(1.5.dp, OptimusTokens.Accent, OptimusTokens.CornerCard),
                    shape = OptimusTokens.CornerCard,
                    colors = CardDefaults.cardColors(containerColor = OptimusTokens.SurfaceRaised)
                ) {
                    Column(
                        modifier = Modifier.padding(16.dp),
                        verticalArrangement = Arrangement.spacedBy(10.dp)
                    ) {
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(
                                text = "SEND REVIEW",
                                fontSize = 11.sp,
                                fontWeight = FontWeight.Bold,
                                color = OptimusTokens.Accent
                            )
                            Text(
                                text = "Say send · redictate · cancel",
                                fontSize = 11.sp,
                                color = OptimusTokens.TextSecondary
                            )
                        }

                        Text(
                            text = "Send this prompt to $destTitle?",
                            fontSize = 14.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = OptimusTokens.TextPrimary
                        )

                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(8.dp)
                        ) {
                            OutlinedButton(
                                onClick = onCancel,
                                modifier = Modifier
                                    .weight(1f)
                                    .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                                shape = OptimusTokens.CornerControl
                            ) {
                                Text("Cancel", fontSize = 12.sp)
                            }

                            Button(
                                onClick = onConfirm,
                                enabled = state.canConfirm,
                                modifier = Modifier
                                    .weight(1.5f)
                                    .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                                shape = OptimusTokens.CornerControl,
                                colors = ButtonDefaults.buttonColors(
                                    containerColor = OptimusTokens.Accent,
                                    contentColor = OptimusTokens.Background
                                )
                            ) {
                                Text(
                                    text = if (state.sending) "Sending..." else "Confirm & Send",
                                    fontSize = 13.sp,
                                    fontWeight = FontWeight.SemiBold
                                )
                            }
                        }

                        if (!state.canConfirm && !state.sending) {
                            val chosen = state.selectedDestination
                            val reason = when {
                                chosen == null && state.selectedDestinationId == null -> "Pick a destination above."
                                chosen != null && !chosen.ready -> "${chosen.name} not ready: ${chosen.detail}"
                                !state.connected -> "PC disconnected."
                                else -> ""
                            }
                            if (reason.isNotBlank()) {
                                Text(reason, fontSize = 11.sp, color = OptimusTokens.Error)
                            }
                        }
                    }
                }
            }

            // 4. TRANSCRIPT / RESPONSE AREA
            if (state.sendSummary.isNotBlank()) {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = OptimusTokens.CornerCard,
                    colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
                ) {
                    Text(
                        text = state.sendSummary,
                        modifier = Modifier.padding(14.dp),
                        fontSize = 13.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = if (state.sendSummary.startsWith("Sent")) OptimusTokens.Success else OptimusTokens.Error
                    )
                }
            }

            if (state.timings.isNotBlank()) {
                Surface(
                    shape = OptimusTokens.CornerControl,
                    color = OptimusTokens.SurfaceRaised,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        text = state.timings,
                        fontSize = 11.sp,
                        fontFamily = FontFamily.Monospace,
                        color = OptimusTokens.Accent,
                        modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp)
                    )
                }
            }

            if (state.rawTranscript.isNotBlank()) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("RAW TRANSCRIPT", fontSize = 10.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextTertiary)
                    TextButton(onClick = { showRawTranscript = !showRawTranscript }) {
                        Text(if (showRawTranscript) "Hide" else "Show", fontSize = 11.sp, color = OptimusTokens.Accent)
                    }
                }

                if (showRawTranscript) {
                    Surface(
                        shape = OptimusTokens.CornerControl,
                        color = OptimusTokens.Surface,
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(
                            text = state.rawTranscript,
                            fontSize = 12.sp,
                            color = OptimusTokens.TextSecondary,
                            modifier = Modifier.padding(12.dp)
                        )
                    }
                }
            }

            // 5. COMPOSER TEXT FIELD (Draft Editor / Direct Prompting)
            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                Text(
                    text = "PROMPT COMPOSER",
                    fontSize = 10.sp,
                    fontWeight = FontWeight.Bold,
                    color = OptimusTokens.TextSecondary
                )

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    OutlinedTextField(
                        value = state.cleanedDraft,
                        onValueChange = onDraftChange,
                        placeholder = { Text("Speak or type prompt...", color = OptimusTokens.TextTertiary) },
                        modifier = Modifier
                            .weight(1f)
                            .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                        shape = OptimusTokens.CornerControl,
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedContainerColor = OptimusTokens.Surface,
                            unfocusedContainerColor = OptimusTokens.Surface,
                            focusedBorderColor = OptimusTokens.BorderFocused,
                            unfocusedBorderColor = OptimusTokens.Border,
                            focusedTextColor = OptimusTokens.TextPrimary,
                            unfocusedTextColor = OptimusTokens.TextPrimary
                        ),
                        maxLines = 4
                    )

                    Button(
                        onClick = onConfirm,
                        enabled = state.canConfirm,
                        modifier = Modifier
                            .size(OptimusTokens.MinTouchTarget)
                            .clip(OptimusTokens.CornerControl),
                        shape = OptimusTokens.CornerControl,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = OptimusTokens.Accent,
                            contentColor = OptimusTokens.Background
                        )
                    ) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.Send,
                            contentDescription = "Confirm and send",
                            modifier = Modifier.size(18.dp)
                        )
                    }
                }
            }

            if (state.error.isNotBlank()) {
                Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = OptimusTokens.CornerCard,
                    colors = CardDefaults.cardColors(containerColor = OptimusTokens.SurfaceRaised)
                ) {
                    Text(
                        text = state.error,
                        modifier = Modifier.padding(12.dp),
                        color = OptimusTokens.Error,
                        fontSize = 12.sp
                    )
                }
            }

            // Safe bottom spacing for bottom navigation
            Spacer(modifier = Modifier.height(72.dp))
        }

        // BOTTOM SHEET: Destination / Thread Picker
        if (showDestinationSheet) {
            ModalBottomSheet(
                onDismissRequest = { showDestinationSheet = false },
                sheetState = sheetState,
                containerColor = OptimusTokens.SurfaceRaised,
                shape = OptimusTokens.CornerBottomSheet
            ) {
                DestinationPickerBottomSheet(
                    destinations = state.destinations,
                    selectedId = state.selectedDestinationId,
                    onSelect = { id ->
                        onSelectDestination(id)
                        coroutineScope.launch {
                            sheetState.hide()
                            showDestinationSheet = false
                        }
                    },
                    onRefresh = onRefreshDestinations
                )
            }
        }

        // CONNECTION & CONTROLS DIALOG / SHEET
        if (showConnectionDialog) {
            ConnectionSettingsDialog(
                state = state,
                onHostChange = onHostChange,
                onPortChange = onPortChange,
                onConnect = onConnect,
                onDisconnect = onDisconnect,
                onNarrationModeChange = onNarrationModeChange,
                onNarrateToolsChange = onNarrateToolsChange,
                onDismiss = { showConnectionDialog = false }
            )
        }
    }
}

/** Destination Picker Modal Bottom Sheet. */
@Composable
private fun DestinationPickerBottomSheet(
    destinations: List<PcDestination>,
    selectedId: String?,
    onSelect: (String) -> Unit,
    onRefresh: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column {
                Text(
                    text = "TARGET DESTINATIONS",
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Bold,
                    color = OptimusTokens.TextPrimary
                )
                Text(
                    text = "Explicit voice binding target",
                    fontSize = 11.sp,
                    color = OptimusTokens.TextSecondary
                )
            }

            IconButton(
                onClick = onRefresh,
                modifier = Modifier.size(OptimusTokens.MinTouchTarget)
            ) {
                Icon(Icons.Default.Refresh, contentDescription = "Refresh destinations", tint = OptimusTokens.Accent)
            }
        }

        // Standard destinations if PC hasn't reported list yet
        val effectiveDestinations = if (destinations.isEmpty()) {
            listOf(
                PcDestination("claude", "Claude Desktop", true, "Default Windows desktop adapter"),
                PcDestination("antigravity", "Antigravity IDE", true, "Advanced agent coding session"),
                PcDestination("codex", "Codex / ChatGPT", true, "Dedicated workspace window")
            )
        } else destinations

        effectiveDestinations.forEach { dest ->
            val isSelected = dest.id == selectedId
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(OptimusTokens.CornerCard)
                    .border(
                        width = if (isSelected) 1.5.dp else 1.dp,
                        color = if (isSelected) OptimusTokens.Accent else OptimusTokens.Border,
                        shape = OptimusTokens.CornerCard
                    )
                    .clickable { onSelect(dest.id) },
                shape = OptimusTokens.CornerCard,
                color = if (isSelected) OptimusTokens.SurfaceRaised else OptimusTokens.Surface
            ) {
                Row(
                    modifier = Modifier.padding(14.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    RadioButton(
                        selected = isSelected,
                        onClick = { onSelect(dest.id) }
                    )
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = dest.name,
                            fontSize = 15.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = OptimusTokens.TextPrimary
                        )
                        Text(
                            text = dest.detail,
                            fontSize = 11.sp,
                            color = if (dest.ready) OptimusTokens.TextSecondary else OptimusTokens.Error
                        )
                    }
                }
            }
        }

        Spacer(modifier = Modifier.height(28.dp))
    }
}

/** Connection and Narration Settings Dialog. */
@Composable
private fun ConnectionSettingsDialog(
    state: TalkUiState,
    onHostChange: (String) -> Unit,
    onPortChange: (String) -> Unit,
    onConnect: () -> Unit,
    onDisconnect: () -> Unit,
    onNarrationModeChange: (String) -> Unit,
    onNarrateToolsChange: (Boolean) -> Unit,
    onDismiss: () -> Unit
) {
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss) {
        Surface(
            modifier = Modifier
                .fillMaxWidth()
                .clip(OptimusTokens.CornerCard)
                .border(1.dp, OptimusTokens.Border, OptimusTokens.CornerCard),
            color = OptimusTokens.SurfaceRaised,
            shape = OptimusTokens.CornerCard
        ) {
            Column(
                modifier = Modifier
                    .padding(20.dp)
                    .verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(14.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = "CONNECTION & NARRATION",
                        fontSize = 14.sp,
                        fontWeight = FontWeight.Bold,
                        color = OptimusTokens.TextPrimary
                    )
                    IconButton(onClick = onDismiss, modifier = Modifier.size(OptimusTokens.MinTouchTarget)) {
                        Icon(Icons.Default.Close, contentDescription = "Close", tint = OptimusTokens.TextPrimary)
                    }
                }

                // Address & Port
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
                        onClick = {
                            onConnect()
                            onDismiss()
                        },
                        enabled = !state.connected && state.host.isNotBlank(),
                        modifier = Modifier
                            .weight(1f)
                            .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                        shape = OptimusTokens.CornerControl,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = OptimusTokens.Accent,
                            contentColor = OptimusTokens.Background
                        )
                    ) { Text("Connect") }

                    Button(
                        onClick = {
                            onDisconnect()
                            onDismiss()
                        },
                        enabled = state.connected,
                        modifier = Modifier
                            .weight(1f)
                            .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                        shape = OptimusTokens.CornerControl
                    ) { Text("Disconnect") }
                }

                // Narration settings
                Text(
                    text = "AGENT NARRATION MODE",
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Bold,
                    color = OptimusTokens.TextSecondary
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(
                        selected = state.narrationMode == "concise",
                        onClick = { onNarrationModeChange("concise") }
                    )
                    Text("Concise", fontSize = 13.sp, color = OptimusTokens.TextPrimary)
                    Spacer(modifier = Modifier.width(16.dp))
                    RadioButton(
                        selected = state.narrationMode == "comprehensive",
                        onClick = { onNarrationModeChange("comprehensive") }
                    )
                    Text("Comprehensive", fontSize = 13.sp, color = OptimusTokens.TextPrimary)
                }

                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Text("Narrate tools & skills", fontSize = 13.sp, color = OptimusTokens.TextPrimary)
                    Switch(
                        checked = state.narrateToolsAndSkills,
                        onCheckedChange = onNarrateToolsChange
                    )
                }
            }
        }
    }
}
