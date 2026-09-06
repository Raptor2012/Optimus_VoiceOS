package com.optimus.voiceos.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.optimus.voiceos.MainActivity
import com.optimus.voiceos.R

/**
 * Android Foreground Service managing active voice conversation lifetime.
 *
 * Keeps microphone and TCP audio streaming active when switching apps or locking the screen.
 * Exposes Notification controls for Mute and End.
 */
class VoiceConversationService : Service() {

    companion object {
        const val CHANNEL_ID = "optimus_voice_conversation"
        const val NOTIFICATION_ID = 4096

        const val ACTION_START = "com.optimus.voiceos.action.START"
        const val ACTION_STOP = "com.optimus.voiceos.action.STOP"
        const val ACTION_MUTE = "com.optimus.voiceos.action.MUTE"
        const val ACTION_UNMUTE = "com.optimus.voiceos.action.UNMUTE"
        const val ACTION_END = "com.optimus.voiceos.action.END"

        const val EXTRA_DESTINATION = "extra_destination"

        var isServiceRunning = false
            private set

        var isMuted = false
            private set

        var onMuteToggled: ((Boolean) -> Unit)? = null
        var onConversationEnded: (() -> Unit)? = null

        fun start(context: Context, destination: String = "Locked Destination") {
            val intent = Intent(context, VoiceConversationService::class.java).apply {
                action = ACTION_START
                putExtra(EXTRA_DESTINATION, destination)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun stop(context: Context) {
            val intent = Intent(context, VoiceConversationService::class.java).apply {
                action = ACTION_STOP
            }
            context.startService(intent)
        }
    }

    private var currentDestination: String = "Locked Destination"

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> {
                isServiceRunning = true
                currentDestination = intent.getStringExtra(EXTRA_DESTINATION) ?: "Active Destination"
                val notification = buildNotification(isMuted)

                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    startForeground(
                        NOTIFICATION_ID,
                        notification,
                        ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE or
                            ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
                    )
                } else {
                    startForeground(NOTIFICATION_ID, notification)
                }
            }

            ACTION_MUTE -> {
                isMuted = true
                onMuteToggled?.invoke(true)
                updateNotification()
            }

            ACTION_UNMUTE -> {
                isMuted = false
                onMuteToggled?.invoke(false)
                updateNotification()
            }

            ACTION_END -> {
                onConversationEnded?.invoke()
                stopForegroundService()
            }

            ACTION_STOP -> {
                stopForegroundService()
            }
        }

        return START_NOT_STICKY
    }

    private fun stopForegroundService() {
        isServiceRunning = false
        isMuted = false
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } else {
            @Suppress("DEPRECATION")
            stopForeground(true)
        }
        stopSelf()
    }

    private fun updateNotification() {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(NOTIFICATION_ID, buildNotification(isMuted))
    }

    private fun buildNotification(muted: Boolean): Notification {
        val openAppIntent = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
        }
        val openAppPendingIntent = PendingIntent.getActivity(
            this, 0, openAppIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        // Mute / Unmute action
        val muteActionIntent = Intent(this, VoiceConversationService::class.java).apply {
            action = if (muted) ACTION_UNMUTE else ACTION_MUTE
        }
        val mutePendingIntent = PendingIntent.getService(
            this, 1, muteActionIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val muteTitle = if (muted) "Unmute" else "Mute"

        // End Conversation action
        val endActionIntent = Intent(this, VoiceConversationService::class.java).apply {
            action = ACTION_END
        }
        val endPendingIntent = PendingIntent.getService(
            this, 2, endActionIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val statusText = if (muted) "Microphone muted" else "Listening & streaming audio"

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Optimus Voice OS — $currentDestination")
            .setContentText(statusText)
            .setSmallIcon(R.drawable.ic_optimus_foreground)
            .setOngoing(true)
            .setContentIntent(openAppPendingIntent)
            .addAction(android.R.drawable.ic_lock_silent_mode, muteTitle, mutePendingIntent)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, "End", endPendingIntent)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "Voice Conversation",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Keeps Optimus Voice OS active when app is in background or device is locked"
                setShowBadge(false)
            }
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.createNotificationChannel(channel)
        }
    }

    override fun onDestroy() {
        isServiceRunning = false
        super.onDestroy()
    }
}
