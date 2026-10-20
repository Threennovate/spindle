import { useState } from 'react'

/**
 * State remembered in this browser, e.g. a chosen view. Storage can be unavailable (private windows, blocked
 * site data), in which case the state still works for the session.
 */
export function usePersistentState<T extends string>(key: string, initial: T, allowed: readonly T[]): [T, (value: T) => void] {
  const [value, setValue] = useState<T>(() => {
    try {
      const stored = window.localStorage.getItem(key)
      return stored !== null && (allowed as readonly string[]).includes(stored) ? (stored as T) : initial
    } catch {
      return initial
    }
  })

  function update(next: T) {
    setValue(next)
    try {
      window.localStorage.setItem(key, next)
    } catch {
      // Not remembered, but still applied.
    }
  }

  return [value, update]
}
