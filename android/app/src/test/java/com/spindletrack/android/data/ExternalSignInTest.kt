package com.spindletrack.android.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The pieces of Continue with Google or Apple that need no browser: PKCE, the start address and the way back. */
class ExternalSignInTest {
    @Test
    fun the_challenge_is_rfc_7636s() {
        assertEquals("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"))
    }

    @Test
    fun a_verifier_is_43_url_safe_characters_and_new_each_time() {
        val verifier = Pkce.verifier()

        assertTrue(Regex("[A-Za-z0-9_-]{43}").matches(verifier))
        assertNotEquals(verifier, Pkce.verifier())
        assertEquals(43, Pkce.challenge(verifier).length)
    }

    @Test
    fun the_start_address_carries_the_app_redirect_challenge_time_zone_and_terms() {
        val url = externalSignInUrl("https://app.spindletrack.com/", "google", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "America/New_York")

        assertEquals(
            "https://app.spindletrack.com/api/auth/external/google/start?redirectUri=spindle%3A%2F%2Fauth%2Fcallback" +
                "&codeChallenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&timeZone=America%2FNew_York&acceptTerms=true",
            url,
        )
    }

    @Test
    fun an_invitation_goes_along() {
        val url = externalSignInUrl("http://10.0.2.2:5258", "apple", "challenge", "Europe/Zagreb", invitation = "a b+c")

        assertTrue(url.startsWith("http://10.0.2.2:5258/api/auth/external/apple/start?"))
        assertTrue(url.endsWith("&acceptTerms=true&invitation=a%20b%2Bc"))
    }

    @Test
    fun the_callback_brings_a_code() {
        assertEquals(ExternalCallback.Code("abc-123_x"), ExternalCallback.parse("spindle://auth/callback?code=abc-123_x"))
        assertEquals(ExternalCallback.Code("a/b c"), ExternalCallback.parse("spindle://auth/callback?code=a%2Fb%20c"))
    }

    @Test
    fun turning_back_is_cancelled_and_anything_else_failed() {
        assertEquals(ExternalCallback.Cancelled, ExternalCallback.parse("spindle://auth/callback?error=cancelled"))
        assertEquals(ExternalCallback.Failed("expired"), ExternalCallback.parse("spindle://auth/callback?error=expired"))
        assertEquals(ExternalCallback.Failed("unknown"), ExternalCallback.parse("spindle://auth/callback"))
    }

    @Test
    fun other_addresses_are_not_the_callback() {
        assertNull(ExternalCallback.parse("spindle://auth/other?code=abc"))
        assertNull(ExternalCallback.parse("spindle://elsewhere/callback?code=abc"))
        assertNull(ExternalCallback.parse("https://auth/callback?code=abc"))
        assertNull(ExternalCallback.parse("not a url"))
    }
}
