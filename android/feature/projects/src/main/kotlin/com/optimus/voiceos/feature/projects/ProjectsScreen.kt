package com.optimus.voiceos.feature.projects

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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
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
import androidx.compose.ui.graphics.Color
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
fun ProjectsScreen(
    state: ProjectsUiState,
    onSelectProject: (ProjectItem?) -> Unit,
    onSelectTask: (ProjectTask?) -> Unit,
    onTalkHere: (String) -> Unit,
    onOpenReader: (String, String) -> Unit,
    onCloseReader: () -> Unit,
    onSetThreadSheetVisible: (Boolean) -> Unit,
    capacityContent: @Composable () -> Unit = {},
    modifier: Modifier = Modifier
) {
    val sheetState = rememberModalBottomSheetState()
    val coroutineScope = rememberCoroutineScope()

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(OptimusTokens.Background)
    ) {
        when {
            state.selectedTask != null -> {
                TaskDetailView(
                    task = state.selectedTask,
                    onBack = { onSelectTask(null) },
                    onTalkHere = { onTalkHere(state.selectedProject?.destinationId ?: "claude") },
                    onOpenReader = onOpenReader
                )
            }
            state.selectedProject != null -> {
                ProjectDetailView(
                    project = state.selectedProject,
                    onBack = { onSelectProject(null) },
                    onSelectTask = onSelectTask,
                    onTalkHere = { onTalkHere(state.selectedProject.destinationId) },
                    onOpenThreadSheet = { onSetThreadSheetVisible(true) },
                    onOpenReader = onOpenReader
                )
            }
            else -> {
                ProjectListView(
                    projects = state.projects,
                    onSelectProject = onSelectProject,
                    onTalkHere = onTalkHere,
                    capacityContent = capacityContent
                )
            }
        }

        // Full-screen Reader Dialog for long plans and results
        if (state.reader.isOpen) {
            FullScreenReaderDialog(
                title = state.reader.title,
                content = state.reader.content,
                onDismiss = onCloseReader
            )
        }

        // Bottom Sheet for Conversations / Thread Selection
        if (state.showThreadSheet && state.selectedProject != null) {
            ModalBottomSheet(
                onDismissRequest = { onSetThreadSheetVisible(false) },
                sheetState = sheetState,
                containerColor = OptimusTokens.SurfaceRaised,
                shape = OptimusTokens.CornerBottomSheet
            ) {
                ConversationThreadBottomSheet(
                    conversations = state.selectedProject.conversations,
                    onSelectThread = { conv ->
                        coroutineScope.launch {
                            sheetState.hide()
                            onSetThreadSheetVisible(false)
                        }
                    },
                    onTalkHere = {
                        onTalkHere(state.selectedProject.destinationId)
                        coroutineScope.launch {
                            sheetState.hide()
                            onSetThreadSheetVisible(false)
                        }
                    }
                )
            }
        }
    }
}

