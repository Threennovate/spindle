package com.spindletrack.android

import android.Manifest
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.lifecycle.lifecycleScope
import android.content.Intent
import com.spindletrack.android.ui.LaunchOptions
import com.spindletrack.android.ui.MainTab
import com.spindletrack.android.ui.SpindleApp
import com.spindletrack.android.ui.theme.SpindleTheme
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val repository get() = (application as SpindleApplication).repository
    private val shortcut = LauncherShortcut.pending
    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        TimerNotifications(this).update(repository.state.value, force = true)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val options = if (BuildConfig.DEBUG) launchOptions(intent) else LaunchOptions()
        setContent {
            SpindleTheme { SpindleApp(repository, options, shortcut.value) { shortcut.value = null } }
        }
        lifecycleScope.launch {
            repository.state.collect { state ->
                if (state.running != null && Build.VERSION.SDK_INT >= 33) {
                    val prefs = getSharedPreferences("notification_permission", MODE_PRIVATE)
                    if (!prefs.getBoolean("asked", false)) {
                        prefs.edit().putBoolean("asked", true).apply()
                        notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                    }
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        repository.onForeground()
    }
    override fun onStop() {
        repository.onBackground()
        super.onStop()
    }
}

/** A debug run's options, from `adb shell am start … --es tab calendar` and the like; see [LaunchOptions]. */
private fun launchOptions(intent: Intent): LaunchOptions {
    fun value(name: String) = intent.getStringExtra(name)?.takeIf { it.isNotEmpty() }
    return LaunchOptions(
        email = value("email"), password = value("password"), server = value("server"),
        tab = value("tab")?.let { tab -> MainTab.entries.firstOrNull { it.name.equals(tab, ignoreCase = true) } },
        auth = value("auth"), description = value("description"), edit = value("edit"), picker = value("picker"),
        report = value("report"), filters = value("filters") == "1", manage = value("manage"), project = value("project"),
    )
}
