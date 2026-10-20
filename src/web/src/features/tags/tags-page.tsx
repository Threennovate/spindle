import { useQuery } from '@tanstack/react-query'
import { Ellipsis } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import type { Tag } from '@/api/types'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { InlineNameForm } from '@/components/inline-name'
import { PageHeader } from '@/components/page-header'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/error-message'
import { usePageTitle } from '@/lib/title'
import { tagsQuery, useDeleteTag, useSaveTag } from '../catalog/queries'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'

export function TagsPage() {
  usePageTitle('Tags')
  const { workspaceId } = useWorkspace()
  const { canCreateTags, canManageTags } = usePermissions()
  const query = useQuery(tagsQuery(workspaceId))
  const save = useSaveTag(workspaceId)

  async function add(name: string) {
    try {
      await save.mutateAsync({ name })
      return null
    } catch (error) {
      return errorMessage(error, "Couldn't add the tag.")
    }
  }

  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <PageHeader title="Tags" description="Labels that cut across projects, such as meeting, review or overtime." />

      {canCreateTags && (
        <div className="bg-card grid gap-4 rounded-xl border p-4">
          <InlineNameForm label="New tag name" placeholder="Add a tag" submitLabel="Add tag" onSubmit={add} />
        </div>
      )}

      <div className="bg-card overflow-hidden rounded-xl border">
        {query.isPending ? (
          <div className="grid gap-2 p-4">
            <Skeleton className="h-8 w-full" />
            <Skeleton className="h-8 w-full" />
          </div>
        ) : query.isError ? (
          <p className="text-muted-foreground p-8 text-center text-sm">{errorMessage(query.error, "Couldn't load tags.")}</p>
        ) : query.data.length === 0 ? (
          <p className="text-muted-foreground p-8 text-center text-sm">{canCreateTags ? 'No tags yet. Add one above, or create tags while tracking.' : 'No tags yet.'}</p>
        ) : (
          <ul className="divide-y">
            {query.data.map((tag) => (
              <TagRow key={tag.id} tag={tag} workspaceId={workspaceId} canManage={canManageTags} />
            ))}
          </ul>
        )}
      </div>
    </div>
  )
}

function TagRow({ tag, workspaceId, canManage }: { tag: Tag; workspaceId: string; canManage: boolean }) {
  const save = useSaveTag(workspaceId)
  const remove = useDeleteTag(workspaceId)
  const [renaming, setRenaming] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)

  if (renaming) {
    return (
      <li className="p-3">
        <InlineNameForm
          label="Tag name"
          initialValue={tag.name}
          submitLabel="Save"
          onCancel={() => setRenaming(false)}
          onSubmit={async (name) => {
            try {
              await save.mutateAsync({ id: tag.id, name })
              setRenaming(false)
              return null
            } catch (error) {
              return errorMessage(error, "Couldn't rename the tag.")
            }
          }}
        />
      </li>
    )
  }

  return (
    <li className="flex items-center gap-3 px-4 py-3">
      <span className="min-w-0 flex-1 truncate font-medium">{tag.name}</span>
      {canManage && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={`Actions for ${tag.name}`}>
              <Ellipsis />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={() => setRenaming(true)}>Rename</DropdownMenuItem>
            <DropdownMenuItem variant="destructive" onSelect={() => setConfirmDelete(true)}>
              Delete
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      )}
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete the tag “${tag.name}”?`}
        description="It is removed from every time entry that has it. The time itself stays."
        confirmLabel="Delete tag"
        onConfirm={() =>
          remove.mutate(tag.id, {
            onSuccess: () => toast.success(`“${tag.name}” deleted`),
            onError: (error) => toast.error(errorMessage(error, "Couldn't delete the tag.")),
          })
        }
      />
    </li>
  )
}
