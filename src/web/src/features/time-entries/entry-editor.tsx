import { useEffect, useState } from 'react'
import { EntryDialog } from './entry-dialog'
import { EDIT_ENTRY_EVENT, type EditEntryRequest } from './events'

/** The entry dialog for requests from anywhere in the app; see `editEntry`. */
export function EntryEditor() {
  const [request, setRequest] = useState<EditEntryRequest | null>(null)

  useEffect(() => {
    const open = (event: Event) => setRequest((event as CustomEvent<EditEntryRequest>).detail)
    window.addEventListener(EDIT_ENTRY_EVENT, open)
    return () => window.removeEventListener(EDIT_ENTRY_EVENT, open)
  }, [])

  return <EntryDialog entry={request?.entry ?? null} newEntry={request?.newEntry} stopping={request?.stopping} split={request?.split} onClose={() => setRequest(null)} />
}
