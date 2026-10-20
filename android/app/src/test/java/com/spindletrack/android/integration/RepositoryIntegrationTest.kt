package com.spindletrack.android.integration

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.spindletrack.android.data.*
import kotlinx.coroutines.*
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.*
import okhttp3.mockwebserver.*
import org.junit.*
import org.junit.Assert.*
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant
import java.time.LocalDate
import java.util.concurrent.CopyOnWriteArrayList

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28], application = android.app.Application::class)
class RepositoryIntegrationTest {
    private val json = Json { encodeDefaults = true }
    private lateinit var server: MockWebServer
    private lateinit var context: Context
    private val vault = MemorySession()
    private lateinit var repo: SpindleRepository
    private val entries = linkedMapOf<String, TimeEntryResponse>()
    private val writes = CopyOnWriteArrayList<RecordedRequest>()
    private val reads = CopyOnWriteArrayList<RecordedRequest>()
    @Volatile private var unavailable = false
    @Volatile private var rejectStop = false
    @Volatile private var rejectCreate = false
    @Volatile private var loseNextCreate = false
    @Volatile private var refreshCount = 0
    @Volatile private var expired = false
    @Volatile private var revokeAccess = false
    @Volatile private var readOnly = false
    private val me = MeResponse(id = "account", timeZone = "Europe/Zagreb", defaultWorkspaceId = "one",
        workspaces = listOf(WorkspaceSummary(id = "one", organizationId = "org", role = "Owner"), WorkspaceSummary(id = "two", organizationId = "org", role = "Owner")),
        organizations = listOf(OrganizationSummary(id = "org", name = "Acme", role = "owner", access = "full")))

