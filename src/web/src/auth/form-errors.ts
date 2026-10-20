import { ApiError } from '@/api/problem'

/** Per-field messages, plus `form` for errors that belong to no single field. */
export type FormErrors<Field extends string> = Partial<Record<Field | 'form', string>>

export const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/

export const CONNECTION_ERROR = "Couldn't reach Spindle. Check your connection and try again."

/** Turns a failed call into form errors: validation errors go next to their fields, anything else on top. */
export function toFormErrors<Field extends string>(error: ApiError, fields: readonly Field[]): FormErrors<Field> {
  if (error.status === 400) {
    const errors: FormErrors<Field> = {}
    for (const [key, messages] of Object.entries(error.fieldErrors)) {
      const field = fields.includes(key as Field) ? (key as Field) : 'form'
      errors[field] ??= messages[0]
    }
    return errors
  }

  const detail = error.problem?.detail
  const title = error.problem?.title ?? 'Something went wrong. Please try again.'
  return { form: detail ? `${title} ${detail}` : title } as FormErrors<Field>
}

export function hasErrors(errors: object): boolean {
  return Object.values(errors).some(Boolean)
}
