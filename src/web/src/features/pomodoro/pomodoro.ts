/**
 * Pomodoro: focus for a set time, then a short break. While the timer runs, each full focus interval ends with a
 * reminder; with auto-stop, the timer stops there and a break counts down.
 */

/** How many focus intervals have fully passed since the timer started. */
export function intervalsDone(elapsedMs: number, focusMinutes: number): number {
  return Math.max(0, Math.floor(elapsedMs / (focusMinutes * 60_000)))
}

/** Time left in the current focus interval. */
export function focusLeftMs(elapsedMs: number, focusMinutes: number): number {
  const focus = focusMinutes * 60_000
  return focus - (Math.max(0, elapsedMs) % focus)
}

/** "12:05": minutes and seconds of a countdown. */
export function formatCountdown(ms: number): string {
  const seconds = Math.max(0, Math.ceil(ms / 1000))
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}

const LAST_EVENT = 'spindle:pomodoro-last'
const BREAK_END = 'spindle:pomodoro-break-end'

/**
 * Lets one open tab act on a Pomodoro event, such as the end of an interval: the first tab to claim it does, the
 * others see it claimed. Without storage every tab acts, which is only noisy.
 */
export function claim(event: string): boolean {
  try {
    if (localStorage.getItem(LAST_EVENT) === event) {
      return false
    }
    localStorage.setItem(LAST_EVENT, event)
  } catch {
    // Storage is off: act anyway.
  }
  return true
}

/** When the current break ends, shared by every tab, or null outside a break. */
export const breakStore = {
  get(): number | null {
    try {
      const end = Number(localStorage.getItem(BREAK_END))
      return end > Date.now() ? end : null
    } catch {
      return null
    }
  },
  start(minutes: number) {
    try {
      localStorage.setItem(BREAK_END, String(Date.now() + minutes * 60_000))
      window.dispatchEvent(new StorageEvent('storage', { key: BREAK_END }))
    } catch {
      // Without storage there is no break countdown; the reminders still come.
    }
  },
  end() {
    try {
      localStorage.removeItem(BREAK_END)
      window.dispatchEvent(new StorageEvent('storage', { key: BREAK_END }))
    } catch {
      // Nothing to end.
    }
  },
  subscribe(onChange: () => void): () => void {
    const listener = (event: StorageEvent) => event.key === BREAK_END && onChange()
    window.addEventListener('storage', listener)
    return () => window.removeEventListener('storage', listener)
  },
}

/** A short two-note chime, made on the spot so there is no sound file to load. */
export function chime() {
  try {
    const audio = new AudioContext()
    const notes = [880, 1318.5]
    notes.forEach((frequency, index) => {
      const oscillator = audio.createOscillator()
      const gain = audio.createGain()
      const start = audio.currentTime + index * 0.18
      oscillator.frequency.value = frequency
      oscillator.type = 'sine'
      gain.gain.setValueAtTime(0.0001, start)
      gain.gain.exponentialRampToValueAtTime(0.25, start + 0.02)
      gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.6)
      oscillator.connect(gain).connect(audio.destination)
      oscillator.start(start)
      oscillator.stop(start + 0.65)
    })
    setTimeout(() => void audio.close(), 1500)
  } catch {
    // No audio in this browser, or it is not allowed yet; the notification still shows.
  }
}

/** A system notification, if the person allowed them; the app also shows its own. */
export function notify(title: string, body: string) {
  try {
    if ('Notification' in window && Notification.permission === 'granted') {
      new Notification(title, { body, tag: 'spindle-pomodoro', icon: '/favicon.svg' })
    }
  } catch {
    // Some browsers only allow notifications from a service worker; the in-app toast is enough.
  }
}
