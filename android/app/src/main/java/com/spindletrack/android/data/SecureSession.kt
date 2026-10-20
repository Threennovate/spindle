package com.spindletrack.android.data
import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec
interface SessionStore { fun read():String?; fun write(value:String); fun clear() }
class SecureSession(context:Context):SessionStore{
 private val prefs=context.getSharedPreferences("session",Context.MODE_PRIVATE)
 private val alias="spindle.session.v1"
 private fun key():SecretKey{
 val store=KeyStore.getInstance("AndroidKeyStore").apply{load(null)}
 (store.getKey(alias,null) as? SecretKey)?.let{return it}
 return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore").apply{init(KeyGenParameterSpec.Builder(alias,KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).build())}.generateKey()
 }
 override fun read():String?=prefs.getString("ciphertext",null)?.let{encoded->runCatching{val bytes=Base64.decode(encoded,Base64.NO_WRAP);Cipher.getInstance("AES/GCM/NoPadding").run{init(Cipher.DECRYPT_MODE,key(),GCMParameterSpec(128,bytes.copyOfRange(0,12)));String(doFinal(bytes.copyOfRange(12,bytes.size)),Charsets.UTF_8)}}.getOrNull()}
 override fun write(value:String){val cipher=Cipher.getInstance("AES/GCM/NoPadding").apply{init(Cipher.ENCRYPT_MODE,key())};check(prefs.edit().putString("ciphertext",Base64.encodeToString(cipher.iv+cipher.doFinal(value.toByteArray()),Base64.NO_WRAP)).commit())}
 override fun clear(){prefs.edit().clear().commit()}
}
