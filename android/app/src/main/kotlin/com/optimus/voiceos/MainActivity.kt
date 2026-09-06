package com.optimus.voiceos

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.Notifications
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.ViewModelProvider
import com.optimus.voiceos.core.ui.CompactConversationCapsule
import com.optimus.voiceos.feature.projects.ProjectsScreen
import com.optimus.voiceos.feature.projects.ProjectsViewModel
import com.optimus.voiceos.feature.talk.TalkScreen
import com.optimus.voiceos.feature.talk.TalkViewModel
import com.optimus.voiceos.feature.talk.selectedDestinationName
import com.optimus.voiceos.feature.talk.toVoiceOrbState
import com.optimus.voiceos.feature.updates.UpdatesScreen
import com.optimus.voiceos.feature.updates.UpdatesViewModel
import com.optimus.voiceos.service.VoiceConversationService
import com.optimus.voiceos.ui.theme.OptimusTheme
import com.optimus.voiceos.ui.theme.OptimusTokens
import kotlinx.coroutines.launch

enum class AppDestination(val label: String) {
    Talk("Talk"),
    Projects("Projects"),
    Updates("Updates")
}

class MainActivity : ComponentActivity() {

    private lateinit var talkModel: TalkViewModel
    private lateinit var projectsModel: ProjectsViewModel
    private lateinit var updatesModel: UpdatesViewModel

