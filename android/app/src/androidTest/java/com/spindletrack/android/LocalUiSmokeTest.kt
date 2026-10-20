package com.spindletrack.android

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.spindletrack.android.data.SpindleRepository
import kotlinx.coroutines.runBlocking
import org.junit.*
import org.junit.Assert.*
import org.junit.runner.RunWith
import java.util.UUID

/** Actual screen interactions against a disposable account on a local API. */
@RunWith(AndroidJUnit4::class)
class LocalUiSmokeTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()
    private lateinit var repository: SpindleRepository

    @Test fun timerCalendarCatalogReportsAndSettings() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val server = InstrumentationRegistry.getArguments().getString("server")
        Assume.assumeTrue("Supply a local API server argument", server != null)
        require(server!!.startsWith("http://10.0.2.2:") || server.startsWith("http://127.0.0.1:"))
        val context = instrumentation.targetContext.applicationContext
        if (android.os.Build.VERSION.SDK_INT >= 33) {
            instrumentation.uiAutomation.grantRuntimePermission(context.packageName, android.Manifest.permission.POST_NOTIFICATIONS)
        }
        repository = (context as SpindleApplication).repository
        runBlocking {
            repository.initialize()
            if (repository.state.value.me != null) {
                require(repository.serverUrl == server && repository.state.value.me!!.email.startsWith("android-"))
                repository.signOut(force = true)
            }
            repository.configureServer(server)
            repository.register("Android UI smoke", "android-ui-${UUID.randomUUID()}@example.test", "Android-UI-Secure9!", acceptTerms = true)
        }
        try {
            awaitText("What are you working on?", description = true)
            field("What are you working on?").performTextReplacement("UI timer")
            described("Start timer").performClick()
            compose.waitUntil(15_000) { repository.state.value.running?.description == "UI timer" }
            awaitText("Stop timer", description = true)
            screenshot("timer")
            compose.onAllNodesWithText("UI timer").filter(hasClickAction() and !hasSetTextAction()).onFirst().performClick()
            awaitText("Running Entry")
            field("Description").performTextReplacement("UI edited timer")
            bar("Save")
            compose.waitUntil(15_000) { repository.state.value.running?.description == "UI edited timer" }
            described("Stop timer").performClick()
            compose.waitUntil(15_000) { repository.state.value.running == null }
            described("Add a time entry").performClick()
            awaitText("New Time Entry")
            field("Description").performTextReplacement("UI manual entry")
            bar("Add")
            compose.waitUntil(15_000) { repository.state.value.entries.any { it.description == "UI manual entry" && it.stop != null } }

            tab("Calendar")
            awaitText("UI manual entry", description = false, substring = true)
            screenshot("calendar")
            described("Previous week").performClick()
            described("Next week").performClick()

            tab("Projects")
            described("New project").performClick()
            awaitText("New Project")
            field("Name").performTextReplacement("UI project")
            bar("Create")
            compose.waitUntil(15_000) { repository.state.value.projects.any { it.name == "UI project" } }
            click("UI project")
            field("Add a task").performTextReplacement("UI task")
            click("Add")
            compose.waitUntil(15_000) { repository.state.value.projects.any { p -> p.tasks.any { it.name == "UI task" } } }
            awaitText("UI task")
            described("Back").performClick()
            click("Tags")
            described("New tag").performClick()
            dialogField("Tag name").performTextReplacement("UI tag")
            click("Add")
            compose.waitUntil(15_000) { repository.state.value.tags.any { it.name == "UI tag" } }
            click("Clients")
            described("New client").performClick()
            dialogField("Client name").performTextReplacement("UI client")
            click("Add")
            compose.waitUntil(15_000) { repository.state.value.clients.any { it.name == "UI client" } }
            screenshot("catalog")

            tab("Reports")
            awaitText("Breakdown")
            click("Detailed")
            awaitText("UI manual entry")
            click("Weekly")
            awaitText("By day")
            click("Summary")
            awaitText("Breakdown")
            described("Saved reports and export").performClick()
            click("Save as New Report…")
            dialogField("Name").performTextReplacement("UI saved report")
            click("Save")
            awaitText("UI saved report")
            screenshot("reports")

            tab("Settings")
            click(repository.state.value.me!!.fullName)
            awaitText("Profile")
            field("Full name").performTextReplacement("Android UI verified")
            bar("Save")
            compose.waitUntil(15_000) { repository.state.value.me?.fullName == "Android UI verified" }
            screenshot("settings")
            assertEquals(0, repository.state.value.pendingCount)
            assertTrue(repository.state.value.failedChanges.isEmpty())
        } finally {
            runBlocking { repository.signOut(force = true) }
        }
    }
    private fun screenshot(name: String) {
        compose.waitForIdle()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val folder = java.io.File(instrumentation.targetContext.cacheDir, "ui-smoke").apply { mkdirs() }
        instrumentation.uiAutomation.takeScreenshot()?.let { bitmap ->
            java.io.File(folder, "$name.png").outputStream().use { bitmap.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, it) }
            bitmap.recycle()
        }
    }
    /** A text field, by the label it gives TalkBack. */
    private fun field(label: String) = compose.onAllNodes(hasSetTextAction() and hasContentDescription(label)).onFirst()
    /** A field in a dialog, by its Material label. */
    private fun dialogField(label: String) = compose.onNode(hasSetTextAction() and hasText(label))
    private fun described(label: String) = compose.onAllNodesWithContentDescription(label).onFirst()
    /** The main action in an editor's bar, e.g. Save. */
    private fun bar(text: String) {
        compose.onAllNodesWithText(text).filter(hasClickAction()).onFirst().performClick()
        compose.waitForIdle()
    }
    private fun click(text: String) { compose.onAllNodesWithText(text).filter(hasClickAction()).onFirst().performClick() }
    private fun tab(name: String) { compose.onAllNodesWithText(name).filter(hasClickAction()).onLast().performClick() }
    private fun awaitText(text: String, description: Boolean = false, substring: Boolean = false) {
        compose.waitUntil(15_000) {
            (if (description) compose.onAllNodesWithContentDescription(text, substring = substring) else compose.onAllNodesWithText(text, substring = substring))
                .fetchSemanticsNodes().isNotEmpty()
        }
    }
}