    @Before fun setUp() = runBlocking {
        context = ApplicationProvider.getApplicationContext()
        context.getSharedPreferences("configuration", Context.MODE_PRIVATE).edit().clear().commit()
        server = MockWebServer()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                val path = request.requestUrl!!.encodedPath
                if (unavailable) return MockResponse().setResponseCode(503).setBody("{\"detail\":\"Temporarily unavailable\"}")
                fun response(value: JsonElement) = MockResponse().setHeader("Content-Type", "application/json").setBody(value.toString())
                if (path == "/api/server") return response(json.encodeToJsonElement(ServerResponse(registrationOpen = true)))
                if (path == "/api/auth/login") return response(tokens(expired))
                if (path == "/api/auth/refresh") { refreshCount++; return response(tokens(false)) }
                if (path == "/api/auth/logout") return MockResponse().setResponseCode(204)
                if (revokeAccess && request.getHeader("Authorization") == "Bearer access") return MockResponse().setResponseCode(401)
                if (path == "/api/me") return response(json.encodeToJsonElement(if (readOnly) me.copy(organizations = me.organizations.map { it.copy(access = "readOnly") }) else me))
                if (path == "/api/me/time-entries/current") return response(entries.values.firstOrNull { it.stop == null }?.let { json.encodeToJsonElement(it) } ?: JsonNull)
                if (request.method == "GET") {
                    reads.add(request)
                    return when {
                        path.endsWith("/settings") -> response(json.encodeToJsonElement(WorkspaceSettingsResponse()))
                        path.endsWith("/time-entries") -> response(json.encodeToJsonElement(entries.values.filter { it.workspaceId == path.split('/')[3] }))
                        else -> response(JsonArray(emptyList()))
                    }
                }
                writes.add(request)
                if (readOnly) return MockResponse().setResponseCode(402).setBody("{\"title\":\"Payment required\"}")
                if (path.endsWith("/stop")) {
                    if (rejectStop) return MockResponse().setResponseCode(403).setBody("{\"detail\":\"This day is locked\"}")
                    val id = path.split('/')[5]
                    val stop = json.parseToJsonElement(request.body.readUtf8()).jsonObject.getValue("stop").jsonPrimitive.content
                    entries[id] = entries.getValue(id).let { it.copy(stop = stop, version = (it.version.toInt() + 1).toString()) }
                    return response(json.encodeToJsonElement(entries.getValue(id)))
                }
                if (request.method == "PUT") {
                    // As the server does: a write whose If-Match names another version is refused with the entry as it is.
                    val id = path.split('/')[5]
                    val stored = entries.getValue(id)
                    if (request.getHeader("If-Match") != "\"${stored.version}\"") return MockResponse().setResponseCode(412).setHeader("Content-Type", "application/problem+json")
                        .setBody(buildJsonObject { put("type", "urn:spindle:problem:entry-changed"); put("status", 412); put("current", json.encodeToJsonElement(stored)) }.toString())
                    val draft = json.decodeFromJsonElement<EntryDraft>(json.parseToJsonElement(request.body.readUtf8()))
                    entries[id] = stored.copy(description = draft.description, start = draft.start, stop = draft.stop, version = (stored.version.toInt() + 1).toString())
                    return response(json.encodeToJsonElement(entries.getValue(id)))
                }
                if (rejectCreate) return MockResponse().setResponseCode(422).setBody("{\"detail\":\"Description required\"}")
                val body = json.parseToJsonElement(request.body.readUtf8()).jsonObject
                val id = body.getValue("id").jsonPrimitive.content
                val draft = json.decodeFromJsonElement<EntryDraft>(JsonObject(body.filterKeys { it != "id" }))
                val entry = entries.getOrPut(id) { TimeEntryResponse(id = id, workspaceId = path.split('/')[3], description = draft.description, start = draft.start, stop = draft.stop, version = "1") }
                if (loseNextCreate) { loseNextCreate = false; return MockResponse().setSocketPolicy(SocketPolicy.DISCONNECT_AFTER_REQUEST) }
                return response(json.encodeToJsonElement(entry))
            }
        }
        server.start()
        repo = newRepository()
        repo.configureServer(server.url("/").toString())
        repo.signIn("test@example.test", "password")
    }
    @After fun tearDown() = runBlocking {
        unavailable = false
        repo.signOut(force = true)
        server.shutdown()
    }
    private fun newRepository() = SpindleRepository(context, sessionStore = vault, scheduleSync = {})
    private fun tokens(expired: Boolean) = json.encodeToJsonElement(AuthTokensResponse(accessToken = if (refreshCount == 0) "access" else "access-$refreshCount", refreshToken = "refresh", accessTokenExpiresAt = if (expired) Instant.EPOCH.toString() else Instant.now().plusSeconds(3600).toString(), refreshTokenExpiresAt = Instant.now().plusSeconds(86400).toString()))

    @Test fun unsentTimerIsPersistedAndRestoredBeforeNetworkRecovery() = runBlocking {
        unavailable = true
        repo.startTimer(EntryDraft(description = "Offline work"))
        val id = repo.state.value.running!!.id
        assertEquals(1, repo.state.value.pendingCount)
        repo = newRepository()
        repo.initialize()
        assertEquals(id, repo.state.value.running!!.id)
        assertEquals(1, repo.state.value.pendingCount)
        unavailable = false
        repo.refresh()
        assertEquals(0, repo.state.value.pendingCount)
        assertEquals(id, repo.state.value.running!!.id)
        assertEquals(1, entries.size)
    }

    @Test fun lostCreateResponseRetriesWithSameClientId() = runBlocking {
        loseNextCreate = true
        repo.startTimer(EntryDraft(description = "Acknowledged before disconnect"))
        val id = repo.state.value.running!!.id
        repo.refresh()
        assertEquals(0, repo.state.value.pendingCount)
        assertEquals(listOf(id), entries.keys.toList())
        assertTrue(writes.count { it.requestUrl!!.encodedPath.endsWith("/time-entries") } >= 2)
    }

    @Test fun currentTimerStopsInItsOwnWorkspaceAfterSwitch() = runBlocking {
        repo.startTimer(EntryDraft(description = "Workspace one"))
        val first = repo.state.value.running!!
        repo.switchWorkspace("two")
        assertEquals("one", repo.state.value.running!!.workspaceId)
        repo.stopTimer(at = Instant.parse(first.start).plusSeconds(60))
        assertTrue(writes.any { it.path == "/api/workspaces/one/time-entries/${first.id}/stop" })
        assertNull(repo.state.value.running)
        assertNotNull(entries.getValue(first.id).stop)
        repo.startTimer(EntryDraft(description = "Workspace two"))
        assertEquals("two", repo.state.value.running!!.workspaceId)
    }

    @Test fun rejectedStopKeepsServerTimerAndShowsRecoverableError() = runBlocking {
        repo.startTimer(EntryDraft(description = "Keep running"))
        val first = repo.state.value.running!!
        rejectStop = true
        val failure = runCatching { repo.stopTimer(at = Instant.parse(first.start).plusSeconds(60)) }.exceptionOrNull()
        assertTrue(failure is ApiException)
        assertEquals(first.id, repo.state.value.running!!.id)
        assertNull(repo.state.value.running!!.stop)
        assertEquals(1, repo.state.value.failedChanges.size)
        assertTrue(repo.state.value.error!!.contains("locked"))
    }

    @Test fun readOnlyRejectionSaysSoAndReloadsTheOrganization() = runBlocking {
        readOnly = true
        val failure = runCatching { repo.startTimer(EntryDraft(description = "After the trial")) }.exceptionOrNull()
        assertEquals(ReadOnlyMessage, failure?.message)
        assertEquals(ReadOnlyMessage, repo.state.value.error)
        assertEquals("readOnly", repo.state.value.me!!.organizations.single().access)
        assertNull(repo.state.value.running)
    }

    @Test fun signOutRefusesPendingChangesWithoutDeletingSession() = runBlocking {
        unavailable = true
        repo.startTimer(EntryDraft(description = "Do not lose me"))
        val failure = runCatching { repo.signOut() }.exceptionOrNull()
        assertTrue(failure is IllegalStateException)
        assertNotNull(repo.state.value.me)
        assertEquals(1, repo.state.value.pendingCount)
        assertNotNull(vault.read())
    }

    @Test fun expiredSessionRefreshesOnceForConcurrentRequests() = runBlocking {
        repo.signOut()
        expired = true
        repo.signIn("test@example.test", "password")
        assertEquals(1, refreshCount)
        // Force the persisted token stale while preserving the encrypted-session format.
        val stored = json.parseToJsonElement(vault.read()!!).jsonObject
        val tokens = stored.getValue("tokens").jsonObject.toMutableMap().apply { put("accessTokenExpiresAt", JsonPrimitive(Instant.EPOCH.toString())) }
        vault.write(JsonObject(stored.toMutableMap().apply { put("tokens", JsonObject(tokens)) }).toString())
        coroutineScope { (1..8).map { async(Dispatchers.IO) { repo.request("GET", "/api/me") } }.awaitAll() }
        assertEquals(2, refreshCount)
    }

    @Test fun serverRevokedAccessRefreshesOnceForConcurrentRequests() = runBlocking {
        revokeAccess = true
        coroutineScope { (1..8).map { async(Dispatchers.IO) { repo.request("GET", "/api/me") } }.awaitAll() }
        assertEquals(1, refreshCount)
    }

    @Test fun cachedWorkspaceSwitchOfflinePreservesGlobalRunningTimer() = runBlocking {
        repo.switchWorkspace("two") // Cache a workspace while no timer is running.
        repo.switchWorkspace("one")
        repo.startTimer(EntryDraft(description = "Account-wide timer"))
        val id = repo.state.value.running!!.id
        unavailable = true
        repo.switchWorkspace("two")
        assertEquals("two", repo.state.value.workspaceId)
        assertEquals(id, repo.state.value.running!!.id)
        assertEquals("one", repo.state.value.running!!.workspaceId)
    }

    @Test fun editingRejectedCreateRepairsOriginalCreateInsteadOfUpdatingMissingServerEntry() = runBlocking {
        rejectCreate = true
        assertTrue(runCatching { repo.startTimer(EntryDraft()) }.exceptionOrNull() is ApiException)
        val rejected = repo.state.value.failedChanges.single()
        val original = json.decodeFromString<TimeEntryResponse>(rejected.optimistic!!)
        assertNull(repo.state.value.running)
        rejectCreate = false
        repo.saveEntry(EntryDraft(description = "Corrected details", start = original.start), original)
        assertEquals("${repo.state.value.error}; requests=${writes.map { it.path }}", 0, repo.state.value.pendingCount)
        assertTrue(repo.state.value.failedChanges.isEmpty())
        assertEquals(original.id, repo.state.value.running!!.id)
        assertEquals("Corrected details", entries.getValue(original.id).description)
        assertEquals("POST", writes.last().method)
        assertEquals("/api/workspaces/one/time-entries", writes.last().path)
    }

    @Test fun editOfAnEntryChangedElsewhereWaitsAsARejectedDraft() = runBlocking {
        val start = Instant.now().minusSeconds(7200).toString(); val stop = Instant.now().minusSeconds(3600).toString()
        repo.saveEntry(EntryDraft(description = "Original", start = start, stop = stop))
        val id = entries.keys.single()
        val seen = repo.state.value.entries.single { it.id == id }
        entries[id] = entries.getValue(id).copy(description = "Theirs", version = "2")

        val failure = runCatching { repo.saveEntry(EntryDraft(description = "Mine", start = start, stop = stop), seen) }.exceptionOrNull()

        assertTrue(failure is ApiException)
        assertEquals("\"1\"", writes.last().getHeader("If-Match"))
        assertEquals("Theirs", entries.getValue(id).description)
        assertEquals("Theirs", repo.state.value.entries.single { it.id == id }.description)
        // Retrying keeps this device's version, now made on theirs.
        repo.retryFailedChange(repo.state.value.failedChanges.single().id)
        assertEquals("\"2\"", writes.last().getHeader("If-Match"))
        assertEquals("Mine", entries.getValue(id).description)
        assertEquals(0, repo.state.value.pendingCount)
    }

    @Test fun calendarRangeUsesAccountTimezoneAcrossDaylightSavingTransition() = runBlocking {
        repo.loadEntries(LocalDate.parse("2026-03-29"), LocalDate.parse("2026-03-30"))
        val request = reads.last { it.requestUrl!!.encodedPath.endsWith("/time-entries") }
        assertEquals("2026-03-28T23:00:00Z", request.requestUrl!!.queryParameter("from"))
        assertEquals("2026-03-29T22:00:00Z", request.requestUrl!!.queryParameter("to"))
    }

    private class MemorySession : SessionStore {
        @Volatile private var value: String? = null
        override fun read() = value
        override fun write(value: String) { this.value = value }
        override fun clear() { value = null }
    }
}
