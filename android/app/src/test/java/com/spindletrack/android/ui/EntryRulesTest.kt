package com.spindletrack.android.ui

import com.spindletrack.android.data.*
import org.junit.Assert.*
import org.junit.Test
import java.time.*

class EntryRulesTest {
    @Test fun `a closed task does not make the workspace require a task`() {
        val project = ProjectResponse(id = "project", tasks = listOf(TaskResponse(id = "task", isDone = true)))
        val state = AppState(projects = listOf(project), settings = WorkspaceSettingsResponse(requireTask = true))
        assertFalse(missingDetails(TimeEntryResponse(projectId = "project"), state))
        assertTrue(missingDetails(TimeEntryResponse(projectId = "project"), state.copy(projects = listOf(project.copy(tasks = listOf(TaskResponse(id = "task", isDone = false)))))))
    }
    @Test fun `required projects are checked on each split part`() {
        val state = AppState(settings = WorkspaceSettingsResponse(requireProject = true))
        val entry = TimeEntryResponse(projectId = "a", shares = listOf(TimeShareResponse(projectId = "a", percent = 70), TimeShareResponse(percent = 30)))
        assertTrue(missingDetails(entry, state))
    }
    @Test fun `calendar clips crossing midnight and accounts for daylight saving`() {
        val zone = ZoneId.of("Europe/Zagreb")
        val day = LocalDate.of(2026, 10, 25)
        val entry = TimeEntryResponse(start = day.minusDays(1).atTime(23, 0).atZone(zone).toInstant().toString(), stop = day.plusDays(1).atTime(1, 0).atZone(zone).toInstant().toString())
        assertEquals(25 * 3600L, daySeconds(entry, day, zone))
        assertEquals(3600L, daySeconds(entry, day.minusDays(1), zone))
        assertEquals(3600L, daySeconds(entry, day.plusDays(1), zone))
    }
    @Test fun `workspace admins can edit locked days`() {
        val state = AppState(workspaceId = "workspace", me = MeResponse(workspaces = listOf(WorkspaceSummary(id = "workspace", role = "admin"))), settings = WorkspaceSettingsResponse(lockedBefore = "2026-10-01"))
        val entry = TimeEntryResponse(start = "2026-09-01T10:00:00Z")
        assertFalse(entryLocked(entry, state))
        assertTrue(entryLocked(entry, state.copy(me = state.me!!.copy(workspaces = listOf(WorkspaceSummary(id = "workspace", role = "member"))))))
    }
}
