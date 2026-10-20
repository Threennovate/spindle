import { useEffect, type RefObject } from 'react'

const EVENT = 'spindle:focus-description'
/** How long a request waits for the field to mount, e.g. after going to Home, which may mount it twice as it loads. */
const PENDING_MS = 1500
// Remembered so a field that mounts after the request (e.g. after navigating to the timer) still takes focus.
let requestedAt = 0

/** Asks the timer's description field to take focus, e.g. from a keyboard shortcut on another page. */
export function requestDescriptionFocus() {
  requestedAt = Date.now()
  window.dispatchEvent(new Event(EVENT))
}

export function useDescriptionFocus(ref: RefObject<HTMLInputElement | null>) {
  useEffect(() => {
    const focus = () => ref.current?.focus()
    if (Date.now() - requestedAt < PENDING_MS) {
      focus()
    }
    window.addEventListener(EVENT, focus)
    return () => window.removeEventListener(EVENT, focus)
  }, [ref])
}
