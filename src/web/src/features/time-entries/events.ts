import type { TimeEntry } from '@/api/types'

export const EDIT_ENTRY_EVENT = 'spindle:edit-entry'

export interface EditEntryRequest {
  /** The entry to edit; without it, `newEntry` is created. */
  entry?: TimeEntry
  /** A span of time to add an entry for, e.g. the gap since the last entry. */
  newEntry?: { start: Date; stop: Date }
  /** The timer was about to stop, but the entry lacks details the workspace requires. */
  stopping?: boolean
  /** Begins splitting the entry's time between projects. */
  split?: boolean
}

/** Opens a time entry for editing, or a new one, from anywhere, e.g. from the sidebar's stop button. */
export function editEntry(request: EditEntryRequest) {
  window.dispatchEvent(new CustomEvent<EditEntryRequest>(EDIT_ENTRY_EVENT, { detail: request }))
}
