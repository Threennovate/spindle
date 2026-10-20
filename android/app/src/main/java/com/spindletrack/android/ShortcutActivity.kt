package com.spindletrack.android

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import androidx.compose.runtime.mutableStateOf
import com.spindletrack.android.ui.ShortcutStart
import com.spindletrack.android.ui.ShortcutStop

/** The launcher shortcut the app is to carry out, e.g. start a timer, until it has. Only [ShortcutActivity] sets it. */
internal object LauncherShortcut {
    val pending = mutableStateOf<String?>(null)
}

/**
 * Where the launcher's shortcuts land. It isn't exported, so only the launcher (which starts shortcuts as Spindle)
 * reaches it; another app sending the same actions to MainActivity only opens the app and starts no timer.
 */
class ShortcutActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        intent.action?.takeIf { it == ShortcutStart || it == ShortcutStop }?.let { LauncherShortcut.pending.value = it }
        startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP))
        finish()
    }
}
