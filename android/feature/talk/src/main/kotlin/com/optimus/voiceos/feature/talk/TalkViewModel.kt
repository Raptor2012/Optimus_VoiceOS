package com.optimus.voiceos.feature.talk

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import android.app.Application

/**
 * Owns the connection across Activity recreation.
 *
 * The controller cannot live in the Activity: showing the keyboard or rotating the phone
 * destroys and recreates it, and disposing on `onDestroy` tore down a live socket mid-session.
 * A ViewModel survives configuration changes and is cleared only when the screen really goes
 * away, which is exactly the lifetime the connection should have.
 */
class TalkViewModel(application: Application) : AndroidViewModel(application) {

    private val preferences = application.getSharedPreferences("optimus-connection", 0)

    var uiState by mutableStateOf(TalkUiState())
        private set

    private val controller = TalkController { uiState = it }

    init {
        controller.setHost(preferences.getString("host", "") ?: "")
        controller.setPort(preferences.getString("port", "8770") ?: "8770")
        if (uiState.host.isNotBlank()) controller.connect()
    }

    fun setHost(host: String) = controller.setHost(host)

    fun setPort(port: String) = controller.setPort(port)

    fun connect() {
        preferences.edit().putString("host", uiState.host).putString("port", uiState.port).apply()
        controller.connect()
    }

    fun disconnect() = controller.disconnect()

    fun startCapture() = controller.startCapture()

    fun stopCapture() = controller.stopCapture()

    fun setDraft(text: String) = controller.setDraft(text)

    fun selectDestination(id: String) = controller.selectDestination(id)

    fun refreshDestinations() = controller.refreshDestinations()

    fun confirm() = controller.confirm()

    fun cancelDraft() = controller.cancelDraft()

    fun setNarrationMode(mode: String) = controller.setNarrationMode(mode)

    fun setNarrateToolsAndSkills(enabled: Boolean) = controller.setNarrateToolsAndSkills(enabled)

    var onProjectsReceived: ((List<com.optimus.voiceos.core.transport.PcProjectItem>) -> Unit)?
        get() = controller.onProjectsReceived
        set(value) { controller.onProjectsReceived = value }

    fun requestProjects() = controller.requestProjects()

    override fun onCleared() {
        controller.dispose()
        super.onCleared()
    }
}
