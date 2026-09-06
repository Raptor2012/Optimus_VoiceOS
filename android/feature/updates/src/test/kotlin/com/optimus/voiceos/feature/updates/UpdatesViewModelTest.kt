package com.optimus.voiceos.feature.updates

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class UpdatesViewModelTest {

    @Test
    fun initialUpdatesContainDecisionsDigestsAndCompletedWork() {
        val vm = UpdatesViewModel()
        val state = vm.uiState

        assertTrue("Pending decisions must be present", state.pendingDecisions.isNotEmpty())
        assertTrue("Progress digests must be present", state.progressDigests.isNotEmpty())
        assertTrue("Completed work must be present", state.completedWork.isNotEmpty())

        val decision = state.pendingDecisions.first()
        assertFalse("Initial decision should be unanswered", decision.isAnswered)
        assertNotNull("Source event ID must be present", decision.sourceEventId)
    }

    @Test
    fun resolveDecisionMarksDecisionAnswered() {
        val vm = UpdatesViewModel()
        val decision = vm.uiState.pendingDecisions.first()

        vm.resolveDecision(decision.id, "Confirm & Send")

        val updated = vm.uiState.pendingDecisions.first { it.id == decision.id }
        assertTrue(updated.isAnswered)
        assertEquals("Confirm & Send", updated.selectedResolution)
        assertNull(vm.uiState.activeDecisionForSheet)
    }

    @Test
    fun openAndCloseDecisionSheet() {
        val vm = UpdatesViewModel()
        val decision = vm.uiState.pendingDecisions.first()

        vm.openDecisionSheet(decision)
        assertEquals(decision.id, vm.uiState.activeDecisionForSheet?.id)

        vm.closeDecisionSheet()
        assertNull(vm.uiState.activeDecisionForSheet)
    }

    @Test
    fun sourceEventTracingPopulatesDetails() {
        val vm = UpdatesViewModel()

        assertNull(vm.uiState.selectedEvent)

        vm.viewSourceEvent(
            eventId = "EVT-8192",
            title = "Refactor Slice",
            description = "Android Navigation restructure event",
            agent = "Gemini",
            project = "Optimus Voice OS",
            dest = "antigravity"
        )

        assertNotNull(vm.uiState.selectedEvent)
        assertEquals("EVT-8192", vm.uiState.selectedEvent?.eventId)
        assertEquals("antigravity", vm.uiState.selectedEvent?.destinationId)

        vm.closeSourceEvent()
        assertNull(vm.uiState.selectedEvent)
    }
}
