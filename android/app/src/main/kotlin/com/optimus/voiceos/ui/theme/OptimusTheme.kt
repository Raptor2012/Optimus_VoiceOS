package com.optimus.voiceos.ui.theme

import android.content.Context
import android.os.Build
import android.provider.Settings
import android.view.accessibility.AccessibilityManager
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/**
 * Optimus Voice OS Design Tokens.
 *
 * Refined dark palette and shared spatial/motion constants matching
 * the Windows companion shell.
 */
object OptimusColors {
    // CURRENT → NEW Palette:
    // Background: #0B111B → #101114
    val Background = Color(0xFF101114)

    // Main surfaces: #111C2B → #181A1F; raised surfaces #1A293C → #20232A
    val Surface = Color(0xFF181A1F)
    val SurfaceRaised = Color(0xFF20232A)

    // Primary text: #EAF2FA → #F1F2F4
    val TextPrimary = Color(0xFFF1F2F4)

    // Secondary text: #A7B8CD → #A3A9B5
    val TextSecondary = Color(0xFFA3A9B5)

    // Accent: #7DD3FC → #8C9EFF (periwinkle instead of sky blue)
    val Accent = Color(0xFF8C9EFF)
    val AccentMuted = Color(0xFF7080D9)
    val OnAccent = Color(0xFF101114) // Dark contrast text on periwinkle

    // Success: #30D158 → keep but mute slightly
    val Success = Color(0xFF34C759)
    val SurfaceSuccess = Color(0xFF162419)

    // Error: #FF5555 → keep but mute slightly
    val Error = Color(0xFFE05353)
    val SurfaceError = Color(0xFF2D1818)

    // Border: #34465C → thin borders with subtle opacity
    val Border = Color(0x26FFFFFF)       // Subtle opacity border (~15%)
    val BorderSolid = Color(0xFF282C34)  // Thin border solid fallback

    // Warning
    val Warning = Color(0xFFF59E0B)
    val SurfaceWarning = Color(0xFF2A2415)
}

/**
 * Spacing tokens: 4px base; 8, 12, 16, 24, 32px scale.
 */
object OptimusSpacing {
    val Base: Dp = 4.dp
    val Scale4: Dp = 4.dp
    val Scale8: Dp = 8.dp
    val Scale12: Dp = 12.dp
    val Scale16: Dp = 16.dp
    val Scale24: Dp = 24.dp
    val Scale32: Dp = 32.dp

    val Xs: Dp = 4.dp
    val Sm: Dp = 8.dp
    val Md: Dp = 12.dp
    val Lg: Dp = 16.dp
    val Xl: Dp = 24.dp
    val Xxl: Dp = 32.dp
}

/**
 * Corner tokens: 12px controls, 16px cards, fully rounded companion.
 */
object OptimusCorners {
    val Control: Dp = 12.dp
    val Card: Dp = 16.dp
    val Companion: Dp = 24.dp

    val ControlShape = RoundedCornerShape(Control)
    val CardShape = RoundedCornerShape(Card)
    val CompanionShape = RoundedCornerShape(Companion)
}

/**
 * Motion tokens: 160-240ms controls, 240-320ms panel transitions.
 */
object OptimusMotion {
    const val ControlDurationMs = 200
    const val PanelDurationMs = 280

    /**
     * Checks accessibility animation settings via AccessibilityManager and system animation scales.
     */
    fun isReducedMotion(context: Context): Boolean {
        val am = context.getSystemService(Context.ACCESSIBILITY_SERVICE) as? AccessibilityManager
        val animationScale = try {
            Settings.Global.getFloat(
                context.contentResolver,
                Settings.Global.ANIMATOR_DURATION_SCALE,
                1.0f
            )
        } catch (_: Exception) {
            1.0f
        }
        val transitionScale = try {
            Settings.Global.getFloat(
                context.contentResolver,
                Settings.Global.TRANSITION_ANIMATION_SCALE,
                1.0f
            )
        } catch (_: Exception) {
            1.0f
        }
        return animationScale == 0f || transitionScale == 0f || (am != null && am.isEnabled && animationScale == 0f)
    }
}

/**
 * Returns whether reduced motion is requested on the device.
 */
@Composable
@ReadOnlyComposable
fun isReducedMotion(): Boolean {
    val context = LocalContext.current
    return OptimusMotion.isReducedMotion(context)
}

/**
 * Returns control animation duration in ms (0ms when reduced motion is enabled).
 */
@Composable
@ReadOnlyComposable
fun controlAnimationDurationMs(): Int {
    return if (isReducedMotion()) 0 else OptimusMotion.ControlDurationMs
}

/**
 * Returns panel transition duration in ms (0ms when reduced motion is enabled).
 */
@Composable
@ReadOnlyComposable
fun panelTransitionDurationMs(): Int {
    return if (isReducedMotion()) 0 else OptimusMotion.PanelDurationMs
}

val OptimusDarkColorScheme: ColorScheme = darkColorScheme(
    primary = OptimusColors.Accent,
    onPrimary = OptimusColors.OnAccent,
    primaryContainer = OptimusColors.SurfaceRaised,
    onPrimaryContainer = OptimusColors.TextPrimary,
    secondary = OptimusColors.TextSecondary,
    onSecondary = OptimusColors.Background,
    secondaryContainer = OptimusColors.SurfaceRaised,
    onSecondaryContainer = OptimusColors.TextPrimary,
    background = OptimusColors.Background,
    onBackground = OptimusColors.TextPrimary,
    surface = OptimusColors.Surface,
    onSurface = OptimusColors.TextPrimary,
    surfaceVariant = OptimusColors.SurfaceRaised,
    onSurfaceVariant = OptimusColors.TextSecondary,
    outline = OptimusColors.BorderSolid,
    outlineVariant = OptimusColors.Border,
    error = OptimusColors.Error,
    onError = OptimusColors.TextPrimary
)

val OptimusShapes: Shapes = Shapes(
    small = OptimusCorners.ControlShape,
    medium = OptimusCorners.CardShape,
    large = OptimusCorners.CompanionShape
)

@Composable
fun OptimusTheme(
    content: @Composable () -> Unit
) {
    MaterialTheme(
        colorScheme = OptimusDarkColorScheme,
        shapes = OptimusShapes,
        content = content
    )
}
