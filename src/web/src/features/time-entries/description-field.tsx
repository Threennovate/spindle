import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { cn } from 'cn'
import { useId, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import type { TimeEntrySuggestion } from '@/api/types'
import { useDebouncedValue } from '@/lib/use-debounced-value'
import { ProjectLabel } from '../catalog/project-label'
import { SplitLabel } from '../catalog/split-label'
import type { Catalog } from '../catalog/use-catalog'
import { useDescriptionFocus } from './focus'
import { MentionHighlights, MentionList, MentionPreview } from './mention-field'
import { useMentions } from './use-mentions'
import type { MentionBase } from './mentions'
import { suggestionsQuery } from './queries'
import { isSplit, partsOf } from './split'

interface DescriptionFieldProps {
  workspaceId: string
  catalog: Catalog
  value: string
  onChange: (value: string) => void
  /** Enter without a highlighted suggestion. */
  onSubmit: () => void
  onPick: (suggestion: TimeEntrySuggestion) => void
  /** What the entry is about before any `@Project` mentions, to show where they send the time. */
  mentionBase: MentionBase
}

const keyOf = (suggestion: TimeEntrySuggestion) =>
  `${suggestion.description}|${suggestion.projectId ?? ''}|${suggestion.taskId ?? ''}|${suggestion.shares.map((s) => `${s.projectId}/${s.taskId}/${s.percent}`).join(',')}`

/**
 * The "What are you working on?" field. While typing it offers previous entries; arrow keys move through them,
 * Enter picks the highlighted one or, with none highlighted, submits what was typed. `@` mentions a project, which
 * splits the time with it and gives it the text that follows (see `mentions.ts`).
 */
export function DescriptionField({ workspaceId, catalog, value, onChange, onSubmit, onPick, mentionBase }: DescriptionFieldProps) {
  const listId = useId()
  const inputRef = useRef<HTMLInputElement>(null)
  useDescriptionFocus(inputRef)
  const [open, setOpen] = useState(false)
  const [focused, setFocused] = useState(false)
  const mentions = useMentions(value, onChange, inputRef, catalog)
  const mentioning = mentions.matches.length > 0
  // The highlighted suggestion is remembered by what it is, not by position: if the list changes while the user
  // presses Enter, the entry that starts is still the one they saw highlighted, or none.
  const [activeKey, setActiveKey] = useState<string | null>(null)
  const search = useDebouncedValue(value.trim(), 150)
  const { data } = useQuery({ ...suggestionsQuery(workspaceId, search), enabled: open, placeholderData: keepPreviousData })

  // The server searches for the debounced text; until it answers, results for an older text are narrowed to what
  // is typed now, so the list never shows entries that do not match the field.
  const suggestions = useMemo(() => {
    const term = value.trim().toLowerCase()
    return (data ?? []).filter(
      (suggestion) =>
        !term ||
        suggestion.description.toLowerCase().includes(term) ||
        !!catalog.project(suggestion.projectId)?.name.toLowerCase().includes(term),
    )
  }, [data, value, catalog])

  const activeIndex = suggestions.findIndex((suggestion) => keyOf(suggestion) === activeKey)
  // Previous entries are offered for plain text; once a project is mentioned, where the time goes is shown instead.
  const visible = open && suggestions.length > 0 && !mentioning && !mentions.hasMentions

  function pick(suggestion: TimeEntrySuggestion) {
    setOpen(false)
    setActiveKey(null)
    onPick(suggestion)
  }

  function move(step: 1 | -1) {
    const next = activeIndex + step
    setActiveKey(next < 0 ? null : keyOf(suggestions[Math.min(next, suggestions.length - 1)]))
  }

  function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (mentions.onKeyDown(event)) {
      return
    }
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      setOpen(true)
      if (suggestions.length > 0) {
        move(1)
      }
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      move(-1)
    } else if (event.key === 'Escape') {
      setOpen(false)
      setActiveKey(null)
    } else if (event.key === 'Enter') {
      event.preventDefault()
      if (visible && activeIndex >= 0) {
        pick(suggestions[activeIndex])
      } else {
        setOpen(false)
        onSubmit()
      }
    }
  }

  return (
    <div className="relative min-w-0 flex-1">
      <MentionHighlights value={value} mentions={mentions} catalog={catalog} inputRef={inputRef} className="px-1 text-base leading-10" />
      <input
        ref={inputRef}
        role="combobox"
        aria-label="Description"
        aria-autocomplete="list"
        aria-expanded={visible || mentioning}
        aria-controls={listId}
        aria-activedescendant={mentioning ? `${listId}-${mentions.active}` : visible && activeIndex >= 0 ? `${listId}-${activeIndex}` : undefined}
        placeholder="What are you working on?"
        autoComplete="off"
        value={value}
        onChange={(event) => {
          onChange(event.target.value)
          setOpen(true)
          setActiveKey(null)
          mentions.track()
        }}
        onSelect={mentions.track}
        onFocus={() => {
          setOpen(true)
          setFocused(true)
        }}
        onBlur={() => {
          setOpen(false)
          setFocused(false)
        }}
        onKeyDown={handleKeyDown}
        className="placeholder:text-muted-foreground relative h-10 w-full bg-transparent px-1 text-base outline-none"
      />
      {mentioning && <MentionList id={listId} mentions={mentions} catalog={catalog} />}
      {!mentioning && focused && mentions.hasMentions && <MentionPreview result={mentions.apply(mentionBase)} catalog={catalog} />}
      {visible && (
        <ul
          id={listId}
          role="listbox"
          aria-label="Previous entries"
          className="bg-popover text-popover-foreground ring-foreground/10 absolute top-full left-0 z-50 mt-2 w-full max-w-xl overflow-hidden rounded-lg p-1 shadow-md ring-1"
        >
          {suggestions.map((suggestion, index) => {
            const project = catalog.project(suggestion.projectId)
            return (
              <li
                key={keyOf(suggestion)}
                id={`${listId}-${index}`}
                role="option"
                aria-selected={index === activeIndex}
                // Keeps focus in the field so the blur does not close the list before the click lands.
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => pick(suggestion)}
                onMouseEnter={() => setActiveKey(keyOf(suggestion))}
                className={cn('flex cursor-pointer items-center gap-3 rounded-md px-2 py-1.5 text-sm', index === activeIndex && 'bg-muted')}
              >
                <span className={cn('truncate', !suggestion.description && 'text-muted-foreground')}>
                  {suggestion.description || 'No description'}
                </span>
                {isSplit(suggestion) ? (
                  <SplitLabel catalog={catalog} parts={partsOf(suggestion)} className="ml-auto max-w-[55%] shrink-0" />
                ) : project && (
                  <ProjectLabel
                    project={project}
                    task={catalog.task(suggestion.taskId)}
                    client={catalog.client(project.clientId)}
                    className="ml-auto max-w-[55%] shrink-0"
                  />
                )}
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
