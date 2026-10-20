import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { setSyncConnection } from '@/api/sync-connection'
import { session } from '@/auth/session'
import { useWorkspace } from '../workspace'
import { applyChange, type SyncChange } from './changes'

const RETRY_MS = 10_000

/**
 * Keeps the screen current while other devices and teammates make changes: the server says what changed, and the
 * affected data loads again. Without a connection the app works as before, and catches up when it reconnects.
 */
export function LiveSync() {
  const queryClient = useQueryClient()
  const { me, workspaceId } = useWorkspace()
  const connectionRef = useRef<HubConnection | null>(null)
  const watched = useRef<string | null>(null)
  const current = useRef({ userId: me.id, workspaceId })

  useEffect(() => {
    current.current = { userId: me.id, workspaceId }
  })

  useEffect(() => {
    let stopped = false
    let retry: number | undefined
    const connection = new HubConnectionBuilder()
      .withUrl('/api/hubs/sync', { accessTokenFactory: async () => (await session.getAccessToken()) ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.None)
      .build()
    connectionRef.current = connection

    connection.on('Changed', (change: SyncChange) => applyChange(queryClient, change, current.current.userId))
    connection.onreconnecting(() => setSyncConnection(null))
    connection.onreconnected(async (id) => {
      setSyncConnection(id ?? null)
      watched.current = null
      await watch(connection, current.current.workspaceId, watched)
      // News may have been missed while the connection was down.
      await queryClient.invalidateQueries()
    })
    connection.onclose(() => {
      setSyncConnection(null)
      if (!stopped) {
        retry = window.setTimeout(() => void start(), RETRY_MS)
      }
    })

    async function start() {
      try {
        await connection.start()
        if (stopped) {
          return
        }
        setSyncConnection(connection.connectionId)
        watched.current = null
        await watch(connection, current.current.workspaceId, watched)
      } catch {
        // The server may be restarting, or the network gone. Try again in a while.
        if (!stopped) {
          retry = window.setTimeout(() => void start(), RETRY_MS)
        }
      }
    }

    void start()
    return () => {
      stopped = true
      window.clearTimeout(retry)
      setSyncConnection(null)
      connectionRef.current = null
      void connection.stop()
    }
  }, [queryClient])

  // Follows whichever workspace is on screen.
  useEffect(() => {
    const connection = connectionRef.current
    if (connection?.state === HubConnectionState.Connected) {
      void watch(connection, workspaceId, watched)
    }
  }, [workspaceId])

  return null
}

async function watch(connection: HubConnection, workspaceId: string, watched: { current: string | null }) {
  if (watched.current === workspaceId) {
    return
  }
  try {
    if (watched.current) {
      await connection.invoke('Unwatch', watched.current)
    }
    watched.current = (await connection.invoke<boolean>('Watch', workspaceId)) ? workspaceId : null
  } catch {
    // Reconnecting watches again.
  }
}
