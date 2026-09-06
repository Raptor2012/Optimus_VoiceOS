package com.optimus.voiceos.feature.projects

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.ViewModel

data class AgentWorker(
    val id: String,
    val name: String,
    val role: String,
    val model: String
)

data class FileChangeSummary(
    val path: String,
    val additions: Int,
    val deletions: Int
)

data class ProjectTask(
    val id: String,
    val title: String,
    val status: String, // "In Progress", "Queued", "Completed"
    val progressPercent: Int,
    val agent: AgentWorker,
    val conversationSnippet: String,
    val changedFiles: List<FileChangeSummary> = emptyList(),
    val planOrResultContent: String = ""
)

data class ProjectConversation(
    val id: String,
    val title: String,
    val lastMessage: String,
    val timestamp: String,
    val messageCount: Int
)

data class ProjectItem(
    val id: String,
    val name: String,
    val objective: String,
    val progressSentence: String,
    val completedTasks: Int,
    val totalTasks: Int,
    val activeWorkers: List<AgentWorker>,
    val nextAction: String,
    val destinationId: String,
    val tasks: List<ProjectTask>,
    val conversations: List<ProjectConversation>,
    val recentResultSummary: String
)

data class ReaderState(
    val isOpen: Boolean = false,
    val title: String = "",
    val content: String = ""
)

data class ProjectsUiState(
    val projects: List<ProjectItem> = emptyList(),
    val selectedProject: ProjectItem? = null,
    val selectedTask: ProjectTask? = null,
    val reader: ReaderState = ReaderState(),
    val showThreadSheet: Boolean = false
)

class ProjectsViewModel : ViewModel() {

    var uiState by mutableStateOf(ProjectsUiState(projects = createInitialProjects()))
        private set

    fun selectProject(project: ProjectItem?) {
        uiState = uiState.copy(selectedProject = project, selectedTask = null)
    }

    fun selectTask(task: ProjectTask?) {
        uiState = uiState.copy(selectedTask = task)
    }

    fun openReader(title: String, content: String) {
        uiState = uiState.copy(reader = ReaderState(isOpen = true, title = title, content = content))
    }

    fun closeReader() {
        uiState = uiState.copy(reader = ReaderState(isOpen = false))
    }

    fun setThreadSheetVisible(visible: Boolean) {
        uiState = uiState.copy(showThreadSheet = visible)
    }

