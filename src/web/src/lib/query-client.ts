import { QueryClient } from '@tanstack/react-query'
import { ApiError } from '@/api/problem'

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      // Client errors will not fix themselves, except 401: the token is renewed before the retry.
      retry: (failureCount, error) => {
        const isClientError = error instanceof ApiError && error.status >= 400 && error.status < 500
        return failureCount < 2 && (!isClientError || error.status === 401)
      },
    },
  },
})