/** List of project cards with one-hand reachability. */
@Composable
private fun ProjectListView(
    projects: List<ProjectItem>,
    onSelectProject: (ProjectItem) -> Unit,
    onTalkHere: (String) -> Unit,
    capacityContent: @Composable () -> Unit
) {
    LazyColumn(
        modifier = Modifier
            .fillMaxSize()
            .padding(horizontal = 16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        item {
            Spacer(modifier = Modifier.height(12.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column {
                    Text(
                        text = "PROJECTS",
                        fontSize = 20.sp,
                        fontWeight = FontWeight.Bold,
                        letterSpacing = 1.sp,
                        color = OptimusTokens.TextPrimary
                    )
                    Text(
                        text = "Active workspaces & agent teams",
                        fontSize = 12.sp,
                        color = OptimusTokens.TextSecondary
                    )
                }
            }
            Spacer(modifier = Modifier.height(4.dp))
        }

        item { capacityContent() }

        items(projects, key = { it.id }) { project ->
            ProjectCard(
                project = project,
                onClick = { onSelectProject(project) },
                onTalkHere = { onTalkHere(project.destinationId) }
            )
        }

        item {
            // Safe bottom padding so bottom capsule doesn't occlude cards
            Spacer(modifier = Modifier.height(84.dp))
        }
    }
}

/** Individual Project Card showing objective, progress, tasks, workers, next action, and Talk Here. */
@Composable
private fun ProjectCard(
    project: ProjectItem,
    onClick: () -> Unit,
    onTalkHere: () -> Unit
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clip(OptimusTokens.CornerCard)
            .border(1.dp, OptimusTokens.Border, OptimusTokens.CornerCard)
            .clickable(onClick = onClick),
        shape = OptimusTokens.CornerCard,
        colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
    ) {
        Column(
            modifier = Modifier.padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            // Header: Name & Destination
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = project.name,
                    fontSize = 17.sp,
                    fontWeight = FontWeight.Bold,
                    color = OptimusTokens.TextPrimary
                )
                Surface(
                    shape = RoundedCornerShape(8.dp),
                    color = OptimusTokens.SurfaceRaised
                ) {
                    Text(
                        text = "→ ${project.destinationId.replaceFirstChar { it.uppercase() }}",
                        fontSize = 11.sp,
                        fontWeight = FontWeight.Medium,
                        color = OptimusTokens.Accent,
                        modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp)
                    )
                }
            }

            // Current Objective
            Text(
                text = project.objective,
                fontSize = 13.sp,
                color = OptimusTokens.TextPrimary,
                lineHeight = 18.sp
            )

            // Short Progress Sentence
            Surface(
                shape = RoundedCornerShape(8.dp),
                color = OptimusTokens.SurfaceRaised,
                modifier = Modifier.fillMaxWidth()
            ) {
                Text(
                    text = "• ${project.progressSentence}",
                    fontSize = 12.sp,
                    color = OptimusTokens.TextSecondary,
                    modifier = Modifier.padding(10.dp)
                )
            }

            // Completed / Total Tasks with Progress Bar
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween
                ) {
                    Text(
                        text = "Tasks completed",
                        fontSize = 11.sp,
                        color = OptimusTokens.TextSecondary
                    )
                    Text(
                        text = "${project.completedTasks}/${project.totalTasks}",
                        fontSize = 11.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = OptimusTokens.Accent
                    )
                }
                LinearProgressIndicator(
                    progress = { project.completedTasks.toFloat() / project.totalTasks.coerceAtLeast(1) },
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(6.dp)
                        .clip(RoundedCornerShape(3.dp)),
                    color = OptimusTokens.Accent,
                    trackColor = OptimusTokens.SurfaceRaised
                )
            }

            // Active Workers
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(text = "Workers:", fontSize = 11.sp, color = OptimusTokens.TextSecondary)
                project.activeWorkers.forEach { worker ->
                    Surface(
                        shape = RoundedCornerShape(6.dp),
                        color = OptimusTokens.SurfaceRaised
                    ) {
                        Text(
                            text = worker.name,
                            fontSize = 11.sp,
                            color = OptimusTokens.TextPrimary,
                            modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp)
                        )
                    }
                }
            }

            // Next Action
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                Text(text = "Next:", fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = OptimusTokens.Warning)
                Text(
                    text = project.nextAction,
                    fontSize = 12.sp,
                    color = OptimusTokens.TextPrimary,
                    maxLines = 1
                )
            }

            // Bottom Actions: "Talk here" explicit voice destination lock
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Button(
                    onClick = onTalkHere,
                    modifier = Modifier.defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                    shape = OptimusTokens.CornerControl,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = OptimusTokens.Accent,
                        contentColor = OptimusTokens.Background
                    )
                ) {
                    Icon(
                        imageVector = Icons.Default.Lock,
                        contentDescription = null,
                        modifier = Modifier.size(14.dp)
                    )
                    Spacer(modifier = Modifier.width(6.dp))
                    Text("Talk here", fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                }
            }
        }
    }
}

