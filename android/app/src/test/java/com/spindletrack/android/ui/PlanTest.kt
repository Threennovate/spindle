package com.spindletrack.android.ui

import com.spindletrack.android.data.*
import org.junit.Assert.*
import org.junit.Test
import java.time.Instant
import java.time.ZoneId

/** The same cases as the iPhone's `PlanTests` and the web's `describePlan` tests, without the buttons for paying. */
class PlanTest {
    private val time = AccountTime(ZoneId.of("Europe/Zagreb"), hour12 = false, durationFormat = "hms", weekStart = 1)
    private val now = Instant.parse("2026-10-11T10:00:00Z")
    /** Midnight in Zagreb at the start of 21 October, so the trial's last day is 20 October. */
    private val trialEnd = "2026-10-20T22:00:00Z"

    private fun subscription(status: String, access: String = "full", trialEndsAt: String? = null, paidUntil: String? = null, hasPlan: Boolean = false, cancelsAtPeriodEnd: Boolean = false) =
        SubscriptionResponse(status = status, access = access, trialEndsAt = trialEndsAt, paidUntil = paidUntil, seats = 3, checkoutAvailable = true, hasPlan = hasPlan, interval = "month", cancelsAtPeriodEnd = cancelsAtPeriodEnd)

    private fun describe(subscription: SubscriptionResponse) = PlanDescription.of(subscription, "Acme", time, now)

    @Test fun `counts the days left of a trial`() {
        val plan = describe(subscription("trialing", trialEndsAt = trialEnd))
        assertEquals("Free trial", plan.badge)
        assertEquals("10 days left", plan.title)
        assertTrue(plan.text.contains("20 October 2026"))
    }

    @Test fun `says when the first charge comes for a plan chosen during the trial`() {
        val plan = describe(subscription("trialing", trialEndsAt = trialEnd, hasPlan = true))
        assertEquals("Plan chosen", plan.badge)
        assertEquals("Starts after 20 October 2026", plan.title)
    }

    @Test fun `says an ended trial is read-only`() {
        val plan = describe(subscription("trialing", access = "readOnly", trialEndsAt = trialEnd))
        assertEquals(PlanDescription.Tone.Bad, plan.tone)
        assertEquals("The trial has ended", plan.title)
        assertTrue(plan.text.contains("Acme is read-only"))
    }

    @Test fun `describes paid, canceled and comped`() {
        assertEquals("Paid until 20 October 2026", describe(subscription("active", paidUntil = trialEnd)).title)
        assertEquals("Works until 20 October 2026", describe(subscription("active", paidUntil = trialEnd, cancelsAtPeriodEnd = true)).title)
        assertEquals("The plan has ended", describe(subscription("canceled", access = "readOnly", paidUntil = trialEnd)).title)
        assertEquals("Free", describe(subscription("comped")).badge)
    }

    @Test fun `counts a part of a day as one`() {
        assertEquals(1, PlanDescription.daysLeft(now.plusSeconds(3600), now))
        assertEquals(0, PlanDescription.daysLeft(now.minusSeconds(3600), now))
        assertEquals("Last day", PlanDescription.describeDaysLeft(1))
    }

    @Test fun `only the current workspace's organization makes the app read-only`() {
        val me = MeResponse(
            workspaces = listOf(WorkspaceSummary(id = "one", organizationId = "a"), WorkspaceSummary(id = "two", organizationId = "b")),
            organizations = listOf(OrganizationSummary(id = "a", name = "Acme", role = "member", access = "readOnly"), OrganizationSummary(id = "b", name = "Beta", role = "owner", access = "full", trialEndsAt = trialEnd)),
        )
        assertEquals("Acme", AppState(me = me, workspaceId = "one").readOnlyOrganization?.name)
        assertNull(AppState(me = me, workspaceId = "two").readOnlyOrganization)
        assertEquals("Ended", me.organizations[0].planSummary(now))
        assertEquals("10 days left", me.organizations[1].planSummary(now))
    }

    @Test fun `only owners and admins on the hosted service see the plan`() {
        val me = MeResponse(workspaces = listOf(WorkspaceSummary(id = "one", organizationId = "a")), organizations = listOf(OrganizationSummary(id = "a", role = "admin")))
        assertNotNull(AppState(me = me, workspaceId = "one", serverInfo = ServerResponse(hosted = true)).planOrganization)
        assertNull(AppState(me = me, workspaceId = "one", serverInfo = ServerResponse(hosted = false)).planOrganization)
        assertNull(AppState(me = me.copy(organizations = listOf(OrganizationSummary(id = "a", role = "member"))), workspaceId = "one", serverInfo = ServerResponse(hosted = true)).planOrganization)
    }

    @Test fun `an invited member creates a workspace of their own, on its own trial`() {
        val me = MeResponse(
            workspaces = listOf(WorkspaceSummary(id = "one", organizationId = "a")),
            organizations = listOf(OrganizationSummary(id = "a", name = "Firma", role = "member")),
            newWorkspace = NewWorkspaceResponse(allowed = true, organizationId = null, trialDays = 90),
        )
        val state = AppState(me = me, workspaceId = "one")
        assertTrue(state.canCreateWorkspaces)
        assertEquals("A space of your own, apart from Firma, with its own projects, clients, tags and team. It's not on Firma's plan: it starts its own 90-day free trial.", state.newWorkspaceDescription)
        assertFalse(AppState(me = me.copy(newWorkspace = null), workspaceId = "one").canCreateWorkspaces)
    }
}
