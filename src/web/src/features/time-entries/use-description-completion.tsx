import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { cn } from 'cn'
import { useId, useMemo, useState, type KeyboardEvent } from 'react'
import { useDebouncedValue } from '@/lib/use-debounced-value'
import { descriptionsQuery } from './queries'

interface CompletionOptions {
  workspaceId: string
  value: string
  /** Descriptions written on this project come first. */
  projectId?: string | null
  /** Replaces the field's text with the picked description; nothing else about the entry changes. */
  onPick: (description: string) => void
  /** Off while something else, such as the list of projects to mention, owns the field's keys. */
  disabled?: boolean
}

/**
 * Offers descriptions written before while a description is typed in the entry editor. Unlike the timer's field,
 * picking one only fills in the text: the entry's project, task and split stay as they are. The list opens as the
 * person types (or presses ↓), arrow keys move through it, Enter or Tab takes the highlighted one and Escape closes it.
 */
export function useDescriptionCompletion({ workspaceId, value, projectId, onPick, disabled }: CompletionOptions) {
  const listId = useId()
  const [open, setOpen] = useState(false)
  // Remembered by what it is, not by position, so Enter takes what was highlighted even if the list just changed.
  const [active, setActive] = useState<string | null>(null)
  const search = useDebouncedValue(value.trim(), 150)
  const { data } = useQuery({ ...descriptionsQuery(workspaceId, search, projectId), enabled: open && !disabled, placeholderData: keepPreviousData })

  // Until the server answers for the latest text, older results are narrowed to what is typed now.
  const options = useMemo(() => {
    const term = value.trim().toLowerCase()
    return (data ?? []).filter((text) => text.toLowerCase().includes(term) && text.toLowerCase() !== term)
  }, [data, value])
  const visible = open && !disabled && options.length > 0
  const activeIndex = visible ? options.indexOf(active ?? '') : -1

  function close() {
    setOpen(false)
    setActive(null)
  }

  function pick(text: string) {
    close()
    onPick(text)
  }

  /** True when the key was the list's, so the caller leaves it alone. */
  function onKeyDown(event: KeyboardEvent<HTMLInputElement>): boolean {
    if (disabled) {
      return false
    }
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      setOpen(true)
      if (options.length > 0) {
        setActive(options[Math.min(activeIndex + 1, options.length - 1)])
      }
      return true
    }
    if (!visible) {
      return false
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault()
      setActive(activeIndex <= 0 ? null : options[activeIndex - 1])
    } else if ((event.key === 'Enter' || event.key === 'Tab') && activeIndex >= 0) {
      event.preventDefault()
      pick(options[activeIndex])
    } else if (event.key === 'Escape') {
      event.preventDefault()
      close()
    } else {
      return false
    }
    return true
  }

  return {
    visible,
    onKeyDown,
    /** Call when the person types in the field. */
    onTyped: () => {
      setOpen(true)
      setActive(null)
    },
    onBlur: close,
    inputProps: {
      role: 'combobox' as const,
      'aria-autocomplete': 'list' as const,
      'aria-expanded': visible,
      'aria-activedescendant': activeIndex >= 0 ? `${listId}-${activeIndex}` : undefined,
      'data-completing': visible || undefined,
    },
    listId,
    list: visible ? (
      <ul
        id={listId}
        role="listbox"
        aria-label="Earlier descriptions"
        className="bg-popover text-popover-foreground ring-foreground/10 absolute top-full left-0 z-50 mt-1 w-full overflow-hidden rounded-lg p-1 shadow-md ring-1"
      >
        {options.map((text, index) => (
          <li
            key={text}
            id={`${listId}-${index}`}
            role="option"
            aria-selected={index === activeIndex}
            // Keeps focus in the field so the blur does not close the list before the click lands.
            onMouseDown={(event) => event.preventDefault()}
            onClick={() => pick(text)}
            onMouseEnter={() => setActive(text)}
            className={cn('cursor-pointer truncate rounded-md px-2 py-1.5 text-sm', index === activeIndex && 'bg-muted')}
          >
            {text}
          </li>
        ))}
      </ul>
    ) : null,
  }
}
