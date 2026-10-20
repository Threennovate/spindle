import type { ComponentProps } from 'react'
import { ThreadDot } from '@/components/auth-threads'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

interface TextFieldProps extends ComponentProps<typeof Input> {
  id: string
  label: string
  error?: string
  hint?: string
  /** On the sign-in pages: the thread that runs into this field (auth/threads.ts). */
  thread?: string
}

/** A labelled input whose error or hint is announced by screen readers. */
export function TextField({ id, label, error, hint, thread, ...inputProps }: TextFieldProps) {
  const message = error ?? hint
  const messageId = message ? `${id}-message` : undefined

  return (
    <div className="grid gap-2">
      <Label htmlFor={id}>
        {thread && <ThreadDot port={thread} />}
        {label}
      </Label>
      <Input id={id} aria-invalid={error ? true : undefined} aria-describedby={messageId} data-thread={thread} {...inputProps} />
      {message && (
        <p id={messageId} className={error ? 'text-destructive text-sm' : 'text-muted-foreground text-sm'}>
          {message}
        </p>
      )}
    </div>
  )
}