/** Project Detail View: Objective, Conversations, Task Board, Recent Results. */
@Composable
private fun ProjectDetailView(
    project: ProjectItem,
    onBack: () -> Unit,
    onSelectTask: (ProjectTask) -> Unit,
    onTalkHere: () -> Unit,
    onOpenThreadSheet: () -> Unit,
    onOpenReader: (String, String) -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        // Back Button & Title Header
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            IconButton(
                onClick = onBack,
                modifier = Modifier.size(OptimusTokens.MinTouchTarget)
            ) {
                Icon(
                    imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                    contentDescription = "Back to projects",
                    tint = OptimusTokens.TextPrimary
                )
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = project.name,
                    fontSize = 18.sp,
                    fontWeight = FontWeight.Bold,
                    color = OptimusTokens.TextPrimary
                )
                Text(
                    text = "Destination: ${project.destinationId.replaceFirstChar { it.uppercase() }}",
                    fontSize = 12.sp,
                    color = OptimusTokens.Accent
                )
            }
            Button(
                onClick = onTalkHere,
                shape = OptimusTokens.CornerControl,
                modifier = Modifier.defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                colors = ButtonDefaults.buttonColors(
                    containerColor = OptimusTokens.Accent,
                    contentColor = OptimusTokens.Background
                )
            ) {
                Icon(Icons.Default.Lock, contentDescription = null, modifier = Modifier.size(14.dp))
                Spacer(modifier = Modifier.width(4.dp))
                Text("Talk here", fontSize = 12.sp)
            }
        }

        // Objective Card
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = OptimusTokens.CornerCard,
            colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
        ) {
            Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text("OBJECTIVE", fontSize = 10.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextSecondary)
                Text(project.objective, fontSize = 14.sp, color = OptimusTokens.TextPrimary)
                Text(
                    "Next: ${project.nextAction}",
                    fontSize = 12.sp,
                    color = OptimusTokens.Warning,
                    fontWeight = FontWeight.Medium
                )
            }
        }

        // Conversations Section
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("CONVERSATIONS", fontSize = 11.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextSecondary)
            TextButton(onClick = onOpenThreadSheet) {
                Text("View All (${project.conversations.size})", fontSize = 12.sp, color = OptimusTokens.Accent)
            }
        }
        project.conversations.forEach { conv ->
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(OptimusTokens.CornerControl)
                    .border(1.dp, OptimusTokens.Border, OptimusTokens.CornerControl)
                    .clickable { onOpenThreadSheet() },
                shape = OptimusTokens.CornerControl,
                color = OptimusTokens.SurfaceRaised
            ) {
                Row(
                    modifier = Modifier.padding(12.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(conv.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = OptimusTokens.TextPrimary)
                        Text(conv.lastMessage, fontSize = 11.sp, color = OptimusTokens.TextSecondary, maxLines = 1)
                    }
                    Text(conv.timestamp, fontSize = 10.sp, color = OptimusTokens.TextTertiary)
                }
            }
        }

        // Task Board Section
        Text("TASK BOARD", fontSize = 11.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextSecondary)
        project.tasks.forEach { task ->
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(OptimusTokens.CornerCard)
                    .border(1.dp, OptimusTokens.Border, OptimusTokens.CornerCard)
                    .clickable { onSelectTask(task) },
                shape = OptimusTokens.CornerCard,
                color = OptimusTokens.Surface
            ) {
                Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text(
                            text = task.title,
                            fontSize = 14.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = OptimusTokens.TextPrimary,
                            modifier = Modifier.weight(1f)
                        )
                        Surface(
                            shape = RoundedCornerShape(6.dp),
                            color = if (task.status == "Completed") OptimusTokens.Success.copy(alpha = 0.2f)
                            else OptimusTokens.Accent.copy(alpha = 0.2f)
                        ) {
                            Text(
                                text = task.status,
                                fontSize = 10.sp,
                                fontWeight = FontWeight.Bold,
                                color = if (task.status == "Completed") OptimusTokens.Success else OptimusTokens.Accent,
                                modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp)
                            )
                        }
                    }

                    Text(
                        text = "Assigned: ${task.agent.name}",
                        fontSize = 11.sp,
                        color = OptimusTokens.TextSecondary
                    )

                    LinearProgressIndicator(
                        progress = { task.progressPercent / 100f },
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(4.dp)
                            .clip(RoundedCornerShape(2.dp)),
                        color = OptimusTokens.Accent,
                        trackColor = OptimusTokens.SurfaceRaised
                    )
                }
            }
        }

        // Recent Results Card
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = OptimusTokens.CornerCard,
            colors = CardDefaults.cardColors(containerColor = OptimusTokens.SurfaceRaised)
        ) {
            Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text("RECENT RESULTS", fontSize = 10.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextSecondary)
                Text(project.recentResultSummary, fontSize = 12.sp, color = OptimusTokens.TextPrimary)
                TextButton(
                    onClick = { onOpenReader("Recent Results — ${project.name}", project.recentResultSummary) },
                    modifier = Modifier.align(Alignment.End)
                ) {
                    Text("Read Full Report", fontSize = 12.sp, color = OptimusTokens.Accent)
                }
            }
        }

        Spacer(modifier = Modifier.height(84.dp))
    }
}

