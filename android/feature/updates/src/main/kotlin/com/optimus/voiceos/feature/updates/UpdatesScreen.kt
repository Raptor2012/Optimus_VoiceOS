package com.optimus.voiceos.feature.updates

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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.optimus.voiceos.ui.theme.OptimusTokens
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun UpdatesScreen(
    state: UpdatesUiState,
    onResolveDecision: (String, String) -> Unit,
    onOpenDecisionSheet: (PendingDecision) -> Unit,
    onCloseDecisionSheet: () -> Unit,
    onViewSourceEvent: (String, String, String, String, String, String) -> Unit,
    onCloseSourceEvent: () -> Unit,
    onTalkHere: (String) -> Unit,
    modifier: Modifier = Modifier
) {
    val sheetState = rememberModalBottomSheetState()
    val coroutineScope = rememberCoroutineScope()

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(OptimusTokens.Background)
    ) {
        LazyColumn(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp)
        ) {
            item {
                Spacer(modifier = Modifier.height(12.dp))
                Column {
                    Text(
                        text = "UPDATES",
                        fontSize = 20.sp,
                        fontWeight = FontWeight.Bold,
                        letterSpacing = 1.sp,
                        color = OptimusTokens.TextPrimary
                    )
                    Text(
                        text = "Digests, pending decisions & agent work feed",
                        fontSize = 12.sp,
                        color = OptimusTokens.TextSecondary
                    )
                }
                Spacer(modifier = Modifier.height(4.dp))
            }

            // SECTION 1: Unanswered Decisions (Highlighted)
            val unanswered = state.pendingDecisions.filter { !it.isAnswered }
            if (unanswered.isNotEmpty()) {
                item {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(6.dp)
                    ) {
                        Box(
                            modifier = Modifier
                                .size(8.dp)
                                .clip(CircleShape)
                                .background(OptimusTokens.Warning)
                        )
                        Text(
                            text = "UNANSWERED DECISIONS (${unanswered.size})",
                            fontSize = 11.sp,
                            fontWeight = FontWeight.Bold,
                            color = OptimusTokens.Warning
                        )
                    }
                }

                items(unanswered, key = { it.id }) { decision ->
                    DecisionCard(
                        decision = decision,
                        onResolve = { res -> onResolveDecision(decision.id, res) },
                        onTalkHere = { onTalkHere(decision.destinationId) },
                        onTraceEvent = {
                            onViewSourceEvent(
                                decision.sourceEventId,
                                decision.title,
                                decision.description,
                                decision.agentName,
                                decision.projectName,
                                decision.destinationId
                            )
                        },
                        onOpenSheet = { onOpenDecisionSheet(decision) }
                    )
                }
            }

            // SECTION 2: Compiled Progress Digests
            item {
                Text(
                    text = "COMPILED PROGRESS DIGESTS",
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Bold,
                    color = OptimusTokens.TextSecondary,
                    modifier = Modifier.padding(top = 4.dp)
                )
            }

            items(state.progressDigests, key = { it.id }) { digest ->
                DigestCard(
                    digest = digest,
                    onTraceEvent = {
                        onViewSourceEvent(
                            digest.sourceEventId,
                            digest.title,
                            digest.summary,
                            "Optimus Synthesis",
                            "Multi-Project",
                            "claude"
                        )
                    }
                )
            }

            // SECTION 3: Completed Work Summaries
            item {
                Text(
                    text = "COMPLETED WORK",
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Bold,
                    color = OptimusTokens.TextSecondary,
                    modifier = Modifier.padding(top = 4.dp)
                )
            }

            items(state.completedWork, key = { it.id }) { completed ->
                CompletedWorkCard(
                    work = completed,
                    onTalkHere = { onTalkHere(completed.destinationId) },
                    onTraceEvent = {
                        onViewSourceEvent(
                            completed.sourceEventId,
                            completed.title,
                            completed.summaryText,
                            completed.agentName,
                            completed.projectName,
                            completed.destinationId
                        )
                    }
                )
            }

            item {
                Spacer(modifier = Modifier.height(84.dp))
            }
        }

        // Source Event Dialog
        state.selectedEvent?.let { event ->
            SourceEventDialog(
                event = event,
                onDismiss = onCloseSourceEvent,
                onTalkHere = {
                    onTalkHere(event.destinationId)
                    onCloseSourceEvent()
                }
            )
        }

        // Short Decision Bottom Sheet
        state.activeDecisionForSheet?.let { decision ->
            ModalBottomSheet(
                onDismissRequest = onCloseDecisionSheet,
                sheetState = sheetState,
                containerColor = OptimusTokens.SurfaceRaised,
                shape = OptimusTokens.CornerBottomSheet
            ) {
                DecisionBottomSheet(
                    decision = decision,
                    onResolve = { res ->
                        onResolveDecision(decision.id, res)
                        coroutineScope.launch {
                            sheetState.hide()
                            onCloseDecisionSheet()
                        }
                    },
                    onTalkHere = {
                        onTalkHere(decision.destinationId)
                        coroutineScope.launch {
                            sheetState.hide()
                            onCloseDecisionSheet()
                        }
                    }
                )
            }
        }
    }
}

