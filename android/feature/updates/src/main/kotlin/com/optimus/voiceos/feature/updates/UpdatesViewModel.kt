package com.optimus.voiceos.feature.updates

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel

data class PendingDecision(
    val id: String,
    val title: String,
    val description: String,
    val options: List<String>,
    val agentName: String,
    val projectName: String,
    val destinationId: String,
    val sourceEventId: String,
    val timestamp: String,
    val isAnswered: Boolean = false,
    val selectedResolution: String? = null
)

data class ProgressDigest(
    val id: String,
    val title: String,
    val period: String,
    val summary: String,
    val keyAccomplishments: List<String>,
    val timestamp: String,
    val sourceEventId: String
)

data class CompletedWorkSummary(
    val id: String,
    val title: String,
    val projectName: String,
    val agentName: String,
    val summaryText: String,
    val changedFilesSummary: String,
    val timestamp: String,
    val sourceEventId: String,
    val destinationId: String
)

data class SourceEventDetail(
    val eventId: String,
    val title: String,
    val description: String,
    val timestamp: String,
    val agent: String,
    val project: String,
    val destinationId: String
)

data class UpdatesUiState(
    val pendingDecisions: List<PendingDecision> = emptyList(),
    val progressDigests: List<ProgressDigest> = emptyList(),
    val completedWork: List<CompletedWorkSummary> = emptyList(),
    val selectedEvent: SourceEventDetail? = null,
    val activeDecisionForSheet: PendingDecision? = null
)

class UpdatesViewModel : ViewModel() {

    var uiState by mutableStateOf(UpdatesUiState(
        pendingDecisions = createInitialDecisions(),
        progressDigests = createInitialDigests(),
        completedWork = createInitialCompletedWork()
    ))
        private set

    fun resolveDecision(decisionId: String, resolution: String) {
        val updated = uiState.pendingDecisions.map {
            if (it.id == decisionId) it.copy(isAnswered = true, selectedResolution = resolution) else it
        }
        uiState = uiState.copy(
            pendingDecisions = updated,
            activeDecisionForSheet = null
        )
    }

    fun openDecisionSheet(decision: PendingDecision) {
        uiState = uiState.copy(activeDecisionForSheet = decision)
    }

    fun closeDecisionSheet() {
        uiState = uiState.copy(activeDecisionForSheet = null)
    }

    fun viewSourceEvent(eventId: String, title: String, description: String, agent: String, project: String, dest: String) {
        uiState = uiState.copy(
            selectedEvent = SourceEventDetail(
                eventId = eventId,
                title = title,
                description = description,
                timestamp = "Just now",
                agent = agent,
                project = project,
                destinationId = dest
            )
        )
    }

    fun closeSourceEvent() {
        uiState = uiState.copy(selectedEvent = null)
    }

    private fun createInitialDecisions(): List<PendingDecision> {
        return listOf(
            PendingDecision(
                id = "dec-1",
                title = "Send Confirmed Refactor Draft to Antigravity?",
                description = "Antigravity is waiting to execute the multi-file Android Navigation slice. Confirm approval to submit.",
                options = listOf("Confirm & Send", "Redictate", "Decline"),
                agentName = "Gemini 3.8 Flash",
                projectName = "Optimus Voice OS",
                destinationId = "antigravity",
                sourceEventId = "EVT-8192",
                timestamp = "10:20 AM"
            ),
            PendingDecision(
                id = "dec-2",
                title = "Approve Foreground Service Notification Style",
                description = "Choose whether conversation notification keeps ongoing mute action button visible during screen lock.",
                options = listOf("Include Mute Button", "Only End Button"),
                agentName = "Claude Opus 5",
                projectName = "Optimus Voice OS",
                destinationId = "claude",
                sourceEventId = "EVT-8184",
                timestamp = "10:15 AM"
            )
        )
    }

    private fun createInitialDigests(): List<ProgressDigest> {
        return listOf(
            ProgressDigest(
                id = "dig-1",
                title = "Morning Engineering Digest",
                period = "Past 4 Hours",
                summary = "3 vertical slices progressed across Optimus Voice OS and Amyloidose XR. Average audio turnaround remained at 248 ms.",
                keyAccomplishments = listOf(
                    "Restructured Pixel app from 1 screen to 3 bottom destinations (Talk, Projects, Updates).",
                    "Extracted Canvas-rendered VoiceOrb component with 11 WPF visual state replications.",
                    "Configured Android Foreground Service with microphone type and notification controls."
                ),
                timestamp = "10:00 AM",
                sourceEventId = "EVT-8170"
            )
        )
    }

    private fun createInitialCompletedWork(): List<CompletedWorkSummary> {
        return listOf(
            CompletedWorkSummary(
                id = "comp-1",
                title = "WAVEFORMATEXTENSIBLE 16 kHz Interop Fix",
                projectName = "Optimus Voice OS",
                agentName = "Claude Opus 5",
                summaryText = "Fixed WASAPI buffer packet release so in-memory audio is consumed without blocking audio graph.",
                changedFilesSummary = "2 files changed (+42, -18 lines)",
                timestamp = "09:35 AM",
                sourceEventId = "EVT-8152",
                destinationId = "claude"
            ),
            CompletedWorkSummary(
                id = "comp-2",
                title = "German Audio Flow Graph Alignment",
                projectName = "Amyloidose XR",
                agentName = "Codex (ChatGPT)",
                summaryText = "Verified Guide_02 timing at 7.053s with 0.35s visual settle. No procedural generation added.",
                changedFilesSummary = "1 file changed (+88, -12 lines)",
                timestamp = "Yesterday",
                sourceEventId = "EVT-7940",
                destinationId = "antigravity"
            )
        )
    }
}
