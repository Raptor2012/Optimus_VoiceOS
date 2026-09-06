package com.optimus.voiceos.core.ui

import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.waitForUpOrCancellation
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import kotlin.coroutines.cancellation.CancellationException

/**
 * Robust voice interaction gesture modifier.
 *
 * Requirements:
 * 1) Pointer-down starts temporary utterance capture.
 * 2) Release within 300ms starts or retains continuous conversation mode.
 * 3) Release after 300ms ends that held utterance.
 * 6) Gesture cancellation discards temporary utterance without ending active conversation.
 * 8) Recording-state update or recomposition cannot restart gesture ownership (keyed on Unit).
 * 9) Each gesture issues one start and one applicable end/cancel event.
 */
@Composable
fun Modifier.voiceGesture(
    enabled: Boolean = true,
    onGestureStart: () -> Unit,
    onGestureEnd: (elapsedMs: Long) -> Unit,
    onGestureCancel: () -> Unit
): Modifier {
    val currentEnabled by rememberUpdatedState(enabled)
    val currentOnStart by rememberUpdatedState(onGestureStart)
    val currentOnEnd by rememberUpdatedState(onGestureEnd)
    val currentOnCancel by rememberUpdatedState(onGestureCancel)

    return this.pointerInput(Unit) {
        awaitEachGesture {
            val down = awaitFirstDown(requireUnconsumed = false)
            if (!currentEnabled) return@awaitEachGesture

            val downTime = System.currentTimeMillis()
            var gestureActive = false
            try {
                gestureActive = true
                currentOnStart()

                val upOrCancel = waitForUpOrCancellation()
                val elapsed = System.currentTimeMillis() - downTime
                if (upOrCancel != null) {
                    upOrCancel.consume()
                    gestureActive = false
                    currentOnEnd(elapsed)
                } else {
                    gestureActive = false
                    currentOnCancel()
                }
            } catch (c: CancellationException) {
                if (gestureActive) {
                    gestureActive = false
                    currentOnCancel()
                }
                throw c
            }
        }
    }
}
