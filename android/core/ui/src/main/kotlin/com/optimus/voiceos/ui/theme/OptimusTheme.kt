package com.optimus.voiceos.ui.theme

import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp

/**
 * Optimus Voice OS Design Tokens.
 *
 * Midnight & Ice-Blue / Electric-Indigo palette tuned for fast reading and tactile one-handed controls.
 */
object OptimusTokens {
    // Surface & Background Colors
    val Background = Color(0xFF101114)
    val Surface = Color(0xFF181A1F)
    val SurfaceRaised = Color(0xFF20232A)
    val SurfaceHighlight = Color(0xFF2B303B)

    // Text & Content Colors
    val TextPrimary = Color(0xFFF1F2F4)
    val TextSecondary = Color(0xFFA3A9B5)
    val TextTertiary = Color(0xFF6B7280)

    // Accent & Interaction Colors
    val Accent = Color(0xFF8C9EFF)
    val AccentHover = Color(0xFFA6B4FF)
    val AccentMuted = Color(0xFF384373)

    // Functional State Colors (matching WPF widget status mapping)
    val Success = Color(0xFF30D158)
    val Warning = Color(0xFFFF9500)
    val Error = Color(0xFFFF453A)
    val Listening = Color(0xFFFF3B30)
    val Reading = Color(0xFFBF5AF2)
    val Sending = Color(0xFF5E5CE6)

    // Borders & Dividers
    val Border = Color(0xFF282C35)
    val BorderFocused = Color(0xFF8C9EFF)

    // Shapes
    val CornerControl = RoundedCornerShape(12.dp)
    val CornerCard = RoundedCornerShape(16.dp)
    val CornerCapsule = RoundedCornerShape(28.dp)
    val CornerBottomSheet = RoundedCornerShape(topStart = 20.dp, topEnd = 20.dp)

    // Touch & Dimension Targets
    val MinTouchTarget = 48.dp
}

val LocalOptimusTokens = staticCompositionLocalOf { OptimusTokens }

@Composable
fun OptimusTheme(content: @Composable () -> Unit) {
    val darkColors = darkColorScheme(
        primary = OptimusTokens.Accent,
        onPrimary = OptimusTokens.Background,
        primaryContainer = OptimusTokens.SurfaceRaised,
        onPrimaryContainer = OptimusTokens.TextPrimary,

        secondary = OptimusTokens.TextSecondary,
        onSecondary = OptimusTokens.Background,
        secondaryContainer = OptimusTokens.SurfaceRaised,
        onSecondaryContainer = OptimusTokens.TextPrimary,

        background = OptimusTokens.Background,
        onBackground = OptimusTokens.TextPrimary,

        surface = OptimusTokens.Surface,
        onSurface = OptimusTokens.TextPrimary,
        surfaceVariant = OptimusTokens.SurfaceRaised,
        onSurfaceVariant = OptimusTokens.TextSecondary,

        outline = OptimusTokens.Border,
        outlineVariant = OptimusTokens.SurfaceRaised,

        error = OptimusTokens.Error,
        onError = Color.White
    )

    CompositionLocalProvider(LocalOptimusTokens provides OptimusTokens) {
        MaterialTheme(
            colorScheme = darkColors,
            content = content
        )
    }
}
