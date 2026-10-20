export const SHOW_SHORTCUTS_EVENT = 'spindle:show-shortcuts'

/** Opens the keyboard shortcuts dialog, e.g. from the account menu. */
export function showKeyboardShortcuts() {
  window.dispatchEvent(new Event(SHOW_SHORTCUTS_EVENT))
}
