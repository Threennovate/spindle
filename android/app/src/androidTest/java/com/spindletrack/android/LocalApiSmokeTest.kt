package com.spindletrack.android

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.spindletrack.android.data.*
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.time.Instant
import java.time.ZoneId
import java.time.LocalDate
import java.util.UUID

/** Runs only when a local server argument is supplied; creates an isolated test account. */
@RunWith(AndroidJUnit4::class)
class LocalApiSmokeTest {
    @Test fun accountTimerEditsSplitsAndReportsSurviveRecreation() = runBlocking {
        val server = InstrumentationRegistry.getArguments().getString("server")
        org.junit.Assume.assumeTrue("Supply -e server http://10.0.2.2:5260", server != null)
        require(server!!.startsWith("http://10.0.2.2:") || server.startsWith("http://127.0.0.1:"))
        val context = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext
        val applicationRepository = (context as SpindleApplication).repository
        applicationRepository.initialize()
        if (applicationRepository.state.value.me != null) {
            require(applicationRepository.serverUrl == server && applicationRepository.state.value.me!!.email.startsWith("android-")) { "Use an isolated local test installation." }
            applicationRepository.signOut(force = true)
        }
        var repository = SpindleRepository(context, scheduleSync = {})
        repository.initialize()
        repository.configureServer(server)
        val email = "android-${UUID.randomUUID()}@example.test"
        val password = "Android-Smoke-9!Secure"
        repository.register("Android smoke", email, password, acceptTerms = true)
        assertNotNull(repository.state.value.me)
        assertNull(repository.state.value.error)
        val ws = repository.state.value.workspaceId!!
        val root = "/api/workspaces/$ws"
        suspend fun createProject(name: String) = repository.request("POST", "$root/projects", buildJsonObject {
            put("name", name); put("color", "#635BFF"); put("isBillable", true)
        }).jsonObject.getValue("id").jsonPrimitive.content
        val project = createProject("Android test project")
        val second = createProject("Android parallel project")
        val tag = repository.request("POST", "$root/tags", buildJsonObject { put("name", "android smoke") })
            .jsonObject.getValue("id").jsonPrimitive.content
        repository.refresh()
        repository.startTimer(EntryDraft(description = "On-device timer", projectId = project))
        val started = repository.state.value.running!!
        assertEquals(0, repository.state.value.pendingCount)
        repository.saveEntry(EntryDraft(description = "Edited timer", projectId = project, tagIds = listOf(tag),
            isBillable = true, start = started.start), started)
        assertEquals(listOf(tag), repository.state.value.running!!.tagIds)
        repository = SpindleRepository(context, scheduleSync = {})
        repository.initialize()
        assertEquals(started.id, repository.state.value.running!!.id)
        repository.stopTimer(at = Instant.parse(started.start).plusSeconds(60))
        assertNull(repository.state.value.running)
        val start = Instant.now().minusSeconds(7200)
        repository.saveEntry(EntryDraft(description = "Manual split", projectId = project, start = start.toString(),
            stop = start.plusSeconds(3600).toString(), shares = listOf(
                TimeShareRequest(project, isBillable = true, percent = 60),
                TimeShareRequest(second, isBillable = true, percent = 40))))
        assertEquals(0, repository.state.value.pendingCount)
        assertTrue(repository.state.value.failedChanges.isEmpty())
        repository.refresh()
        val split = repository.state.value.entries.single { it.description == "Manual split" }
        assertEquals(listOf(60, 40), split.shares.map { it.percent })
        val report = repository.request("POST", "$root/reports/summary", buildJsonObject {
            put("from", start.atZone(ZoneId.of(repository.state.value.me!!.timeZone)).toLocalDate().toString())
            put("to", LocalDate.now(ZoneId.of(repository.state.value.me!!.timeZone)).toString())
            put("groupBy", "project")
        }).jsonObject
        assertEquals(3660L, report.getValue("totals").jsonObject.getValue("seconds").jsonPrimitive.long)
        val groups = report.getValue("groups").jsonArray.map { it.jsonObject }
        assertEquals(2220L, groups.single { it["id"]?.jsonPrimitive?.content == project }.getValue("seconds").jsonPrimitive.long)
        assertEquals(1440L, groups.single { it["id"]?.jsonPrimitive?.content == second }.getValue("seconds").jsonPrimitive.long)
        repository.signOut()
        repository.signIn(email, password)
        assertTrue(repository.state.value.entries.any { it.id == split.id })
        repository.signOut()
    }
}
