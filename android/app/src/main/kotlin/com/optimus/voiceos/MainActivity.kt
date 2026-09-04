package com.optimus.voiceos

import android.Manifest
import android.content.pm.PackageManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
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
            // Follows the system setting, so the phone gets a real dark mode.
            MaterialTheme(
                colorScheme = if (isSystemInDarkTheme()) darkColorScheme() else lightColorScheme()
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
                        onCancel = vm::cancelDraft
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
