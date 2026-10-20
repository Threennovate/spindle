package com.spindletrack.android

import android.Manifest
import android.app.AlarmManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.work.CoroutineWorker
import androidx.work.Data
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.spindletrack.android.data.AppState
import java.time.Instant
import kotlin.math.max

/**
 * The system draws elapsed time from the start timestamp, including while our process is suspended. On Android 16 the
 * timer asks to be a Live Update, as the iPhone's Live Activity: on the lock screen and as a chip in the status bar.
 */
class TimerNotifications(private val context: Context) {
    private var lastKey: String? = null
    private val manager = context.getSystemService(NotificationManager::class.java)
    private val alarms = context.getSystemService(AlarmManager::class.java)

    fun update(state: AppState, force: Boolean = false) {
        val entry = state.running
        val preferences = state.me?.preferences
        val key = listOf(state.me?.id, entry, state.projects, preferences).hashCode().toString()
        if (!force && key == lastKey) return
        lastKey = key
        // Default importance, as a silent channel isn't shown on the lock screen even as a Live Update; no sound, no vibration.
        manager.deleteNotificationChannel(OLD_TIMER_CHANNEL)
        manager.createNotificationChannel(NotificationChannel(TIMER_CHANNEL, "Running timer", NotificationManager.IMPORTANCE_DEFAULT).apply {
            setSound(null, null); enableVibration(false)
        })
        manager.createNotificationChannel(NotificationChannel(POMODORO_CHANNEL, "Pomodoro", NotificationManager.IMPORTANCE_DEFAULT))
        for (index in 1..8) alarms.cancel(alarmIntent(index, Intent(context, TimerActionReceiver::class.java)))
        if (entry == null || state.me == null) {
            manager.cancel(TIMER_NOTIFICATION)
            manager.cancel(POMODORO_NOTIFICATION)
            return
        }
        val start = runCatching { Instant.parse(entry.start).toEpochMilli() }.getOrNull() ?: return
        // A timer the person swiped away stays away until the next one; Live Updates mustn't come back once dismissed.
        val dismissed = dismissals(context).getString(DISMISSED_ENTRY, null) == entry.id
        // As the iPhone's Live Activity: what's being done, then where the time goes, a split's projects with their shares.
        val project = if (entry.shares.size > 1) entry.shares.joinToString(" · ") { part -> "${state.projects.find { it.id == part.projectId }?.name ?: "No project"} ${part.percent}%" }
            else state.projects.find { it.id == entry.projectId }?.name ?: "No project"
        val open = PendingIntent.getActivity(context, 0, Intent(context, MainActivity::class.java), FLAGS)
        val stop = PendingIntent.getBroadcast(context, 0,
            Intent(context, TimerActionReceiver::class.java).setAction(STOP).putExtra("entryId", entry.id), FLAGS)
        val dismiss = PendingIntent.getBroadcast(context, 2,
            Intent(context, TimerActionReceiver::class.java).setAction(DISMISSED).putExtra("entryId", entry.id), FLAGS)
        // What a locked phone shows when the person hides sensitive content: that a timer runs, not what it's for.
        val public = timer(start, open, stop).setContentTitle("Timer running").build()
        val notification = timer(start, open, stop)
            .setContentTitle(entry.description.ifBlank { "No description" })
            .setContentText(project)
            // A Live Update is always expanded, so a split's projects get every line they need.
            .setStyle(NotificationCompat.BigTextStyle().bigText(project))
            .setPublicVersion(public).setDeleteIntent(dismiss)
            .setRequestPromotedOngoing(true).build()
        if (dismissed) manager.cancel(TIMER_NOTIFICATION)
        else if (canNotify()) manager.notify(TIMER_NOTIFICATION, notification)
        if (preferences?.pomodoroEnabled == true) {
            val focusMillis = preferences.pomodoroMinutes.toLong() * 60_000
            if (focusMillis <= 0) return
            val first = max(1L, (System.currentTimeMillis() - start) / focusMillis + 1)
            val count = if (preferences.pomodoroAutoStop) 1 else 8
            for (offset in 0 until count) {
                val interval = if (preferences.pomodoroAutoStop) 1L else first + offset
                val end = start + interval * focusMillis
                val intent = Intent(context, TimerActionReceiver::class.java).setAction(POMODORO)
                    .putExtra("entryId", entry.id).putExtra("at", end)
                    .putExtra("autoStop", preferences.pomodoroAutoStop)
                // Approximate delivery respects Android battery restrictions; logical stop time stays exact.
                alarms.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, max(System.currentTimeMillis() + 1000, end), alarmIntent(offset + 1, intent))
            }
        }
    }

    private fun timer(start: Long, open: PendingIntent, stop: PendingIntent) = NotificationCompat.Builder(context, TIMER_CHANNEL)
        .setSmallIcon(R.drawable.ic_notification)
        .setColor(0xFF8F5D04.toInt())
        .setWhen(start).setUsesChronometer(true).setShowWhen(true)
        .setOngoing(true).setOnlyAlertOnce(true).setSilent(true).setContentIntent(open)
        .addAction(R.drawable.ic_notification, "Stop", stop)
        .setCategory(NotificationCompat.CATEGORY_STOPWATCH)

    private fun alarmIntent(index: Int, intent: Intent): PendingIntent =
        PendingIntent.getBroadcast(context, 100 + index, intent.setAction(POMODORO), FLAGS)

    private fun canNotify(): Boolean = Build.VERSION.SDK_INT < 33 || ContextCompat.checkSelfPermission(
        context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    companion object {
        const val STOP = "com.spindletrack.android.STOP"
        const val POMODORO = "com.spindletrack.android.POMODORO"
        const val DISMISSED = "com.spindletrack.android.DISMISSED"
        const val DISMISSED_ENTRY = "dismissedEntry"
        const val TIMER_CHANNEL = "running_timer_live"
        const val OLD_TIMER_CHANNEL = "running_timer"
        const val POMODORO_CHANNEL = "pomodoro"
        const val TIMER_NOTIFICATION = 1000
        const val POMODORO_NOTIFICATION = 1001
        const val FLAGS = PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE

        fun dismissals(context: Context) = context.getSharedPreferences("timer_notification", Context.MODE_PRIVATE)

        /** Whether the person turned off Live Updates for Spindle, so Settings can offer the switch; never before Android 16. */
        fun liveUpdatesOff(context: Context): Boolean =
            Build.VERSION.SDK_INT >= 36 && !context.getSystemService(NotificationManager::class.java).canPostPromotedNotifications()

        fun liveUpdatesSettings(context: Context): Intent = Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_PROMOTION_SETTINGS)
            .putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, context.packageName)
    }
}

class TimerActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra("entryId") ?: return
        if (intent.action == TimerNotifications.DISMISSED) {
            TimerNotifications.dismissals(context).edit().putString(TimerNotifications.DISMISSED_ENTRY, id).apply()
            return
        }
        val data = Data.Builder().putString("entryId", id).putString("action", intent.action)
            .putLong("at", intent.getLongExtra("at", System.currentTimeMillis()))
            .putBoolean("autoStop", intent.getBooleanExtra("autoStop", false)).build()
        WorkManager.getInstance(context).enqueue(OneTimeWorkRequestBuilder<TimerActionWorker>().setInputData(data).build())
    }
}

class TimerRestoreReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        WorkManager.getInstance(context).enqueue(OneTimeWorkRequestBuilder<TimerActionWorker>()
            .setInputData(Data.Builder().putString("action", "restore").build()).build())
    }
}

class TimerActionWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        val repository = (applicationContext as SpindleApplication).repository
        runCatching { repository.initialize() }
        if (inputData.getString("action") == "restore") {
            TimerNotifications(applicationContext).update(repository.state.value, force = true)
            return Result.success()
        }
        runCatching { repository.refresh() }
        val entry = repository.state.value.running ?: return Result.success()
        if (entry.id != inputData.getString("entryId")) return Result.success()
        val action = inputData.getString("action")
        val automaticStop = action == TimerNotifications.POMODORO && inputData.getBoolean("autoStop", false)
        if (automaticStop) {
            val prefs = repository.state.value.me?.preferences ?: return Result.success()
            val plannedEnd = runCatching { Instant.parse(entry.start).toEpochMilli() + prefs.pomodoroMinutes.toLong() * 60_000 }.getOrNull()
            // An already queued alarm must not apply preferences that have since changed.
            if (!prefs.pomodoroEnabled || !prefs.pomodoroAutoStop || plannedEnd != inputData.getLong("at", -1)) return Result.success()
        }
        if (action == TimerNotifications.STOP || automaticStop) {
            val at = Instant.ofEpochMilli(inputData.getLong("at", System.currentTimeMillis()))
            val result = runCatching { repository.stopTimer(entry, at) }
            if (result.isFailure) showPomodoro("Open Spindle to finish stopping the timer", result.exceptionOrNull()?.message ?: "Add the required details.")
        } else if (action == TimerNotifications.POMODORO) {
            val prefs = repository.state.value.me?.preferences
            if (prefs?.pomodoroEnabled == true) {
                showPomodoro("Time for a break", "${prefs.pomodoroMinutes} minutes of focus done. Take ${prefs.pomodoroBreakMinutes} minutes off.")
                TimerNotifications(applicationContext).update(repository.state.value, force = true)
            }
        }
        return Result.success()
    }

    private fun showPomodoro(title: String, message: String) {
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val open = PendingIntent.getActivity(applicationContext, 1, Intent(applicationContext, MainActivity::class.java), TimerNotifications.FLAGS)
        val notice = NotificationCompat.Builder(applicationContext, TimerNotifications.POMODORO_CHANNEL)
            .setSmallIcon(R.drawable.ic_notification).setContentTitle(title).setContentText(message)
            .setContentIntent(open).setAutoCancel(true).build()
        NotificationManagerCompat.from(applicationContext).notify(TimerNotifications.POMODORO_NOTIFICATION, notice)
    }
}
