/**
 * The id of this tab's live sync connection. API requests carry it, so the server leaves this tab out when it
 * tells the apps about the change: this tab updated itself already.
 */
let connectionId: string | null = null

export function currentSyncConnection(): string | null {
  return connectionId
}

export function setSyncConnection(id: string | null) {
  connectionId = id
}