    private val requestMic = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) {
            talkModel.onPermissionGranted()
        } else {
            talkModel.onPermissionDenied()
        }
    }

    private val requestNotification = registerForActivityResult(ActivityResultContracts.RequestPermission()) { _ -> }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // Request notification permission on Android 13+
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                requestNotification.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }

        talkModel = ViewModelProvider(this)[TalkViewModel::class.java]
        projectsModel = ViewModelProvider(this)[ProjectsViewModel::class.java]
        updatesModel = ViewModelProvider(this)[UpdatesViewModel::class.java]

        talkModel.onProjectsReceived = { liveProjects ->
            projectsModel.updateFromLive(liveProjects)
        }

        // Wire service callbacks to ViewModel
        VoiceConversationService.onConversationEnded = {
            talkModel.endConversation()
        }
        VoiceConversationService.onMuteToggled = { muted ->
            if (muted && talkModel.uiState.userUtteranceInProgress) {
                talkModel.onGestureCancel()
            }
        }

        setContent {
            OptimusTheme {
                val talkVm = remember { talkModel }
                val projectsVm = remember { projectsModel }
                val updatesVm = remember { updatesModel }
                val context = LocalContext.current
                val coroutineScope = rememberCoroutineScope()
                val snackbarHostState = remember { SnackbarHostState() }

                var currentDestination by rememberSaveable { mutableStateOf(AppDestination.Talk) }

                // Synchronize foreground service with voice conversation state
                LaunchedEffect(talkVm.uiState.conversationEnabled, talkVm.uiState.userUtteranceInProgress) {
                    val active = talkVm.uiState.conversationEnabled || talkVm.uiState.userUtteranceInProgress
                    if (active) {
                        VoiceConversationService.start(context, talkVm.uiState.selectedDestinationName)
                    } else {
                        VoiceConversationService.stop(context)
                    }
                }

                Scaffold(
                    modifier = Modifier.fillMaxSize(),
                    containerColor = OptimusTokens.Background,
                    snackbarHost = { SnackbarHost(snackbarHostState) },
                    bottomBar = {
                        NavigationBar(
                            containerColor = OptimusTokens.Surface,
                            tonalElevation = 8.dp
                        ) {
                            NavigationBarItem(
                                selected = currentDestination == AppDestination.Talk,
                                onClick = { currentDestination = AppDestination.Talk },
                                icon = {
                                    Icon(
                                        imageVector = Icons.Default.Home,
                                        contentDescription = "Talk",
                                        modifier = Modifier.size(24.dp)
                                    )
                                },
                                label = {
                                    Text(
                                        "Talk",
                                        fontWeight = if (currentDestination == AppDestination.Talk) FontWeight.Bold else FontWeight.Normal,
                                        fontSize = 12.sp
                                    )
                                },
                                colors = NavigationBarItemDefaults.colors(
                                    selectedIconColor = OptimusTokens.Accent,
                                    selectedTextColor = OptimusTokens.Accent,
                                    indicatorColor = OptimusTokens.SurfaceRaised,
                                    unselectedIconColor = OptimusTokens.TextSecondary,
                                    unselectedTextColor = OptimusTokens.TextSecondary
                                )
                            )

                            NavigationBarItem(
                                selected = currentDestination == AppDestination.Projects,
                                onClick = { currentDestination = AppDestination.Projects },
                                icon = {
                                    Icon(
                                        imageVector = Icons.AutoMirrored.Filled.List,
                                        contentDescription = "Projects",
                                        modifier = Modifier.size(24.dp)
                                    )
                                },
                                label = {
                                    Text(
                                        "Projects",
                                        fontWeight = if (currentDestination == AppDestination.Projects) FontWeight.Bold else FontWeight.Normal,
                                        fontSize = 12.sp
                                    )
                                },
                                colors = NavigationBarItemDefaults.colors(
                                    selectedIconColor = OptimusTokens.Accent,
                                    selectedTextColor = OptimusTokens.Accent,
                                    indicatorColor = OptimusTokens.SurfaceRaised,
                                    unselectedIconColor = OptimusTokens.TextSecondary,
                                    unselectedTextColor = OptimusTokens.TextSecondary
                                )
                            )

                            NavigationBarItem(
                                selected = currentDestination == AppDestination.Updates,
                                onClick = { currentDestination = AppDestination.Updates },
                                icon = {
                                    Icon(
                                        imageVector = Icons.Default.Notifications,
                                        contentDescription = "Updates",
                                        modifier = Modifier.size(24.dp)
                                    )
                                },
                                label = {
                                    Text(
                                        "Updates",
                                        fontWeight = if (currentDestination == AppDestination.Updates) FontWeight.Bold else FontWeight.Normal,
                                        fontSize = 12.sp
                                    )
                                },
                                colors = NavigationBarItemDefaults.colors(
                                    selectedIconColor = OptimusTokens.Accent,
                                    selectedTextColor = OptimusTokens.Accent,
                                    indicatorColor = OptimusTokens.SurfaceRaised,
                                    unselectedIconColor = OptimusTokens.TextSecondary,
                                    unselectedTextColor = OptimusTokens.TextSecondary
                                )
                            )
                        }
                    }
                ) { innerPadding ->
                    Box(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(innerPadding)
                    ) {
                        // Render Active Destination Screen
                        when (currentDestination) {
                            AppDestination.Talk -> {
                                TalkScreen(
                                    state = talkVm.uiState,
                                    onHostChange = talkVm::setHost,
                                    onPortChange = talkVm::setPort,
                                    onConnect = talkVm::connect,
                                    onDisconnect = talkVm::disconnect,
                                    onGestureStart = ::handleGestureStart,
                                    onGestureEnd = talkVm::onGestureEnd,
                                    onGestureCancel = talkVm::onGestureCancel,
                                    onEndConversation = talkVm::endConversation,
                                    onStartCapture = ::handleGestureStart,
                                    onStopCapture = talkVm::stopCapture,
                                    onDraftChange = talkVm::setDraft,
                                    onSelectDestination = talkVm::selectDestination,
                                    onRefreshDestinations = talkVm::refreshDestinations,
                                    onConfirm = talkVm::confirm,
                                    onCancel = talkVm::cancelDraft,
                                    onNarrationModeChange = talkVm::setNarrationMode,
                                    onNarrateToolsChange = talkVm::setNarrateToolsAndSkills
                                )
                            }

                            AppDestination.Projects -> {
                                ProjectsScreen(
                                    state = projectsVm.uiState,
                                    onSelectProject = projectsVm::selectProject,
                                    onSelectTask = projectsVm::selectTask,
                                    onTalkHere = { destinationId ->
                                        talkVm.selectDestination(destinationId)
                                        coroutineScope.launch {
                                            snackbarHostState.showSnackbar("Voice locked to ${destinationId.replaceFirstChar { it.uppercase() }}")
                                        }
                                    },
                                    onOpenReader = projectsVm::openReader,
                                    onCloseReader = projectsVm::closeReader,
                                    onSetThreadSheetVisible = projectsVm::setThreadSheetVisible
                                )
                            }

                            AppDestination.Updates -> {
                                UpdatesScreen(
                                    state = updatesVm.uiState,
                                    onResolveDecision = updatesVm::resolveDecision,
                                    onOpenDecisionSheet = updatesVm::openDecisionSheet,
                                    onCloseDecisionSheet = updatesVm::closeDecisionSheet,
                                    onViewSourceEvent = updatesVm::viewSourceEvent,
                                    onCloseSourceEvent = updatesVm::closeSourceEvent,
                                    onTalkHere = { destinationId ->
                                        talkVm.selectDestination(destinationId)
                                        currentDestination = AppDestination.Talk
                                    }
                                )
                            }
                        }

                        // Floating compact active-conversation capsule visible when outside Talk
                        if (currentDestination != AppDestination.Talk) {
                            CompactConversationCapsule(
                                destinationName = talkVm.uiState.selectedDestinationName,
                                statusText = when {
                                    talkVm.uiState.playbackActive -> "Speaking..."
                                    talkVm.uiState.userUtteranceInProgress -> "Recording voice..."
                                    talkVm.uiState.conversationEnabled -> "Conversation active"
                                    else -> talkVm.uiState.status
                                },
                                orbState = talkVm.uiState.toVoiceOrbState(),
                                isCapturing = talkVm.uiState.userUtteranceInProgress,
                                conversationEnabled = talkVm.uiState.conversationEnabled,
                                onCapsuleClick = {
                                    currentDestination = AppDestination.Talk
                                },
                                onGestureStart = ::handleGestureStart,
                                onGestureEnd = talkVm::onGestureEnd,
                                onGestureCancel = talkVm::onGestureCancel,
                                onEndConversation = talkVm::endConversation,
                                modifier = Modifier
                                    .align(Alignment.BottomCenter)
                                    .padding(bottom = 8.dp)
                            )
                        }
                    }
                }
            }
        }
    }

    private fun handleGestureStart() {
        val granted = ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) ==
            PackageManager.PERMISSION_GRANTED

        if (granted) {
            talkModel.onGestureStart()
        } else {
            requestMic.launch(Manifest.permission.RECORD_AUDIO)
        }
    }

    private fun startCaptureWithPermission() {
        handleGestureStart()
    }
}
