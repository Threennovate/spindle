import { publicApi } from '@/api/public'
import { unwrap } from '@/api/unwrap'
import { errorMessage } from '@/lib/error-message'
import { describeBrowser } from './device'

export function supportsPasskeys(): boolean {
  return window.isSecureContext && typeof PublicKeyCredential !== 'undefined'
    && typeof PublicKeyCredential.parseCreationOptionsFromJSON === 'function'
    && typeof PublicKeyCredential.parseRequestOptionsFromJSON === 'function'
    && typeof PublicKeyCredential.prototype.toJSON === 'function'
}

export async function createPasskey(optionsJson: string): Promise<string> {
  const publicKey = PublicKeyCredential.parseCreationOptionsFromJSON(JSON.parse(optionsJson))
  const credential = await navigator.credentials.create({ publicKey })
  if (!(credential instanceof PublicKeyCredential)) throw new Error('No passkey was created.')
  return JSON.stringify(credential.toJSON())
}

export async function signInWithPasskey() {
  const { optionsJson } = await unwrap(publicApi.POST('/api/auth/web/passkeys/options'))
  const publicKey = PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(optionsJson))
  const credential = await navigator.credentials.get({ publicKey })
  if (!(credential instanceof PublicKeyCredential)) throw new Error('No passkey was selected.')
  return unwrap(publicApi.POST('/api/auth/web/passkeys/login', {
    body: { credentialJson: JSON.stringify(credential.toJSON()), deviceName: describeBrowser(navigator.userAgent) },
  }))
}

export function passkeyError(error: unknown): string {
  if (error instanceof DOMException) {
    if (error.name === 'NotAllowedError' || error.name === 'AbortError') {
      return 'The passkey request was cancelled or timed out. You can try again.'
    }
    if (error.name === 'InvalidStateError') return 'This authenticator already has a passkey for your account.'
    if (error.name === 'SecurityError') return 'Passkeys are not available on this address.'
  }
  return errorMessage(error, "Couldn't use the passkey. Please try again.")
}
