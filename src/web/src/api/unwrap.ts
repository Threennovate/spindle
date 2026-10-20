import { toApiError } from './problem'

/** The data of a successful call, or throws an ApiError with the server's problem details. */
export async function unwrap<T>(call: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await call
  if (!response.ok) {
    throw toApiError(response, error)
  }
  return data as T
}
