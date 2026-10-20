import { useCallback, useSyncExternalStore } from 'react'

/**
 * Short lists of IDs kept in this browser, e.g. ghost blocks dismissed in the calendar or what the review was told
 * "Not now". Every part of the page reading one follows a change at once, and other tabs follow through `storage`.
 */
const cache = new Map<string, readonly string[]>()
const listeners = new Map<string, Set<() => void>>()

function read(key: string): readonly string[] {
  let ids = cache.get(key)
  if (!ids) {
    try {
      const stored: unknown = JSON.parse(window.localStorage.getItem(key) ?? '[]')
      ids = Array.isArray(stored) ? stored.filter((id): id is string => typeof id === 'string') : []
    } catch {
      ids = []
    }
    cache.set(key, ids)
  }
  return ids
}

function write(key: string, ids: readonly string[]) {
  cache.set(key, ids)
  try {
    window.localStorage.setItem(key, JSON.stringify(ids))
  } catch {
    // Kept for now, but not remembered.
  }
  listeners.get(key)?.forEach((listener) => listener())
}

function subscribe(key: string, listener: () => void) {
  const forKey = listeners.get(key) ?? new Set()
  forKey.add(listener)
  listeners.set(key, forKey)
  const fromOtherTab = (event: StorageEvent) => {
    if (event.key === key) {
      cache.delete(key)
      listener()
    }
  }
  window.addEventListener('storage', fromOtherTab)
  return () => {
    forKey.delete(listener)
    window.removeEventListener('storage', fromOtherTab)
  }
}

/** A list kept under `key`, the newest `limit` of it. */
export function useKeptList(key: string, limit: number) {
  const ids = useSyncExternalStore(
    useCallback((listener) => subscribe(key, listener), [key]),
    () => read(key),
  )
  return {
    ids,
    has: (id: string) => ids.includes(id),
    add: (id: string) => write(key, [...read(key).filter((kept) => kept !== id), id].slice(-limit)),
    remove: (id: string) => write(key, read(key).filter((kept) => kept !== id)),
  }
}
