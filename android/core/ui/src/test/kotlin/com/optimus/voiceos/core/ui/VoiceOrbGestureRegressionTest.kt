package com.optimus.voiceos.core.ui

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Regression test for the Android recording gesture bug identified in Baseline Audit (Slice 1).
 *
 * BUG DESCRIPTION:
 * In [VoiceOrb.kt], the gesture detector is attached with:
 * `Modifier.pointerInput(isCapturing, onStartCapture, onStopCapture) { detectTapGestures(onPress = { ... }) }`
 *
 * When the user touches down:
 * 1. `onPress` begins executing while `isCapturing == false`.
 * 2. It calls `onStartCapture()`.
 * 3. The state updates `isCapturing = true`.
 * 4. In Compose, changing the key parameter to `pointerInput(isCapturing)` cancels the active
 *    `PointerInputScope` coroutine where `onPress` is suspended in `tryAwaitRelease()`.
 * 5. Coroutine cancellation causes `tryAwaitRelease()` to abort and return `false` (or throw).
 * 6. The `onPress` logic contains:
 *    ```kotlin
 *    val released = tryAwaitRelease()
 *    if (released && elapsed > 300) { onStopCapture() }
 *    else if (!released) { onStopCapture() }
 *    ```
 *    Since `released` is false (due to key invalidation cancellation, not a user drag-off),
 *    it immediately executes `onStopCapture()`.
 * 7. Result: The recording is immediately stopped within milliseconds of touch-down.
 */
class VoiceOrbGestureRegressionTest {

    /**
     * Models the buggy gesture handler from VoiceOrb.kt lines 133-155.
     */
    private class BuggyGestureSimulator(
        private val onStartCapture: () -> Unit,
        private val onStopCapture: () -> Unit
    ) {
        var isCapturing: Boolean = false
        private var activeJob: Job? = null
        val events = mutableListOf<String>()

        fun onTouchDown(scope: CoroutineScope) {
            // pointerInput(key = isCapturing) creates a coroutine job keyed on isCapturing
            val keyAtStart = isCapturing
            activeJob = scope.launch {
                val downTime = System.currentTimeMillis()
                if (keyAtStart) {
                    events.add("stop_on_already_capturing")
                    onStopCapture()
                } else {
                    events.add("start_capture")
                    onStartCapture()

                    // Simulating tryAwaitRelease() which suspends until pointer release.
                    // If the coroutine is cancelled (because isCapturing key changed),
                    // tryAwaitRelease catches CancellationException and returns false.
                    val released = try {
                        CompletableDeferred<Boolean>().await()
                    } catch (e: CancellationException) {
                        false
                    }

                    val elapsed = System.currentTimeMillis() - downTime
                    if (released && elapsed > 300) {
                        events.add("stop_on_hold_release")
                        onStopCapture()
                    } else if (!released) {
                        // BUG: Key invalidation triggers this branch immediately!
                        events.add("stop_on_cancellation")
                        onStopCapture()
                    }
                }
            }
        }

        fun onStateUpdated(newCapturing: Boolean) {
            val keyChanged = this.isCapturing != newCapturing
            this.isCapturing = newCapturing
            if (keyChanged) {
                // In Compose, changing pointerInput key cancels the coroutine immediately
                activeJob?.cancel(CancellationException("pointerInput key changed: isCapturing=$newCapturing"))
            }
        }
    }

    @Test
    fun reproduceGestureBug_startingRecordingChangesKeyAndImmediatelyStopsCapture() = runBlocking {
        var captureActive = false
        val calls = mutableListOf<String>()

        val simulator = BuggyGestureSimulator(
            onStartCapture = {
                captureActive = true
                calls.add("START")
            },
            onStopCapture = {
                captureActive = false
                calls.add("STOP")
            }
        )

        // User touches down on VoiceOrb
        val testScope = CoroutineScope(Dispatchers.Unconfined)
        simulator.onTouchDown(testScope)

        // 1. start_capture was called
        assertEquals(listOf("START"), calls)
        assertTrue(captureActive)

        // 2. The capture state change propagates back to Compose as a recomposition with isCapturing = true.
        // Because pointerInput is keyed on `isCapturing`, this cancels the press handler!
        simulator.onStateUpdated(newCapturing = true)

        // 3. REPRODUCTION OF THE BUG:
        // The cancellation triggered `else if (!released) onStopCapture()`!
        // So STOP was called immediately without the user ever releasing their finger!
        assertEquals(listOf("START", "STOP"), calls)
        assertFalse("Recording must have been prematurely stopped by the gesture bug", captureActive)
        assertEquals(listOf("start_capture", "stop_on_cancellation"), simulator.events)
    }

    /**
     * Verifies the required invariant for the Slice 2 fix:
     * Pointer input must NOT be keyed on dynamic capture state;
     * a press down should remain active throughout hold, and only stop when physically released.
     */
    @Test
    fun fixedGestureContract_pointerInputRetainsGestureThroughStateChanges() = runBlocking {
        var captureActive = false
        val calls = mutableListOf<String>()

        // Fixed simulator: pointerInput is keyed on Unit / stable key, not on isCapturing
        class FixedGestureSimulator(
            val onStart: () -> Unit,
            val onStop: () -> Unit
        ) {
            var activeJob: Job? = null
            val releaseDeferred = CompletableDeferred<Boolean>()

            fun onTouchDown(scope: CoroutineScope) {
                activeJob = scope.launch {
                    onStart()
                    // tryAwaitRelease suspends until real physical pointer release
                    val released = releaseDeferred.await()
                    if (released) {
                        onStop()
                    }
                }
            }

            fun onPhysicalRelease() {
                releaseDeferred.complete(true)
            }
        }

        val fixed = FixedGestureSimulator(
            onStart = {
                captureActive = true
                calls.add("START")
            },
            onStop = {
                captureActive = false
                calls.add("STOP")
            }
        )

        val testScope = CoroutineScope(Dispatchers.Unconfined)
        fixed.onTouchDown(testScope)

        assertEquals(listOf("START"), calls)
        assertTrue(captureActive)

        // State update happens, but the fixed handler is NOT cancelled
        captureActive = true // model external UI state update

        // The hold is still active!
        assertEquals("Still only START called", listOf("START"), calls)

        // User physically releases finger
        fixed.onPhysicalRelease()

        // Now STOP is called
        assertEquals(listOf("START", "STOP"), calls)
        assertFalse(captureActive)
    }
}
