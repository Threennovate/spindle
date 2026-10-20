import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

interface InlineNameFormProps {
  label: string
  initialValue?: string
  submitLabel: string
  placeholder?: string
  /** Resolves to an error message, or null when saved. */
  onSubmit: (name: string) => Promise<string | null>
  onCancel?: () => void
  /** Focus the field on mount; by default only when renaming, which opens the form on purpose. */
  autoFocus?: boolean
}

/** A one-field form for naming or renaming something in place: a client, a tag, a task. */
export function InlineNameForm({ label, initialValue = '', submitLabel, placeholder, onSubmit, onCancel, autoFocus = !!initialValue }: InlineNameFormProps) {
  const [name, setName] = useState(initialValue)
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!name.trim()) {
      setError('Enter a name.')
      return
    }
    const submitted = name.trim()
    setPending(true)
    const failure = await onSubmit(submitted)
    setPending(false)
    setError(failure)
    if (!failure && !initialValue) {
      // Clear what was added, but keep anything typed while the request was on its way.
      setName((current) => (current.trim() === submitted ? '' : current))
    }
  }

  return (
    <form onSubmit={handleSubmit} className="grid gap-1">
      <div className="flex gap-2">
        <Input
          aria-label={label}
          placeholder={placeholder}
          value={name}
          autoFocus={autoFocus}
          onChange={(event) => setName(event.target.value)}
          onKeyDown={(event) => event.key === 'Escape' && onCancel?.()}
          aria-invalid={error ? true : undefined}
        />
        <Button type="submit" disabled={pending}>
          {submitLabel}
        </Button>
        {onCancel && (
          <Button type="button" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
        )}
      </div>
      {error && <p className="text-destructive text-sm">{error}</p>}
    </form>
  )
}
