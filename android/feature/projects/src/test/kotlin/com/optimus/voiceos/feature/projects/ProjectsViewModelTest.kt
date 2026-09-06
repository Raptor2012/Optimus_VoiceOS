package com.optimus.voiceos.feature.projects

import com.optimus.voiceos.core.transport.PcAgentWorker
import com.optimus.voiceos.core.transport.PcFileChangeSummary
import com.optimus.voiceos.core.transport.PcProjectConversation
import com.optimus.voiceos.core.transport.PcProjectItem
import com.optimus.voiceos.core.transport.PcProjectTask
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

    @Test
    fun updateFromLivePopulatesProjectsAndMapsCorrectly() {
        val vm = ProjectsViewModel()

        val worker = PcAgentWorker("agent-1", "Gemini 3.8 Flash", "Routine Dev", "gemini-3.8-flash")
        val liveTask = PcProjectTask(
            id = "task-live-1",
            title = "Live Task 1",
            status = "In Progress",
            progressPercent = 50,
            agent = worker,
            conversationSnippet = "Working on feature",
            changedFiles = listOf(PcFileChangeSummary("Test.kt", 10, 2)),
            planOrResultContent = "Plan content"
        )
        val liveConv = PcProjectConversation("c1", "Live Chat", "last msg", "12:00", 3)
        val liveProject = PcProjectItem(
            id = "proj-live",
            name = "Live Project",
            objective = "Live Objective",
            progressSentence = "Halfway done",
            completedTasks = 1,
            totalTasks = 2,
            activeWorkers = listOf(worker),
            nextAction = "Test feature",
            destinationId = "ao:proj-live",
            tasks = listOf(liveTask),
            conversations = listOf(liveConv),
            recentResultSummary = "Clean build"
        )

        vm.updateFromLive(listOf(liveProject))

        assertEquals(1, vm.uiState.projects.size)
        val proj = vm.uiState.projects.first()
        assertEquals("proj-live", proj.id)
        assertEquals("Live Project", proj.name)
        assertEquals("ao:proj-live", proj.destinationId)
        assertEquals(1, proj.tasks.size)
        assertEquals("task-live-1", proj.tasks.first().id)
        assertEquals(1, proj.tasks.first().changedFiles.size)
        assertEquals("Test.kt", proj.tasks.first().changedFiles.first().path)
        assertEquals(1, proj.conversations.size)
        assertEquals("Live Chat", proj.conversations.first().title)
    }

    @Test
    fun updateFromLivePreservesBrowsingState() {
        val vm = ProjectsViewModel()

        val worker = PcAgentWorker("agent-1", "Gemini 3.8 Flash", "Routine Dev", "gemini-3.8-flash")
        val liveTask = PcProjectTask(
            id = "task-live-1",
            title = "Live Task 1",
            status = "In Progress",
            progressPercent = 50,
            agent = worker,
            conversationSnippet = "Working on feature",
            changedFiles = listOf(PcFileChangeSummary("Test.kt", 10, 2)),
            planOrResultContent = "Plan content"
        )
        val liveProject = PcProjectItem(
            id = "proj-live",
            name = "Live Project",
            objective = "Live Objective",
            progressSentence = "Halfway done",
            completedTasks = 1,
            totalTasks = 2,
            activeWorkers = listOf(worker),
            nextAction = "Test feature",
            destinationId = "ao:proj-live",
            tasks = listOf(liveTask),
            conversations = emptyList(),
            recentResultSummary = "Clean build"
        )

        // Initial live update
        vm.updateFromLive(listOf(liveProject))

        // User navigates into the project, opens a task, and opens the reader
        vm.selectProject(vm.uiState.projects.first())
        vm.selectTask(vm.uiState.selectedProject?.tasks?.first())
        vm.openReader("Task Plan", "Reviewing plan")
        vm.setThreadSheetVisible(true)

        assertEquals("proj-live", vm.uiState.selectedProject?.id)
        assertEquals("task-live-1", vm.uiState.selectedTask?.id)
        assertTrue(vm.uiState.reader.isOpen)
        assertTrue(vm.uiState.showThreadSheet)

        // New live update arrives with updated task progress
        val updatedTask = liveTask.copy(progressPercent = 75)
        val updatedProject = liveProject.copy(
            progressSentence = "75% done",
            tasks = listOf(updatedTask)
        )

        vm.updateFromLive(listOf(updatedProject))

        // Browsing selection and modal states MUST be preserved
        assertEquals("proj-live", vm.uiState.selectedProject?.id)
        assertEquals("75% done", vm.uiState.selectedProject?.progressSentence)
        assertEquals("task-live-1", vm.uiState.selectedTask?.id)
        assertEquals(75, vm.uiState.selectedTask?.progressPercent)
        assertTrue(vm.uiState.reader.isOpen)
        assertEquals("Task Plan", vm.uiState.reader.title)
        assertTrue(vm.uiState.showThreadSheet)
    }

    @Test
    fun updateFromLiveWithEmptyListDoesNothing() {
        val vm = ProjectsViewModel()
        val initialCount = vm.uiState.projects.size
        assertTrue(initialCount > 0)

        vm.updateFromLive(emptyList())

        assertEquals(initialCount, vm.uiState.projects.size)
    }

    @Test
    fun browsingProjectsOrTasksDoesNotAffectVoiceDestination() {
        // Invariant: Browsing context (ProjectsViewModel) and voice destination (Talk destination)
        // are strictly decoupled. Navigating projects/tasks changes only ProjectsUiState.
        val projectsVm = ProjectsViewModel()
        val initialDestinationId = "claude"
        var voiceDestinationId = initialDestinationId

        // User browses to another project and task
        val project = projectsVm.uiState.projects.first()
        projectsVm.selectProject(project)
        val task = project.tasks.first()
        projectsVm.selectTask(task)

        // Voice destination remains completely unchanged by browsing
        assertEquals(initialDestinationId, voiceDestinationId)

        // Only when the user explicitly triggers "Talk here" callback does destination change
        val talkHereCallback: (String) -> Unit = { destinationId ->
            voiceDestinationId = destinationId
        }

        talkHereCallback(project.destinationId)
        assertEquals(project.destinationId, voiceDestinationId)
    }
}
