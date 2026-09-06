package com.optimus.voiceos.feature.projects

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ProjectsViewModelTest {

    @Test
    fun initialProjectsContainOptimusAndWorkerMetadata() {
        val vm = ProjectsViewModel()
        val state = vm.uiState

        assertTrue("Projects should not be empty", state.projects.isNotEmpty())
        val optimus = state.projects.firstOrNull { it.id == "proj-1" }
        assertNotNull("Optimus Voice OS project must be present", optimus)
        assertEquals("Optimus Voice OS", optimus?.name)
        assertEquals("claude", optimus?.destinationId)
        assertTrue("Optimus must have active workers", optimus?.activeWorkers?.isNotEmpty() == true)
        assertTrue("Optimus must have tasks", optimus?.tasks?.isNotEmpty() == true)
    }

    @Test
    fun selectProjectAndTaskUpdatesState() {
        val vm = ProjectsViewModel()
        val project = vm.uiState.projects.first()

        vm.selectProject(project)
        assertEquals(project.id, vm.uiState.selectedProject?.id)
        assertNull(vm.uiState.selectedTask)

        val task = project.tasks.first()
        vm.selectTask(task)
        assertEquals(task.id, vm.uiState.selectedTask?.id)

        vm.selectTask(null)
        assertNull(vm.uiState.selectedTask)

        vm.selectProject(null)
        assertNull(vm.uiState.selectedProject)
    }

    @Test
    fun fullScreenReaderControlsFunctionCorrectly() {
        val vm = ProjectsViewModel()

        assertFalse(vm.uiState.reader.isOpen)

        vm.openReader("Architecture Review", "Detailed plan content...")
        assertTrue(vm.uiState.reader.isOpen)
        assertEquals("Architecture Review", vm.uiState.reader.title)
        assertEquals("Detailed plan content...", vm.uiState.reader.content)

        vm.closeReader()
        assertFalse(vm.uiState.reader.isOpen)
    }

    @Test
    fun threadSheetVisibilityTogglesCorrectly() {
        val vm = ProjectsViewModel()

        assertFalse(vm.uiState.showThreadSheet)
        vm.setThreadSheetVisible(true)
        assertTrue(vm.uiState.showThreadSheet)
        vm.setThreadSheetVisible(false)
        assertFalse(vm.uiState.showThreadSheet)
    }
}