    private fun createInitialProjects(): List<ProjectItem> {
        val geminiWorker = AgentWorker("gemini", "Gemini 3.8 Flash", "Routine Dev & UI", "gemini-3.8-flash")
        val claudeWorker = AgentWorker("claude", "Claude Opus 5", "Architecture & Debugging", "claude-3-opus")
        val codexWorker = AgentWorker("codex", "Codex (ChatGPT)", "Personal Slice Specialist", "codex-desktop")

        return listOf(
            ProjectItem(
                id = "proj-1",
                name = "Optimus Voice OS",
                objective = "Ship personal-use voice-first horizontal layer between Windows 11 & Pixel 9a.",
                progressSentence = "Completed 3-destination navigation restructure and VoiceOrb canvas rendering.",
                completedTasks = 8,
                totalTasks = 10,
                activeWorkers = listOf(geminiWorker, claudeWorker),
                nextAction = "Verify foreground service with notification controls and dogfood live speech.",
                destinationId = "claude",
                tasks = listOf(
                    ProjectTask(
                        id = "task-101",
                        title = "Android 3-Destination Navigation Restructure",
                        status = "In Progress",
                        progressPercent = 85,
                        agent = geminiWorker,
                        conversationSnippet = "Gemini: Implemented Material 3 NavigationBar, CompactConversationCapsule, and Canvas-drawn VoiceOrb.",
                        changedFiles = listOf(
                            FileChangeSummary("MainActivity.kt", 68, 22),
                            FileChangeSummary("VoiceOrb.kt", 185, 0),
                            FileChangeSummary("ProjectsScreen.kt", 240, 0),
                            FileChangeSummary("UpdatesScreen.kt", 210, 0)
                        ),
                        planOrResultContent = """
                            # Vertical Slice: Android Navigation & Voice Architecture
                            
                            ## Implemented Architecture
                            - Material 3 NavigationBar with Talk, Projects, and Updates destinations.
                            - CompactConversationCapsule floating above bottom tabs during multi-screen browsing.
                            - Canvas-rendered VoiceOrb replicating WPF 11-state machine with sub-millisecond redraw.
                            - Android Foreground Service with microphone type and notification Mute/End controls.
                            
                            ## Verification Plan
                            1. Switch tabs while audio stream is active; confirm unbroken TCP PCM streaming.
                            2. Tap 'Talk here' on Project card; verify instant voice lock with no prompt regression.
                            3. Lock device; confirm notification playback controls remain responsive.
                        """.trimIndent()
                    ),
                    ProjectTask(
                        id = "task-102",
                        title = "WASAPI 16 kHz Mono In-Memory Buffer Verification",
                        status = "Completed",
                        progressPercent = 100,
                        agent = claudeWorker,
                        conversationSnippet = "Claude: WAVEFORMATEXTENSIBLE layout verified; buffer packet releases cleanly on RTX 4070.",
                        changedFiles = listOf(
                            FileChangeSummary("WasapiCapture.cs", 42, 18),
                            FileChangeSummary("VoicePipelineTests.cs", 64, 4)
                        ),
                        planOrResultContent = """
                            # WASAPI Audio Buffer Release Report
                            
                            Target: Windows 11 PC (Intel i9-14900HX, RTX 4070 Laptop GPU)
                            - Formats: 16 kHz, 16-bit Mono PCM
                            - Measured packet acquisition latency: < 1.4 ms
                            - Zero disk persistence: buffer processed strictly in-memory.
                        """.trimIndent()
                    )
                ),
                conversations = listOf(
                    ProjectConversation("conv-1", "Vertical Slice Implementation Sync", "Latest tokens and bottom sheet navigation mapped.", "10:18 AM", 14),
                    ProjectConversation("conv-2", "Audio Streaming Latency Diagnostic", "Piper warm TTS synthesis median: 248 ms.", "Yesterday", 28)
                ),
                recentResultSummary = "Compiled Android APK with new design tokens (#101114 / #181A1F / #8C9EFF). All tests passing."
            ),
            ProjectItem(
                id = "proj-2",
                name = "Amyloidose XR",
                objective = "Maintain VR flow graph invariants and visitor pacing for medical congress booth.",
                progressSentence = "Strictly adhered to Flow Graph orchestration; verified German pacing settles at 0.35s.",
                completedTasks = 12,
                totalTasks = 12,
                activeWorkers = listOf(codexWorker),
                nextAction = "Batchmode scene validation check before release freeze.",
                destinationId = "antigravity",
                tasks = listOf(
                    ProjectTask(
                        id = "task-201",
                        title = "Flow Graph German Audio Alignment",
                        status = "Completed",
                        progressPercent = 100,
                        agent = codexWorker,
                        conversationSnippet = "Codex: Flow graph Guide_02 timing matched to 7.053s with 0.3s visual settle.",
                        changedFiles = listOf(
                            FileChangeSummary("AmyloidoseExperience.asset", 88, 12)
                        ),
                        planOrResultContent = """
                            # Flow Graph Timing Validation
                            - Flow graph: Assets/Flow/AmyloidoseExperience.asset
                            - Guide_02 audio length: 7.053s
                            - Visual settlement delay: 0.35s
                            - Zero procedural runtime generation verified.
                        """.trimIndent()
                    )
                ),
                conversations = listOf(
                    ProjectConversation("conv-3", "PNH Parity Review", "Viewer-relative placement matched within 0.02m.", "Sep 4", 19)
                ),
                recentResultSummary = "Flow Graph yaml boundaries and PPtr references checked clean. Batchmode compilation clean."
            ),
            ProjectItem(
                id = "proj-3",
                name = "OmniAgent Inference Engine",
                objective = "Keep Parakeet 0.6B STT and Gemma 4 E2B cleanup resident in RTX 4070 VRAM.",
                progressSentence = "Warm TTS model stays resident; time-to-first-audio remains under 250 ms target.",
                completedTasks = 5,
                totalTasks = 6,
                activeWorkers = listOf(claudeWorker, geminiWorker),
                nextAction = "Tune spoken window-binding alias disambiguation.",
                destinationId = "codex",
                tasks = listOf(
                    ProjectTask(
                        id = "task-301",
                        title = "Gemma 4 E2B Few-Shot Prompt Cleanup Tuning",
                        status = "In Progress",
                        progressPercent = 70,
                        agent = geminiWorker,
                        conversationSnippet = "Gemini: Prompt preserves negations ('do not reply anything') while trimming fillers.",
                        changedFiles = listOf(
                            FileChangeSummary("GemmaPromptCleaner.cs", 34, 15)
                        ),
                        planOrResultContent = """
                            # Gemma 4 E2B Strict Cleanup Evaluation
                            - Prompt: Removes verbal stutters ('uh', 'um') and self-corrections.
                            - Invariant: Retains semantic commands and constraints ('send this message as-is').
                            - Mean tokens generated: 14.8 tokens.
                        """.trimIndent()
                    )
                ),
                conversations = listOf(
                    ProjectConversation("conv-4", "Model Selection Rationale", "Gemma 4 selected over Qwen3.5 on measured token behavior.", "Sep 2", 9)
                ),
                recentResultSummary = "Gemma 4 E2B returns clean rewrite in ~15 tokens vs Qwen runaway reasoning."
            )
        )
    }
}
