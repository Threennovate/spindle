import type { components } from './schema'
import type { TimeEntry } from './types'

type ProblemDetails = components['schemas']['ProblemDetails']
type ValidationProblemDetails = components['schemas']['HttpValidationProblemDetails']

/** A failed API call, carrying the RFC 9457 problem details the server sent, if any. */
export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails | undefined

  constructor(status: number, problem?: ProblemDetails) {
    super(problem?.title ?? `Request failed with status ${status}.`)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
  }

  /** Validation errors keyed by JSON property name, e.g. `{ email: ['…'] }`. */
  get fieldErrors(): Record<string, string[]> {
    return (this.problem as ValidationProblemDetails | undefined)?.errors ?? {}
  }
}

export function toApiError(response: Response, body: unknown): ApiError {
  return new ApiError(response.status, isProblem(body) ? body : undefined)
}

function isProblem(body: unknown): body is ProblemDetails {
  return typeof body === 'object' && body !== null && ('title' in body || 'status' in body)
}

/** The `type` of the problem the API answers with when an organization is read-only. */
export const READ_ONLY_PROBLEM = 'urn:spindle:problem:read-only'

/** A change refused because the organization's trial or plan has ended. */
export function isReadOnlyError(error: unknown): boolean {
  return error instanceof ApiError && error.status === 402 && error.problem?.type === READ_ONLY_PROBLEM
}

/** The `type` of the problem the API answers with when an entry changed since the version a write names. */
export const ENTRY_CHANGED_PROBLEM = 'urn:spindle:problem:entry-changed'

/**
 * The entry as it is now, when a write was refused because it changed elsewhere since the version the web loaded,
 * e.g. on another device while the editor was open; null for any other error.
 */
export function entryChangedBy(error: unknown): TimeEntry | null {
  if (!(error instanceof ApiError) || error.status !== 412 || error.problem?.type !== ENTRY_CHANGED_PROBLEM) {
    return null
  }
  return (error.problem as { current?: TimeEntry }).current ?? null
}