/** Task Detail View: Conversation, progress, changed-file summaries, agent identity, task controls. */
@Composable
private fun TaskDetailView(
    task: ProjectTask,
    onBack: () -> Unit,
    onTalkHere: () -> Unit,
    onOpenReader: (String, String) -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)
    ) {
        // Header
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            IconButton(
                onClick = onBack,
                modifier = Modifier.size(OptimusTokens.MinTouchTarget)
            ) {
                Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back", tint = OptimusTokens.TextPrimary)
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(task.title, fontSize = 16.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextPrimary)
                Text("Status: ${task.status} (${task.progressPercent}%)", fontSize = 12.sp, color = OptimusTokens.Accent)
            }
        }

        // Agent Identity Card
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = OptimusTokens.CornerCard,
            colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
        ) {
            Row(
                modifier = Modifier.padding(14.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Box(
                    modifier = Modifier
                        .size(40.dp)
                        .clip(CircleShape)
                        .background(OptimusTokens.SurfaceRaised),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = task.agent.name.take(2).uppercase(),
                        fontWeight = FontWeight.Bold,
                        color = OptimusTokens.Accent,
                        fontSize = 14.sp
                    )
                }
                Column {
                    Text(task.agent.name, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = OptimusTokens.TextPrimary)
                    Text("${task.agent.role} • ${task.agent.model}", fontSize = 11.sp, color = OptimusTokens.TextSecondary)
                }
            }
        }

        // Conversation Snippet Card
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = OptimusTokens.CornerCard,
            colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
        ) {
            Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text("CONVERSATION SNIPPET", fontSize = 10.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextSecondary)
                Text(task.conversationSnippet, fontSize = 13.sp, color = OptimusTokens.TextPrimary)
            }
        }

        // Changed Files Summary
        if (task.changedFiles.isNotEmpty()) {
            Card(
                modifier = Modifier.fillMaxWidth(),
                shape = OptimusTokens.CornerCard,
                colors = CardDefaults.cardColors(containerColor = OptimusTokens.Surface)
            ) {
                Column(modifier = Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("CHANGED FILES", fontSize = 10.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextSecondary)
                    task.changedFiles.forEach { file ->
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.SpaceBetween,
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Text(file.path, fontSize = 12.sp, fontFamily = FontFamily.Monospace, color = OptimusTokens.TextPrimary)
                            Text(
                                "+${file.additions} / -${file.deletions}",
                                fontSize = 11.sp,
                                fontFamily = FontFamily.Monospace,
                                color = OptimusTokens.Success
                            )
                        }
                    }
                }
            }
        }

        // Long Plan / Result button
        if (task.planOrResultContent.isNotBlank()) {
            OutlinedButton(
                onClick = { onOpenReader(task.title, task.planOrResultContent) },
                modifier = Modifier
                    .fillMaxWidth()
                    .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                shape = OptimusTokens.CornerControl
            ) {
                Text("Read Full Plan & Results", fontSize = 13.sp, color = OptimusTokens.Accent)
            }
        }

        // Task Controls (Pause, Resume, Talk to Agent)
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            OutlinedButton(
                onClick = {},
                modifier = Modifier
                    .weight(1f)
                    .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                shape = OptimusTokens.CornerControl
            ) {
                Text("Pause", fontSize = 12.sp)
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
                Spacer(modifier = Modifier.width(4.dp))
                Text("Talk to Agent", fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
            }
        }

        Spacer(modifier = Modifier.height(84.dp))
    }
}

