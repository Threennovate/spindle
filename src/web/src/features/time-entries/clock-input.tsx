import { useRef, useState, type ComponentProps } from 'react'
import { Input } from '@/components/ui/input'
import { addMinutes, formatClockField, type Clock } from '@/lib/time/clock'
import { readClockField } from '@/lib/time/zone'
import { useWorkspace } from '../workspace'

interface ClockInputProps extends Omit<ComponentProps<'input'>, 'value' | 'onChange'> {
  value: string
  onValueChange: (value: string) => void
  /** The field was left or Enter was pressed: the time typed, or null if it isn't one. */
  onCommit?: (clock: Clock | null) => void
}

/**
 * The field every clock time is typed in, so they all work alike:
 *
 * - entering it selects the time, so typing replaces it
 * - it reads `930`, `9:30`, `9.30`, `9pm`, `now`, or `-15` for 15 minutes ago, and tidies that to `09:30` when left
 * - ↑ and ↓ change the time by a minute, with Shift by 15
 * - a time it can't read is marked once the field is left
 */
export function ClockInput({ value, onValueChange, onCommit, onFocus, onBlur, onKeyDown, onMouseDown, onMouseUp, ...props }: ClockInputProps) {
  const { timeZone, hour12 } = useWorkspace()
  const inputRef = useRef<HTMLInputElement>(null)
  const clickedIn = useRef(false)
  const [left, setLeft] = useState(false)
  const unreadable = left && value.trim() !== '' && !readClockField(value, new Date(), timeZone)

  function commit() {
    const clock = readClockField(value, new Date(), timeZone)
    if (clock) {
      onValueChange(formatClockField(clock, hour12))
    }
    setLeft(true)
    onCommit?.(clock)
  }

  function nudge(minutes: number) {
    const clock = readClockField(value, new Date(), timeZone)
    if (clock) {
      onValueChange(formatClockField(addMinutes(clock, minutes), hour12))
      setLeft(false)
      // Stays selected, so typing still replaces the time.
      requestAnimationFrame(() => inputRef.current?.select())
    }
  }

  return (
    <Input
      ref={inputRef}
      value={value}
      placeholder={hour12 ? '9:30 am' : '9:30'}
      autoComplete="off"
      spellCheck={false}
      {...props}
      aria-invalid={props['aria-invalid'] ?? (unreadable || undefined)}
      onChange={(event) => {
        onValueChange(event.target.value)
        setLeft(false)
      }}
      onFocus={(event) => {
        event.currentTarget.select()
        onFocus?.(event)
      }}
      onMouseDown={(event) => {
        clickedIn.current = document.activeElement !== event.currentTarget
        onMouseDown?.(event)
      }}
      onMouseUp={(event) => {
        // The click that entered the field would otherwise put the caret where it landed, undoing the selection.
        if (clickedIn.current) {
          event.preventDefault()
          clickedIn.current = false
        }
        onMouseUp?.(event)
      }}
      onBlur={(event) => {
        commit()
        onBlur?.(event)
      }}
      onKeyDown={(event) => {
        if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
          event.preventDefault()
          nudge((event.key === 'ArrowUp' ? 1 : -1) * (event.shiftKey ? 15 : 1))
        } else if (event.key === 'Enter') {
          // Not prevented: a form still submits.
          commit()
        }
        onKeyDown?.(event)
      }}
    />
  )
}
