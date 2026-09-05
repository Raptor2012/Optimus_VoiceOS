package com.optimus.voiceos

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.ui.graphics.Color
import androidx.compose.material3.Surface
import androidx.compose.runtime.remember
import androidx.core.content.ContextCompat
import androidx.lifecycle.ViewModelProvider
import com.optimus.voiceos.feature.talk.TalkScreen
import com.optimus.voiceos.feature.talk.TalkViewModel

class MainActivity : ComponentActivity() {

    private lateinit var model: TalkViewModel

    private val requestMic = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) model.startCapture()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // Retained across configuration changes, so the socket survives the keyboard opening
        // or the phone rotating.
        model = ViewModelProvider(this)[TalkViewModel::class.java]

        setContent {
            // Shared midnight / ice-blue palette with the Windows widget.
            MaterialTheme(
                colorScheme = darkColorScheme(
                    primary = Color(0xFF7DD3FC), onPrimary = Color(0xFF07121E),
                    secondary = Color(0xFFA7B8CD), background = Color(0xFF0B111B),
                    surface = Color(0xFF111C2B), surfaceVariant = Color(0xFF1A293C),
                    onBackground = Color(0xFFEAF2FA), onSurface = Color(0xFFEAF2FA),
                    onSurfaceVariant = Color(0xFFA7B8CD), outline = Color(0xFF34465C),
                    error = Color(0xFFFF9C95)
                )
            ) {
                Surface(color = MaterialTheme.colorScheme.background) {
                    val vm = remember { model }
                    TalkScreen(
                        state = vm.uiState,
                        onHostChange = vm::setHost,
                        onPortChange = vm::setPort,
                        onConnect = vm::connect,
                        onDisconnect = vm::disconnect,
                        onStartCapture = ::startCaptureWithPermission,
                        onStopCapture = vm::stopCapture,
                        onDraftChange = vm::setDraft,
                        onSelectDestination = vm::selectDestination,
                        onRefreshDestinations = vm::refreshDestinations,
                        onConfirm = vm::confirm,
                        onCancel = vm::cancelDraft,
                        onNarrationModeChange = vm::setNarrationMode,
                        onNarrateToolsChange = vm::setNarrateToolsAndSkills
                    )
                }
            }
        }
    }

    private fun startCaptureWithPermission() {
        val granted = ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) ==
            PackageManager.PERMISSION_GRANTED

        if (granted) {
            model.startCapture()
        } else {
            requestMic.launch(Manifest.permission.RECORD_AUDIO)
        }
    }
}
