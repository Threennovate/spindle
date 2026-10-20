import { useQuery } from '@tanstack/react-query'
import { serverQuery } from '@/features/subscriptions/queries'

/** Spindle's own repository, for servers from before `/api/server` said where their source is. */
export const SPINDLE_SOURCE_URL = 'https://github.com/Threennovate/spindle'

/**
 * Where to get the source of the version this server runs. The AGPL asks a server to offer it to everyone who uses
 * it, so a server running a fork names the fork's repository.
 */
export function useSourceUrl(): string {
  return useQuery(serverQuery).data?.sourceUrl ?? SPINDLE_SOURCE_URL
}
