import { useMemo, useState, type KeyboardEvent, type RefObject } from 'react'
import type { Catalog } from '../catalog/use-catalog'
import { applyMentions, findMentions, insertMention, mentionable, mentionAt, parseMentions, type MentionBase } from './mentions'

/**
 * `@Project` in a description field: typing `@` offers the workspace's projects, and choosing one writes its name.
 * Mentions are tinted in the field, and while it has focus a line beneath shows where the time and each text will go.
 */
export function useMentions(value: string, onChange: (value: string) => void, inputRef: RefObject<HTMLInputElement | null>, catalog: Catalog) {
  const projects = useMemo(() => mentionable(catalog.projects), [catalog.projects])
  const [caret, setCaret] = useState<number | null>(null)
  const [highlighted, setHighlighted] = useState(0)
  // Escape closes the list for the mention being typed, until another `@` is.
  const [dismissed, setDismissed] = useState<number | null>(null)

  const typing = caret === null ? null : mentionAt(value, caret)
  const query = typing && typing.start !== dismissed ? typing.query.toLowerCase() : null
  const matches =
    query === null
      ? []
      : projects
          .filter((project) => project.name.toLowerCase().includes(query))
          .sort((a, b) => Number(!a.name.toLowerCase().startsWith(query)) - Number(!b.name.toLowerCase().startsWith(query)) || a.name.localeCompare(b.name))
          .slice(0, 8)
  const active = Math.min(highlighted, Math.max(0, matches.length - 1))

  function track() {
    setCaret(inputRef.current?.selectionStart ?? null)
  }

  function pick(name: string) {
    if (!typing) {
      return
    }
    const next = insertMention(value, typing, name)
    onChange(next.text)
    setHighlighted(0)
    requestAnimationFrame(() => {
      inputRef.current?.setSelectionRange(next.caret, next.caret)
      setCaret(next.caret)
    })
  }

  /** Handles the keys of the list of projects while it is open; returns whether it did. */
  function onKeyDown(event: KeyboardEvent<HTMLInputElement>): boolean {
    if (matches.length === 0) {
      return false
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      setHighlighted((active + (event.key === 'ArrowDown' ? 1 : matches.length - 1)) % matches.length)
    } else if (event.key === 'Enter' || event.key === 'Tab') {
      pick(matches[active].name)
    } else if (event.key === 'Escape') {
      setDismissed(typing!.start)
    } else {
      return false
    }
    event.preventDefault()
    return true
  }

  return {
    projects,
    /** Projects offered for the mention being typed. */
    matches,
    active,
    pick,
    onKeyDown,
    /** Call on input, click and key up, so the field knows where the caret is. */
    track,
    hasMentions: findMentions(value, projects).length > 0,
    /** Where the time and texts go for an entry that is `base` before the mentions. */
    apply: (base: MentionBase) => applyMentions(parseMentions(value, projects), base, projects),
  }
}

export type Mentions = ReturnType<typeof useMentions>
