package com.spindletrack.android

import android.content.Intent
import android.net.Uri
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

/** The browser's way back after Google or Apple is handed to the sign-in screen and brings the app to the front. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28], application = android.app.Application::class)
class AuthCallbackActivityTest {
    @After
    fun forget() {
        ExternalSignInReturn.pending.value = null
    }

    @Test
    fun the_callback_is_passed_on_and_opens_the_app() {
        val intent = Intent(Intent.ACTION_VIEW, Uri.parse("spindle://auth/callback?code=abc"))
        val activity = Robolectric.buildActivity(AuthCallbackActivity::class.java, intent).create().get()

        assertEquals("spindle://auth/callback?code=abc", ExternalSignInReturn.pending.value)
        assertEquals(MainActivity::class.java.name, shadowOf(activity).nextStartedActivity.component?.className)
    }

    @Test
    fun another_address_is_not() {
        Robolectric.buildActivity(AuthCallbackActivity::class.java, Intent(Intent.ACTION_VIEW, Uri.parse("spindle://other"))).create()

        assertNull(ExternalSignInReturn.pending.value)
    }
}