/** Full Screen Reader Dialog for long plans and results. */
@Composable
private fun FullScreenReaderDialog(
    title: String,
    content: String,
    onDismiss: () -> Unit
) {
    Dialog(
        onDismissRequest = onDismiss,
        properties = DialogProperties(usePlatformDefaultWidth = false)
    ) {
        Surface(
            modifier = Modifier
                .fillMaxSize()
                .background(OptimusTokens.Background),
            color = OptimusTokens.Background
        ) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(16.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = title,
                        fontSize = 17.sp,
                        fontWeight = FontWeight.Bold,
                        color = OptimusTokens.TextPrimary,
                        modifier = Modifier.weight(1f)
                    )
                    IconButton(
                        onClick = onDismiss,
                        modifier = Modifier.size(OptimusTokens.MinTouchTarget)
                    ) {
                        Icon(Icons.Default.Close, contentDescription = "Close reader", tint = OptimusTokens.TextPrimary)
                    }
                }

                Spacer(modifier = Modifier.height(12.dp))

                Surface(
                    modifier = Modifier
                        .weight(1f)
                        .fillMaxWidth()
                        .clip(OptimusTokens.CornerCard)
                        .border(1.dp, OptimusTokens.Border, OptimusTokens.CornerCard),
                    color = OptimusTokens.Surface
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .verticalScroll(rememberScrollState())
                            .padding(16.dp)
                    ) {
                        Text(
                            text = content,
                            fontSize = 13.sp,
                            lineHeight = 20.sp,
                            fontFamily = FontFamily.Monospace,
                            color = OptimusTokens.TextPrimary
                        )
                    }
                }

                Spacer(modifier = Modifier.height(12.dp))

                Button(
                    onClick = onDismiss,
                    modifier = Modifier
                        .fillMaxWidth()
                        .defaultMinSize(minHeight = OptimusTokens.MinTouchTarget),
                    shape = OptimusTokens.CornerControl,
                    colors = ButtonDefaults.buttonColors(
                        containerColor = OptimusTokens.Accent,
                        contentColor = OptimusTokens.Background
                    )
                ) {
                    Text("Close Reader", fontWeight = FontWeight.SemiBold)
                }
            }
        }
    }
}

/** Conversation Thread Bottom Sheet. */
@Composable
private fun ConversationThreadBottomSheet(
    conversations: List<ProjectConversation>,
    onSelectThread: (ProjectConversation) -> Unit,
    onTalkHere: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("PROJECT CONVERSATIONS", fontSize = 14.sp, fontWeight = FontWeight.Bold, color = OptimusTokens.TextPrimary)
            Button(
                onClick = onTalkHere,
                shape = OptimusTokens.CornerControl,
                colors = ButtonDefaults.buttonColors(
                    containerColor = OptimusTokens.Accent,
                    contentColor = OptimusTokens.Background
                )
            ) {
                Text("Talk here", fontSize = 12.sp)
            }
        }

        conversations.forEach { conv ->
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(OptimusTokens.CornerControl)
                    .border(1.dp, OptimusTokens.Border, OptimusTokens.CornerControl)
                    .clickable { onSelectThread(conv) },
                shape = OptimusTokens.CornerControl,
                color = OptimusTokens.Surface
            ) {
                Row(
                    modifier = Modifier.padding(14.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(conv.title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = OptimusTokens.TextPrimary)
                        Text(conv.lastMessage, fontSize = 12.sp, color = OptimusTokens.TextSecondary)
                    }
                    Text("${conv.messageCount} msgs", fontSize = 11.sp, color = OptimusTokens.Accent)
                }
            }
        }

        Spacer(modifier = Modifier.height(24.dp))
    }
}
