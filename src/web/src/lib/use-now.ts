import { useSyncExternalStore } from 'react'

// One clock for the whole app: a single interval, and every running duration on screen ticks in step.
let now = Date.now()
let interval: number | undefined
const listeners = new Set<() => void>()

function subscribe(listener: () => void) {
  listeners.add(listener)
  if (listeners.size === 1) {
    now = Date.now()
    interval = window.setInterval(() => {
      now = Date.now()
      listeners.forEach((notify) => notify())
    }, 1000)
  }
  return () => {
    listeners.delete(listener)
    if (listeners.size === 0) {
      window.clearInterval(interval)
    }
  }
}

function subscribeToNothing() {
  return () => {}
}

/**
 * The current time, ticking every second while `active`, e.g. to show a running timer. When not active the
 * value can be stale, so use it only for things that are running.
 */
export function useNow(active = true): number {
  return useSyncExternalStore(active ? subscribe : subscribeToNothing, () => now)
}
