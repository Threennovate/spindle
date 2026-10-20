/**
 * Where to go after signing in. Only paths within this app are allowed, so a crafted
 * `?redirect=https://evil.example` link cannot send the user elsewhere.
 */
export function safeRedirect(target: string | undefined): string {
  return target && target.startsWith('/') && !target.startsWith('//') && !target.startsWith('/\\') ? target : '/'
}
