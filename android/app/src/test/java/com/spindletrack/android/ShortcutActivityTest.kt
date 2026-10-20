package com.spindletrack.android

import android.content.Intent
import androidx.test.core.app.ApplicationProvider
import com.spindletrack.android.ui.ShortcutStart
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

/** Only the launcher's shortcuts, through the unexported ShortcutActivity, ask the app to start or stop a timer. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28], application = android.app.Application::class)
class ShortcutActivityTest {
    @After
    fun forget() {
        LauncherShortcut.pending.value = null
    }

    @Test
    fun a_launcher_shortcut_is_passed_on_and_opens_the_app() {
        val activity = Robolectric.buildActivity(ShortcutActivity::class.java, Intent(ShortcutStart)).create().get()

        assertEquals(ShortcutStart, LauncherShortcut.pending.value)
        assertEquals(MainActivity::class.java.name, shadowOf(activity).nextStartedActivity.component?.className)
    }

    @Test
    fun another_action_is_not() {
        Robolectric.buildActivity(ShortcutActivity::class.java, Intent("com.example.SOMETHING")).create()

        assertNull(LauncherShortcut.pending.value)
    }

    @Test
    fun the_shortcut_activity_is_not_exported() {
        val context = ApplicationProvider.getApplicationContext<android.app.Application>()
        val info = context.packageManager.getActivityInfo(android.content.ComponentName(context, ShortcutActivity::class.java), 0)

        assertEquals(false, info.exported)
    }
}
