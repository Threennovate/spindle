package com.spindletrack.android

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import androidx.compose.runtime.mutableStateOf
import com.spindletrack.android.data.ExternalCallback

/** The address the browser came back to after Google or Apple, until the sign-in screen has used it. */
internal object ExternalSignInReturn {
    val pending = mutableStateOf<String?>(null)
}

/**
 * Where the browser comes back to, at `spindle://auth/callback`. It hands the address to [MainActivity] and brings it
 * back to the front, which closes the browser tab opened above it. Any app could open this address, but a code is only
 * worth something with the verifier this app kept.
 */
class AuthCallbackActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        intent.dataString?.takeIf { ExternalCallback.parse(it) != null }?.let { ExternalSignInReturn.pending.value = it }
        startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP))
        finish()
    }
}
