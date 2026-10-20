import { queryOptions } from '@tanstack/react-query'
import { api } from '@/api/client'
import { toApiError } from '@/api/problem'

export const meQuery = queryOptions({
  queryKey: ['me'],
  queryFn: async () => {
    const { data, error, response } = await api.GET('/api/me')
    if (!data) {
      throw toApiError(response, error)
    }
    return data
  },
})
