export const APPEARANCES = ['system', 'light', 'dark'] as const
export type Appearance = (typeof APPEARANCES)[number]

const KEY = 'spindle:appearance'
const listeners = new Set<() => void>()
/** Pages that are documents for someone else, such as a timesheet link, look the same in every browser. */
let lightOnly = 0

function systemIsDark() {
  return window.matchMedia('(prefers-color-scheme: dark)').matches
}

/** The appearance chosen in this browser: the operating system's unless the person picked one. */
export function getAppearance(): Appearance {
  try {
    const stored = window.localStorage.getItem(KEY)
    return (APPEARANCES as readonly string[]).includes(stored ?? '') ? (stored as Appearance) : 'system'
  } catch {
    return 'system'
  }
}

function apply() {
  const appearance = getAppearance()
  document.documentElement.classList.toggle('dark', lightOnly === 0 && (appearance === 'dark' || (appearance === 'system' && systemIsDark())))
  listeners.forEach((notify) => notify())
}

/** Switches the appearance, colours easing over instead of flashing. */
export function setAppearance(appearance: Appearance) {
  try {
    if (appearance === 'system') {
      window.localStorage.removeItem(KEY)
    } else {
      window.localStorage.setItem(KEY, appearance)
    }
  } catch {
    // Applied for now, not remembered.
  }
  const root = document.documentElement
  root.classList.add('theme-switching')
  apply()
  window.setTimeout(() => root.classList.remove('theme-switching'), 320)
}

export function subscribeToAppearance(listener: () => void) {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** Follows the operating system's light or dark appearance, including changes while the app is open, unless the person chose one. */
export function followSystemTheme() {
  apply()
  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', apply)
  // Another tab changed it.
  window.addEventListener('storage', (event) => event.key === KEY && apply())
}

/** Keeps the page light while it is open, whatever the appearance; returns what puts it back. */
export function keepLight() {
  lightOnly++
  apply()
  return () => {
    lightOnly--
    apply()
  }
}