/** Highlighted Pending Decision Card with action buttons. */
@Composable
private fun DecisionCard(
    decision: PendingDecision,
    onResolve: (String) -> Unit,
    onTalkHere: () -> Unit,
    onTraceEvent: () -> Unit,
    onOpenSheet: () -> Unit
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clip(OptimusTokens.CornerCard)
            .border(1.5.dp, OptimusTokens.Warning, OptimusTokens.CornerCard)
            .clickable(onClick = onOpenSheet),
        shape = OptimusTokens.CornerCard,
        colors = CardDefaults.cardColors(containerColor = OptimusTokens.SurfaceRaised)
    ) {
        Column(
            modifier = Modifier.padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            // Header: Status Badge & Project
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Surface(
                    shape = RoundedCornerShape(6.dp),
                    color = OptimusTokens.Warning.copy(alpha = 0.2f)
                ) {
                    Text(
                        text = "DECISION REQUIRED",
                        fontSize = 10.sp,
                        fontWeight = FontWeight.Bold,
                        color = OptimusTokens.Warning,
                        modifier = Modifier.padding(horizontal = 8.dp, vertical = 3.dp)
                    )
                }

                Text(
                    text = decision.timestamp,
                    fontSize = 11.sp,
                    color = OptimusTokens.TextTertiary
                )
            }

            // Title & Description
            Text(
                text = decision.title,
                fontSize = 15.sp,
                fontWeight = FontWeight.Bold,
                color = OptimusTokens.TextPrimary
            )

            Text(
                text = decision.description,
                fontSize = 13.sp,
                color = OptimusTokens.TextPrimary,
                lineHeight = 18.sp
            )

            // Traceability metadata row
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "${decision.agentName} • ${decision.projectName}",
                    fontSize = 11.sp,
                    color = OptimusTokens.TextSecondary
                )
                TextButton(
                    onClick = onTraceEvent,
                    modifier = Modifier.defaultMinSize(minHeight = 36.dp)
                ) {
                    Text("Source #${decision.sourceEventId}", fontSize = 11.sp, color = OptimusTokens.Accent)
                }
            }

            // Quick Resolution Options
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                decision.options.forEach { option ->
                    Button(
                        onClick = { onResolve(option) },
                        modifier = Modifier
                            .weight(1f)
                            .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                        shape = OptimusTokens.CornerControl,
                        colors = if (option.contains("Confirm", ignoreCase = true) || option.contains("Include", ignoreCase = true)) {
                            ButtonDefaults.buttonColors(containerColor = OptimusTokens.Accent, contentColor = OptimusTokens.Background)
                        } else {
                            ButtonDefaults.buttonColors(containerColor = OptimusTokens.Surface, contentColor = OptimusTokens.TextPrimary)
                        }
                    ) {
                        Text(
                            text = option,
                            fontSize = 11.sp,
                            fontWeight = FontWeight.SemiBold,
                            maxLines = 1
                        )
                    }
                }
            }
        }
    }
}

/** Compiled Progress Digest Card. */
@Composable
private fun DigestCard(
    digest: ProgressDigest,
    onTraceEvent: () -> Unit
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clip(OptimusTokens.CornerCard)
            .border(1.dp, OptimusTokens.Border, OptimusTokens.CornerCard),
        shape = OptimusTokens.CornerCard,
        colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
    ) {
        Column(
            modifier = Modifier.padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = digest.title,
                    fontSize = 15.sp,
                    fontWeight = FontWeight.Bold,
                    color = OptimusTokens.TextPrimary
                )
                Text(
                    text = digest.period,
                    fontSize = 11.sp,
                    color = OptimusTokens.Accent
                )
            }

            Text(
                text = digest.summary,
                fontSize = 13.sp,
                color = OptimusTokens.TextPrimary,
                lineHeight = 18.sp
            )

            // Accomplishments list
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(OptimusTokens.SurfaceRaised, OptimusTokens.CornerControl)
                    .padding(10.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp)
            ) {
                digest.keyAccomplishments.forEach { item ->
                    Text(
                        text = "• $item",
                        fontSize = 12.sp,
                        color = OptimusTokens.TextSecondary,
                        lineHeight = 16.sp
                    )
                }
            }

            // Source trace link
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "Generated at ${digest.timestamp}",
                    fontSize = 11.sp,
                    color = OptimusTokens.TextTertiary
                )
                TextButton(onClick = onTraceEvent) {
                    Text("Trace #${digest.sourceEventId}", fontSize = 11.sp, color = OptimusTokens.Accent)
                }
            }
        }
    }
}

