package com.optimus.voiceos.core.ui

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.optimus.voiceos.ui.theme.OptimusTokens
import kotlin.math.sin

import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.ui.input.pointer.pointerInput

/**
 * Visual states of the voice interaction matching the PC WPF widget states.
 */
enum class VoiceOrbState {
    Idle,
    Listening,
    Processing,
    Confirm,
    Sending,
    Sent,
    Error,
    ReadingDraft,
    AwaitingApproval,
    Redictating,
    SessionListening
}

/**
 * Shared animated Voice Orb drawn on Canvas.
 *
 * Renders an expressive, reactive robotic core with state-dependent ripples, rotating
 * orbital rings, and glowing gradients.
 *
 * Supports both hold-to-talk (press to start, release to stop) and tap-to-toggle.
 */
@Composable
fun VoiceOrb(
    state: VoiceOrbState,
    modifier: Modifier = Modifier,
    size: Dp = 140.dp,
    isCapturing: Boolean = false,
    onStartCapture: (() -> Unit)? = null,
    onStopCapture: (() -> Unit)? = null,
    onClick: (() -> Unit)? = null
) {
    val infiniteTransition = rememberInfiniteTransition(label = "VoiceOrbTransition")

    // Slow ambient breathing pulse (used in Idle, SessionListening, Confirm)
    val ambientPulse by infiniteTransition.animateFloat(
        initialValue = 0.94f,
        targetValue = 1.06f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 1800, easing = LinearEasing),
            repeatMode = RepeatMode.Reverse
        ),
        label = "ambientPulse"
    )

    // Fast acoustic / voice energy ripple (Listening, Redictating)
    val voiceRipple by infiniteTransition.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 1100, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "voiceRipple"
    )

    // High-speed rotation (Processing, Sending)
    val rotationAngle by infiniteTransition.animateFloat(
        initialValue = 0f,
        targetValue = 360f,
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 1600, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "rotationAngle"
    )

    // Undulating sine wave for TTS / ReadingDraft
    val wavePhase by infiniteTransition.animateFloat(
        initialValue = 0f,
        targetValue = 6.283f, // 2 * PI
        animationSpec = infiniteRepeatable(
            animation = tween(durationMillis = 1300, easing = LinearEasing),
            repeatMode = RepeatMode.Restart
        ),
        label = "wavePhase"
    )

    // Base state colors matching WPF
    val stateColor = when (state) {
        VoiceOrbState.Idle -> OptimusTokens.Success
        VoiceOrbState.Listening -> OptimusTokens.Listening
        VoiceOrbState.Processing -> OptimusTokens.Warning
        VoiceOrbState.Confirm -> OptimusTokens.Accent
        VoiceOrbState.Sending -> OptimusTokens.Sending
        VoiceOrbState.Sent -> OptimusTokens.Success
        VoiceOrbState.Error -> OptimusTokens.Error
        VoiceOrbState.ReadingDraft -> OptimusTokens.Reading
        VoiceOrbState.AwaitingApproval -> OptimusTokens.Accent
        VoiceOrbState.Redictating -> OptimusTokens.Listening
        VoiceOrbState.SessionListening -> OptimusTokens.Success
    }

    val interactionSource = remember { MutableInteractionSource() }

    val touchModifier = when {
        onStartCapture != null && onStopCapture != null -> {
            Modifier.pointerInput(isCapturing, onStartCapture, onStopCapture) {
                detectTapGestures(
                    onPress = {
                        val downTime = System.currentTimeMillis()
                        if (isCapturing) {
                            onStopCapture()
                            tryAwaitRelease()
                        } else {
                            onStartCapture()
                            val released = tryAwaitRelease()
                            val elapsed = System.currentTimeMillis() - downTime
                            if (released && elapsed > 300) {
                                // User held down to talk and released
                                onStopCapture()
                            } else if (!released) {
                                // Cancelled / dragged off
                                onStopCapture()
                            }
                            // Otherwise user quick-tapped (<= 300ms): keep capturing active for hands-free dictation
                        }
                    }
                )
            }
        }
        onClick != null -> {
            Modifier.clickable(
                interactionSource = interactionSource,
                indication = null,
                onClick = onClick
            )
        }
        else -> Modifier
    }

    Box(
        modifier = modifier
            .defaultMinSize(minWidth = OptimusTokens.MinTouchTarget, minHeight = OptimusTokens.MinTouchTarget)
            .size(size)
            .then(touchModifier),
        contentAlignment = Alignment.Center
    ) {
        Canvas(modifier = Modifier.size(size)) {
            val center = Offset(this.size.width / 2f, this.size.height / 2f)
            val baseRadius = this.size.minDimension * 0.30f

            // 1. Draw outer dynamic ripples or halo depending on state
            when (state) {
                VoiceOrbState.Listening, VoiceOrbState.Redictating -> {
                    // Two expanding acoustic ripples
                    val rippleRadius1 = baseRadius + (voiceRipple * baseRadius * 1.3f)
                    val rippleAlpha1 = (1f - voiceRipple).coerceIn(0f, 0.7f)
                    drawCircle(
                        color = stateColor.copy(alpha = rippleAlpha1),
                        radius = rippleRadius1,
                        center = center,
                        style = Stroke(width = 3.dp.toPx())
                    )

                    val secondPhase = (voiceRipple + 0.5f) % 1.0f
                    val rippleRadius2 = baseRadius + (secondPhase * baseRadius * 1.3f)
                    val rippleAlpha2 = (1f - secondPhase).coerceIn(0f, 0.6f)
                    drawCircle(
                        color = stateColor.copy(alpha = rippleAlpha2 * 0.7f),
                        radius = rippleRadius2,
                        center = center,
                        style = Stroke(width = 2.dp.toPx())
                    )
                }

                VoiceOrbState.Processing -> {
                    // Orbital spinning arc tracks
                    val arcRadius = baseRadius * 1.28f
                    drawArc(
                        brush = Brush.sweepGradient(
                            listOf(
                                stateColor.copy(alpha = 0.05f),
                                stateColor.copy(alpha = 0.85f),
                                stateColor.copy(alpha = 0.1f)
                            )
                        ),
                        startAngle = rotationAngle,
                        sweepAngle = 140f,
                        useCenter = false,
                        topLeft = Offset(center.x - arcRadius, center.y - arcRadius),
                        size = androidx.compose.ui.geometry.Size(arcRadius * 2f, arcRadius * 2f),
                        style = Stroke(width = 4.dp.toPx(), cap = StrokeCap.Round)
                    )

                    drawArc(
                        brush = Brush.sweepGradient(
                            listOf(
                                stateColor.copy(alpha = 0.1f),
                                stateColor.copy(alpha = 0.6f),
                                stateColor.copy(alpha = 0.05f)
                            )
                        ),
                        startAngle = rotationAngle + 180f,
                        sweepAngle = 80f,
                        useCenter = false,
                        topLeft = Offset(center.x - arcRadius, center.y - arcRadius),
                        size = androidx.compose.ui.geometry.Size(arcRadius * 2f, arcRadius * 2f),
                        style = Stroke(width = 2.5.dp.toPx(), cap = StrokeCap.Round)
                    )
                }

                VoiceOrbState.ReadingDraft -> {
                    // Undulating voice frequency ring
                    val waveRadius = baseRadius * (1.18f + 0.12f * sin(wavePhase))
                    drawCircle(
                        color = stateColor.copy(alpha = 0.45f),
                        radius = waveRadius,
                        center = center,
                        style = Stroke(width = 3.dp.toPx())
                    )
                    val waveRadius2 = baseRadius * (1.32f + 0.10f * sin(wavePhase + 2.0f))
                    drawCircle(
                        color = stateColor.copy(alpha = 0.25f),
                        radius = waveRadius2,
                        center = center,
                        style = Stroke(width = 1.5.dp.toPx())
                    )
                }

                VoiceOrbState.Confirm, VoiceOrbState.AwaitingApproval -> {
                    // Dual-tone beacon halo
                    val beaconRadius = baseRadius * (1.12f * ambientPulse)
                    drawCircle(
                        color = stateColor.copy(alpha = 0.35f),
                        radius = beaconRadius,
                        center = center,
                        style = Stroke(width = 2.5.dp.toPx())
                    )
                }

                VoiceOrbState.Sending -> {
                    val sendRadius = baseRadius * (1.2f + (rotationAngle / 360f) * 0.4f)
                    drawCircle(
                        color = stateColor.copy(alpha = (1f - (rotationAngle / 360f)) * 0.5f),
                        radius = sendRadius,
                        center = center,
                        style = Stroke(width = 3.dp.toPx())
                    )
                }

                VoiceOrbState.Sent -> {
                    drawCircle(
                        color = stateColor.copy(alpha = 0.5f),
                        radius = baseRadius * 1.35f,
                        center = center,
                        style = Stroke(width = 4.dp.toPx())
                    )
                }

                VoiceOrbState.Error -> {
                    drawCircle(
                        color = stateColor.copy(alpha = 0.6f),
                        radius = baseRadius * 1.25f,
                        center = center,
                        style = Stroke(width = 3.5.dp.toPx())
                    )
                }

                VoiceOrbState.Idle, VoiceOrbState.SessionListening -> {
                    // Soft resting ambient ring
                    drawCircle(
                        color = stateColor.copy(alpha = 0.22f),
                        radius = baseRadius * ambientPulse * 1.14f,
                        center = center,
                        style = Stroke(width = 2.dp.toPx())
                    )
                }
            }

            // 2. Outer luminous glow disk
            val glowRadius = when (state) {
                VoiceOrbState.Listening, VoiceOrbState.Redictating -> baseRadius * 1.15f
                VoiceOrbState.Processing -> baseRadius * 1.08f
                VoiceOrbState.ReadingDraft -> baseRadius * 1.12f
                VoiceOrbState.Confirm, VoiceOrbState.AwaitingApproval -> baseRadius * ambientPulse
                else -> baseRadius * 1.05f
            }

            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(
                        stateColor.copy(alpha = 0.55f),
                        stateColor.copy(alpha = 0.20f),
                        Color.Transparent
                    ),
                    center = center,
                    radius = glowRadius * 1.4f
                ),
                radius = glowRadius * 1.4f,
                center = center
            )

            // 3. Core solid / gradient sphere
            val coreRadius = baseRadius * when (state) {
                VoiceOrbState.Listening, VoiceOrbState.Redictating -> 1.02f
                VoiceOrbState.ReadingDraft -> 0.98f + 0.05f * sin(wavePhase)
                VoiceOrbState.Confirm -> 0.96f * ambientPulse
                else -> 0.96f
            }

            drawCircle(
                brush = Brush.radialGradient(
                    colors = listOf(
                        Color.White.copy(alpha = 0.95f),
                        stateColor,
                        stateColor.copy(alpha = 0.85f),
                        Color(0xFF0D121B)
                    ),
                    center = Offset(center.x - coreRadius * 0.25f, center.y - coreRadius * 0.25f),
                    radius = coreRadius * 1.2f
                ),
                radius = coreRadius,
                center = center
            )

            // 4. Futuristic inner aperture ring
            drawCircle(
                color = Color.White.copy(alpha = 0.35f),
                radius = coreRadius * 0.45f,
                center = center,
                style = Stroke(width = 1.5.dp.toPx())
            )
        }
    }
}
