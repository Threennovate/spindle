export const OPEN_PALETTE_EVENT = 'spindle:open-palette'

/** Opens the command palette, e.g. from the search button in the header. */
export function openCommandPalette() {
  window.dispatchEvent(new Event(OPEN_PALETTE_EVENT))
}