/** Completed Work Summary Card. */
@Composable
private fun CompletedWorkCard(
    work: CompletedWorkSummary,
    onTalkHere: () -> Unit,
    onTraceEvent: () -> Unit
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clip(OptimusTokens.CornerCard)
            .border(1.dp, OptimusTokens.Border, OptimusTokens.CornerCard),
        shape = OptimusTokens.CornerCard,
        colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
    ) {
        Column(
            modifier = Modifier.padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = work.title,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = OptimusTokens.TextPrimary
                )
                Surface(
                    shape = RoundedCornerShape(6.dp),
                    color = OptimusTokens.Success.copy(alpha = 0.2f)
                ) {
                    Text(
                        text = "DONE",
                        fontSize = 10.sp,
                        fontWeight = FontWeight.Bold,
                        color = OptimusTokens.Success,
                        modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp)
                    )
                }
            }

            Text(
                text = work.summaryText,
                fontSize = 12.sp,
                color = OptimusTokens.TextSecondary,
                lineHeight = 17.sp
            )

            Text(
                text = work.changedFilesSummary,
                fontSize = 11.sp,
                fontFamily = FontFamily.Monospace,
                color = OptimusTokens.Accent
            )

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "${work.agentName} • ${work.projectName}",
                    fontSize = 11.sp,
                    color = OptimusTokens.TextTertiary
                )
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    TextButton(onClick = onTraceEvent) {
                        Text("Source #${work.sourceEventId}", fontSize = 11.sp, color = OptimusTokens.Accent)
                    }
                    Button(
                        onClick = onTalkHere,
                        shape = OptimusTokens.CornerControl,
                        modifier = Modifier.defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = OptimusTokens.SurfaceRaised,
                            contentColor = OptimusTokens.Accent
                        )
                    ) {
                        Text("Talk here", fontSize = 11.sp)
                    }
                }
            }
        }
    }
}

/** Source Event Details Modal. */
@Composable
private fun SourceEventDialog(
    event: SourceEventDetail,
    onDismiss: () -> Unit,
    onTalkHere: () -> Unit
) {
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Surface(
            modifier = Modifier
                .fillMaxSize()
                .background(OptimusTokens.Background)
                .padding(20.dp),
            color = OptimusTokens.Background
        ) {
            Column(
                modifier = Modifier.fillMaxSize(),
                verticalArrangement = Arrangement.spacedBy(14.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column {
                        Text("EVENT TRACE", fontSize = 11.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextSecondary)
                        Text(event.eventId, fontSize = 18.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.Accent)
                    }
                    IconButton(
                        onClick = onDismiss,
                        modifier = Modifier.size(OptimusTokens.MinTouchTarget)
                    ) {
                        Icon(Icons.Default.Close, contentDescription = "Close", tint = OptimusTokens.TextPrimary)
                    }
                }

                Card(
                    modifier = Modifier.fillMaxWidth(),
                    shape = OptimusTokens.CornerCard,
                    colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
                ) {
                    Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(event.title, fontSize = 16.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextPrimary)
                        Text(event.description, fontSize = 13.sp, color = OptimusTokens.TextSecondary)
                        Text("Agent: ${event.agent}", fontSize = 12.sp, color = OptimusTokens.TextPrimary)
                        Text("Project: ${event.project}", fontSize = 12.sp, color = OptimusTokens.TextPrimary)
                        Text("Recorded: ${event.timestamp}", fontSize = 11.sp, color = OptimusTokens.TextTertiary)
                    }
                }

                Spacer(modifier = Modifier.weight(1f))

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    OutlinedButton(
                        onClick = onDismiss,
                        modifier = Modifier
                            .weight(1f)
                            .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                        shape = OptimusTokens.CornerControl
                    ) {
                        Text("Dismiss")
                    }

                    Button(
                        onClick = onTalkHere,
                        modifier = Modifier
                            .weight(1.5f)
                            .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                        shape = OptimusTokens.CornerControl,
                        colors = ButtonDefaults.buttonColors(
                            containerColor = OptimusTokens.Accent,
                            contentColor = OptimusTokens.Background
                        )
                    ) {
                        Icon(Icons.Default.Lock, contentDescription = null, modifier = Modifier.size(14.dp))
                        Spacer(modifier = Modifier.width(6.dp))
                        Text("Talk on this event", fontWeight = FontWeight.SemiBold)
                    }
                }
            }
        }
    }
}

/** Short Decision Bottom Sheet for decision review and quick answers. */
@Composable
private fun DecisionBottomSheet(
    decision: PendingDecision,
    onResolve: (String) -> Unit,
    onTalkHere: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("RESOLVE DECISION", fontSize = 12.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.Warning)
            TextButton(onClick = onTalkHere) {
                Text("Discuss by Voice", fontSize = 12.sp, color = OptimusTokens.Accent)
            }
        }

        Text(
            text = decision.title,
            fontSize = 16.sp,
            fontWeight = FontWeight.Bold,
            color = OptimusTokens.TextPrimary
        )

        Text(
            text = decision.description,
            fontSize = 13.sp,
            color = OptimusTokens.TextSecondary,
            lineHeight = 18.sp
        )

        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            decision.options.forEach { option ->
                Button(
                    onClick = { onResolve(option) },
                    modifier = Modifier
                        .fillMaxWidth()
                        .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                    shape = OptimusTokens.CornerControl,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = OptimusTokens.Accent,
                        contentColor = OptimusTokens.Background
                    )
                ) {
                    Text(option, fontWeight = FontWeight.SemiBold)
                }
            }
        }

        Spacer(modifier = Modifier.height(24.dp))
    }
}
