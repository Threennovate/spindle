package com.spindletrack.android.ui

import com.spindletrack.android.data.*
import org.junit.Assert.*
import org.junit.Test

/** The same cases as the Apple apps' SplitMathTests, so every app splits time alike. */
class SplitTest {
    @Test fun `divides time to add up exactly as the server does`() {
        assertEquals(listOf(2160L, 1440L), SplitMath.allocate(3600, listOf(60, 40)))
        assertEquals(listOf(3L, 3L, 4L), SplitMath.allocate(10, listOf(33, 33, 34)))
        assertEquals(listOf(1L, 0L), SplitMath.allocate(1, listOf(50, 50)))
    }
    @Test fun `splits evenly, the first parts taking the rest`() {
        assertEquals(listOf(50, 50), SplitMath.equal(2))
        assertEquals(listOf(34, 33, 33), SplitMath.equal(3))
    }
    @Test fun `moving a divider changes only the two parts beside it`() {
        assertEquals(listOf(34, 46, 20), SplitMath.moveBoundary(listOf(34, 33, 33), 1, 80.0))
        assertEquals(listOf(60, 40), SplitMath.moveBoundary(listOf(50, 50), 0, 62.0, step = 5))
        assertEquals(listOf(99, 1), SplitMath.moveBoundary(listOf(50, 50), 0, 120.0))
    }
    @Test fun `a share set for one part makes room in the others in proportion`() {
        assertEquals(listOf(60, 24, 16), SplitMath.setting(listOf(50, 30, 20), 0, 60))
        assertEquals(listOf(1, 1, 98), SplitMath.setting(listOf(50, 30, 20), 2, 100))
    }
    @Test fun `adding and removing parts keeps the total`() {
        assertEquals(listOf(47, 20, 33), SplitMath.adding(listOf(70, 30)))
        assertEquals(listOf(60, 40), SplitMath.removing(listOf(50, 30, 20), 0))
        assertEquals(listOf(100), SplitMath.removing(listOf(60, 40), 1))
    }
    @Test fun `the largest part is the entry's project, the first on a tie`() {
        val parts = listOf(TimeShareResponse("a", null, true, 40), TimeShareResponse("b", "t", false, 40), TimeShareResponse("c", null, false, 20))
        val draft = EntryDraft(projectId = "x").splitting(parts)
        assertEquals("a", draft.projectId)
        assertEquals(true, draft.isBillable)
        assertEquals(parts.map { it.toRequest() }, draft.shares)
        val one = EntryDraft(projectId = "x").splitting(listOf(parts[1]))
        assertEquals("b", one.projectId)
        assertEquals("t", one.taskId)
        // No shares at all, not null, so the server ends the split even on the same project.
        assertEquals(emptyList<TimeShareRequest>(), one.shares)
        assertEquals("x", EntryDraft(projectId = "x").splitting(null).projectId)
    }
    @Test fun `each part is checked for what the workspace requires`() {
        val state = AppState(settings = WorkspaceSettingsResponse(requireProject = true))
        assertEquals(mapOf(1 to "Choose a project."), state.missingInParts(listOf(TimeShareResponse("a", percent = 70), TimeShareResponse(percent = 30))))
    }
    @Test fun `describing each part gives the description to the largest and starts the others empty`() {
        val parts = listOf(TimeShareResponse("a", percent = 30), TimeShareResponse("b", percent = 40), TimeShareResponse("c", percent = 30))
        val described = describingEach(parts, " Fix login ")
        assertEquals(listOf("", "Fix login", ""), described.map { it.description })
        assertTrue(describesEach(described))
        assertFalse(describesEach(parts))
        assertFalse(describesEach(listOf(described[1])))
        // On a tie the first of the largest gets it, as the server and the web choose the entry's project.
        assertEquals(listOf("x", ""), describingEach(parts.take(2).map { it.copy(percent = 50) }, "x").map { it.description })
    }
    @Test fun `one description for all joins the parts' own`() {
        val parts = describingEach(listOf(TimeShareResponse("a", percent = 60), TimeShareResponse("b", percent = 40)), "Fix login")
            .let { listOf(it[0], it[1].copy(description = " Review PR ")) }
        val (shared, description) = sharingOneDescription(parts)
        assertEquals("Fix login; Review PR", description)
        assertTrue(shared.all { it.description == null })
        assertEquals("Fix login", sharingOneDescription(listOf(parts[0], TimeShareResponse("c", percent = 0, description = ""))).second)
    }
    @Test fun `a required description is required of each part described on its own`() {
        val state = AppState(settings = WorkspaceSettingsResponse(requireDescription = true, requireTag = true))
        val parts = listOf(TimeShareResponse("p1", "t0", percent = 60, description = "Design review"), TimeShareResponse("p2", percent = 40, description = " "))
        val draft = EntryDraft(description = "", tagIds = listOf("tag"), describeParts = true).splitting(parts)
        assertEquals(mapOf(1 to DescriptionRequired), state.missingInParts(parts))
        assertEquals(mapOf("description" to DescriptionRequired), state.missingDetails(draft))
        assertTrue(state.missingDetails(draft.splitting(parts.map { it.copy(description = "Done") })).isEmpty())
        // Parts sharing one description are not asked for their own; the entry's is.
        val shared = sharingOneDescription(parts).first
        assertTrue(state.missingInParts(shared).isEmpty())
        assertEquals(setOf("description"), state.missingDetails(draft.splitting(shared)).keys)
    }
    @Test fun `parts keep their descriptions as the server keeps them`() {
        val entry = TimeEntryResponse(
            description = "Fix login; Review PR", projectId = "a",
            shares = listOf(TimeShareResponse("a", percent = 60, description = "Fix login"), TimeShareResponse("b", percent = 40, description = "Review PR")),
        )
        // Changes made outside the editor (times, tags) keep the parts' own while the description stays.
        assertEquals(entry.shares, shownShares(entry.draft().copy(tagIds = listOf("tag")), entry))
        // A changed description is one description for all again.
        assertEquals(listOf(null, null), shownShares(entry.draft().copy(description = "Both"), entry).map { it.description })
        // The editor says which.
        val parts = entry.parts.map { it.toRequest() }
        assertEquals(listOf(null, null), shownShares(EntryDraft(shares = parts, describeParts = false), entry).map { it.description })
        assertEquals(listOf("Fix login", ""), shownShares(EntryDraft(shares = listOf(parts[0], parts[1].copy(description = null)), describeParts = true), entry).map { it.description })
        assertEquals(listOf<String?>(null), shownShares(EntryDraft(shares = listOf(parts[0])), entry).map { it.description })
    }
    @Test fun `a part can't take what another part has`() {
        val taken = takenIn(listOf(TimeShareResponse("a", percent = 50), TimeShareResponse("b", "t", percent = 50)), except = 0)
        assertTrue(taken("b", "t"))
        assertFalse(taken("a", null))
        assertFalse(taken("b", null))
    }
}
