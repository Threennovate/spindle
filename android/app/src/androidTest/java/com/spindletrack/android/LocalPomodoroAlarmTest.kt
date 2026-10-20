package com.spindletrack.android

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.work.*
import com.spindletrack.android.data.*
import kotlinx.coroutines.*
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.*
import org.junit.*
import org.junit.Assert.*
import org.junit.runner.RunWith
import java.time.Instant
import java.util.concurrent.TimeUnit

/** Delivers alarms that were already enqueued when the user changed Pomodoro preferences. */
@RunWith(AndroidJUnit4::class)
class LocalPomodoroAlarmTest {
    @Test fun staleAlarmCannotStopTimerAfterSettingsChange() = runBlocking {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val server = InstrumentationRegistry.getArguments().getString("server")
        Assume.assumeTrue("Supply a local API server argument", server != null)
        require(server!!.startsWith("http://10.0.2.2:") || server.startsWith("http://127.0.0.1:"))
        val context = instrumentation.targetContext.applicationContext
        val repo = (context as SpindleApplication).repository
        repo.initialize()
        if (repo.state.value.me != null) {
            require(repo.serverUrl == server && repo.state.value.me!!.email.startsWith("android-"))
            repo.signOut(force = true)
        }
        repo.configureServer(server)
        repo.register("Android alarm smoke", "android-alarm-${java.util.UUID.randomUUID()}@example.test", "Android-Alarm-Secure9!", acceptTerms = true)
        val json = Json { encodeDefaults = true }
        suspend fun preferences(enabled: Boolean, minutes: Int, autoStop: Boolean) {
            val original = repo.state.value.me!!.preferences
            val body = PreferencesRequest(weekStart = original.weekStart, dateFormat = original.dateFormat,
                timeFormat = original.timeFormat, durationFormat = original.durationFormat,
                pomodoroEnabled = enabled, pomodoroMinutes = minutes, pomodoroBreakMinutes = 5,
                pomodoroAutoStop = autoStop)
            repo.request("PUT", "/api/me/preferences", json.encodeToJsonElement(body).jsonObject)
            repo.refresh()
        }
        try {
            preferences(enabled = true, minutes = 25, autoStop = true)
            repo.startTimer(EntryDraft(description = "Stale alarm must preserve me"))
            val entry = repo.state.value.running!!
            val plannedEnd = Instant.parse(entry.start).plusSeconds(25 * 60).toEpochMilli()
            val manager = WorkManager.getInstance(context)
            suspend fun deliverStaleAlarm() {
                val work = OneTimeWorkRequestBuilder<TimerActionWorker>().setInputData(Data.Builder()
                    .putString("action", TimerNotifications.POMODORO).putString("entryId", entry.id)
                    .putLong("at", plannedEnd).putBoolean("autoStop", true).build()).build()
                manager.enqueue(work).result.get(10, TimeUnit.SECONDS)
                withTimeout(15_000) {
                    while (true) {
                        val info = manager.getWorkInfoById(work.id).get(5, TimeUnit.SECONDS)
                        if (info?.state?.isFinished == true) {
                            assertEquals(WorkInfo.State.SUCCEEDED, info.state)
                            break
                        }
                        delay(100)
                    }
                }
                repo.refresh()
                assertEquals(entry.id, repo.state.value.running?.id)
                assertNull(repo.state.value.running?.stop)
                assertEquals(0, repo.state.value.pendingCount)
            }
            preferences(enabled = false, minutes = 25, autoStop = true)
            deliverStaleAlarm()
            preferences(enabled = true, minutes = 45, autoStop = true)
            deliverStaleAlarm()
            preferences(enabled = true, minutes = 25, autoStop = false)
            deliverStaleAlarm()
            // The same worker must still honor a current, matching auto-stop alarm.
            preferences(enabled = true, minutes = 25, autoStop = true)
            val currentWork = OneTimeWorkRequestBuilder<TimerActionWorker>().setInputData(Data.Builder()
                .putString("action", TimerNotifications.POMODORO).putString("entryId", entry.id)
                .putLong("at", plannedEnd).putBoolean("autoStop", true).build()).build()
            manager.enqueue(currentWork).result.get(10, TimeUnit.SECONDS)
            withTimeout(15_000) {
                while (manager.getWorkInfoById(currentWork.id).get(5, TimeUnit.SECONDS)?.state?.isFinished != true) delay(100)
            }
            repo.refresh()
            assertNull(repo.state.value.running)
            val stopped = repo.state.value.entries.single { it.id == entry.id }
            assertEquals(plannedEnd, Instant.parse(stopped.stop).toEpochMilli())
        } finally { repo.signOut(force = true) }
    }
}
