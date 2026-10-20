import { ApiError, isReadOnlyError } from '@/api/problem'
import { CONNECTION_ERROR } from '@/auth/form-errors'

/** A sentence for a toast: the first validation message, the problem the server reported, or a fallback. */
export function errorMessage(error: unknown, fallback: string): string {
  if (isReadOnlyError(error)) {
    return 'Nothing can be changed here: the trial or plan has ended.'
  }
  if (error instanceof ApiError) {
    const firstFieldError = Object.values(error.fieldErrors)[0]?.[0]
    return firstFieldError ?? error.problem?.detail ?? error.problem?.title ?? fallback
  }
  if (error instanceof TypeError) {
    return CONNECTION_ERROR
  }
  return fallback
}
