import { useQuery } from '@tanstack/react-query'
import { Ellipsis } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import type { Client } from '@/api/types'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { InlineNameForm } from '@/components/inline-name'
import { PageHeader } from '@/components/page-header'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { errorMessage } from '@/lib/error-message'
import { usePageTitle } from '@/lib/title'
import { usePersistentState } from '@/lib/use-persistent-state'
import { clientsQuery, useArchiveClient, useDeleteClient, useSaveClient } from '../catalog/queries'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'

const STATUSES = ['active', 'archived'] as const

export function ClientsPage() {
  usePageTitle('Clients')
  const { workspaceId } = useWorkspace()
  const { canCreateProjects, canManageClients } = usePermissions()
  const query = useQuery(clientsQuery(workspaceId, true))
  const save = useSaveClient(workspaceId)
  const [status, setStatus] = usePersistentState('spindle:clients-status', 'active', STATUSES)

  const clients = (query.data ?? []).filter((client) => (status === 'archived') === !!client.archivedAt)

  async function add(name: string) {
    try {
      await save.mutateAsync({ name })
      return null
    } catch (error) {
      return errorMessage(error, "Couldn't add the client.")
    }
  }

  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <PageHeader title="Clients" description="The people and companies you work for. Assign them to projects." />

      {canCreateProjects && (
        <div className="bg-card grid gap-4 rounded-xl border p-4">
          <InlineNameForm label="New client name" placeholder="Add a client" submitLabel="Add client" onSubmit={add} />
        </div>
      )}

      <Tabs value={status} onValueChange={(value) => setStatus(value as (typeof STATUSES)[number])}>
        <TabsList>
          <TabsTrigger value="active">Active</TabsTrigger>
          <TabsTrigger value="archived">Archived</TabsTrigger>
        </TabsList>
      </Tabs>

      <div className="bg-card overflow-hidden rounded-xl border">
        {query.isPending ? (
          <div className="grid gap-2 p-4">
            <Skeleton className="h-8 w-full" />
            <Skeleton className="h-8 w-full" />
          </div>
        ) : query.isError ? (
          <p className="text-muted-foreground p-8 text-center text-sm">{errorMessage(query.error, "Couldn't load clients.")}</p>
        ) : clients.length === 0 ? (
          <p className="text-muted-foreground p-8 text-center text-sm">
            {status === 'archived' ? 'No archived clients.' : canCreateProjects ? 'No clients yet. Add the first one above.' : 'No clients yet.'}
          </p>
        ) : (
          <ul className="divide-y">
            {clients.map((client) => (
              <ClientRow key={client.id} client={client} workspaceId={workspaceId} canManage={canManageClients} />
            ))}
          </ul>
        )}
      </div>
    </div>
  )
}

function ClientRow({ client, workspaceId, canManage }: { client: Client; workspaceId: string; canManage: boolean }) {
  const save = useSaveClient(workspaceId)
  const archive = useArchiveClient(workspaceId)
  const remove = useDeleteClient(workspaceId)
  const [renaming, setRenaming] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)

  if (renaming) {
    return (
      <li className="p-3">
        <InlineNameForm
          label="Client name"
          initialValue={client.name}
          submitLabel="Save"
          onCancel={() => setRenaming(false)}
          onSubmit={async (name) => {
            try {
              await save.mutateAsync({ id: client.id, name })
              setRenaming(false)
              return null
            } catch (error) {
              return errorMessage(error, "Couldn't rename the client.")
            }
          }}
        />
      </li>
    )
  }

  return (
    <li className="flex items-center gap-3 px-4 py-3">
      <span className="min-w-0 flex-1 truncate font-medium">{client.name}</span>
      {client.archivedAt && <Badge variant="outline">Archived</Badge>}
      <span className="text-muted-foreground text-sm">
        {client.activeProjectCount} {client.activeProjectCount === 1 ? 'project' : 'projects'}
      </span>
      {canManage && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={`Actions for ${client.name}`}>
              <Ellipsis />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={() => setRenaming(true)}>Rename</DropdownMenuItem>
            <DropdownMenuItem
              onSelect={() =>
                archive.mutate(
                  { id: client.id, archive: !client.archivedAt },
                  {
                    onSuccess: () => toast.success(client.archivedAt ? `“${client.name}” restored` : `“${client.name}” archived`),
                    onError: (error) => toast.error(errorMessage(error, "Couldn't change the client.")),
                  },
                )
              }
            >
              {client.archivedAt ? 'Restore' : 'Archive'}
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem variant="destructive" onSelect={() => setConfirmDelete(true)}>
              Delete
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      )}
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete “${client.name}”?`}
        description="Its projects stay, without a client."
        confirmLabel="Delete client"
        onConfirm={() =>
          remove.mutate(client.id, {
            onSuccess: () => toast.success(`“${client.name}” deleted`),
            onError: (error) => toast.error(errorMessage(error, "Couldn't delete the client.")),
          })
        }
      />
    </li>
  )
}
