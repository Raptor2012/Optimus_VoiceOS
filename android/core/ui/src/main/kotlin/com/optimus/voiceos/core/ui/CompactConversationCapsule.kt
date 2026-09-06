package com.optimus.voiceos.core.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.optimus.voiceos.ui.theme.OptimusTokens

/**
 * Floating compact active-conversation capsule visible when navigating outside the Talk tab
 * (e.g. on Projects or Updates).
 *
 * Keeps active voice context reachable in one tap, showing the locked destination, current status,
 * and quick-talk controls.
 */
@Composable
fun CompactConversationCapsule(
    destinationName: String,
    statusText: String,
    orbState: VoiceOrbState,
    isCapturing: Boolean,
    onCapsuleClick: () -> Unit,
    onQuickMicClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    Surface(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 6.dp)
            .shadow(elevation = 10.dp, shape = OptimusTokens.CornerCapsule)
            .clip(OptimusTokens.CornerCapsule)
            .border(width = 1.dp, color = OptimusTokens.Border, shape = OptimusTokens.CornerCapsule)
            .clickable(onClick = onCapsuleClick),
        color = OptimusTokens.SurfaceRaised,
        shape = OptimusTokens.CornerCapsule
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = OptimusTokens.MinTouchTarget)
                .padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp)
        ) {
            // Mini Voice Orb
            VoiceOrb(
                state = orbState,
                size = 34.dp,
                onClick = onCapsuleClick
            )

            // Destination & Status information
            Column(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.Center
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(4.dp)
                ) {
                    Icon(
                        imageVector = Icons.Default.Lock,
                        contentDescription = "Voice locked destination",
                        tint = OptimusTokens.Accent,
                        modifier = Modifier.size(11.dp)
                    )
                    Text(
                        text = destinationName.ifBlank { "Voice Destination Locked" },
                        fontSize = 13.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = OptimusTokens.TextPrimary,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }

                Text(
                    text = statusText.ifBlank { "Tap to open talk view" },
                    fontSize = 11.sp,
                    color = if (isCapturing) OptimusTokens.Listening else OptimusTokens.TextSecondary,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }

            // Quick capture / mic action button (minimum 48dp touch target)
            Box(
                modifier = Modifier
                    .size(OptimusTokens.MinTouchTarget)
                    .clip(CircleShape)
                    .background(
                        if (isCapturing) OptimusTokens.Listening.copy(alpha = 0.2f)
                        else OptimusTokens.SurfaceHighlight
                    ),
                contentAlignment = Alignment.Center
            ) {
                IconButton(
                    onClick = onQuickMicClick,
                    modifier = Modifier.size(OptimusTokens.MinTouchTarget)
                ) {
                    // Visual recording indicator / mic wave
                    Box(
                        modifier = Modifier
                            .size(12.dp)
                            .clip(CircleShape)
                            .background(
                                if (isCapturing) OptimusTokens.Listening
                                else OptimusTokens.Accent
                            )
                    )
                }
            }
        }
    }
}
