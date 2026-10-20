import { useQuery } from '@tanstack/react-query'
import { ChevronDown, Folder, Tag as TagIcon, Trash2, X } from 'lucide-react'
import { useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import type { components } from '@/api/schema'
import type { TimeEntry } from '@/api/types'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { Button } from '@/components/ui/button'
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { errorMessage } from '@/lib/error-message'
import { ProjectPicker } from '../catalog/project-picker'
import { tagsQuery } from '../catalog/queries'
import { matchKeywords } from '../catalog/use-catalog'
import { useBulkDeleteEntries, useBulkUpdateEntries } from './queries'

type BulkChanges = Omit<components['schemas']['BulkUpdateTimeEntriesRequest'], 'ids'>

interface BulkActionsProps {
  workspaceId: string
  selected: TimeEntry[]
  onDeleted: () => void
  onClose: () => void
}

/** The bar shown while entries are selected: change their project, tags or billable status, or delete them. */
export function BulkActions({ workspaceId, selected, onDeleted, onClose }: BulkActionsProps) {
  const bulkUpdate = useBulkUpdateEntries(workspaceId)
  const bulkDelete = useBulkDeleteEntries(workspaceId)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const ids = selected.map((entry) => entry.id)
  const count = `${ids.length} ${ids.length === 1 ? 'entry' : 'entries'}`
  const tagsOnSelection = [...new Set(selected.flatMap((entry) => entry.tagIds))]

  function apply(changes: BulkChanges, done: string) {
    bulkUpdate.mutate(
      { ids, ...changes },
      {
        onSuccess: () => toast.success(`${done} ${count}`),
        onError: (error) => toast.error(errorMessage(error, "Couldn't change the entries.")),
      },
    )
  }

  return (
    <div
      role="toolbar"
      aria-label="Selected entries"
      className="bg-popover text-popover-foreground ring-foreground/10 sticky bottom-4 z-30 mx-auto flex w-fit max-w-full flex-wrap items-center gap-1 rounded-xl p-2 shadow-lg ring-1"
    >
      <span className="px-2 text-sm font-medium">{ids.length} selected</span>
      <ProjectPicker workspaceId={workspaceId} projectId={null} taskId={null} onChange={(projectId, taskId) => apply({ project: { projectId, taskId } }, 'Moved')}>
        <Button variant="ghost" size="sm" disabled={!ids.length}>
          <Folder />
          Project
        </Button>
      </ProjectPicker>
      <TagAction workspaceId={workspaceId} label="Add tag" onPick={(tagId) => apply({ addTagIds: [tagId] }, 'Tagged')}>
        <Button variant="ghost" size="sm" disabled={!ids.length}>
          <TagIcon />
          Add tag
        </Button>
      </TagAction>
      <TagAction workspaceId={workspaceId} label="Remove tag" only={tagsOnSelection} onPick={(tagId) => apply({ removeTagIds: [tagId] }, 'Untagged')}>
        <Button variant="ghost" size="sm" disabled={!tagsOnSelection.length}>
          Remove tag
        </Button>
      </TagAction>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="sm" disabled={!ids.length}>
            Billable
            <ChevronDown />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent>
          <DropdownMenuItem onSelect={() => apply({ isBillable: true }, 'Marked billable:')}>Mark billable</DropdownMenuItem>
          <DropdownMenuItem onSelect={() => apply({ isBillable: false }, 'Marked non-billable:')}>Mark non-billable</DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
      <Button variant="ghost" size="sm" className="text-destructive hover:text-destructive" disabled={!ids.length} onClick={() => setConfirmDelete(true)}>
        <Trash2 />
        Delete
      </Button>
      <Button variant="ghost" size="icon-sm" aria-label="Stop selecting" onClick={onClose}>
        <X />
      </Button>

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete ${count}?`}
        description="The selected time entries and the time on them will be removed."
        confirmLabel={`Delete ${count}`}
        onConfirm={() =>
          bulkDelete.mutate(ids, {
            onSuccess: () => {
              toast.success(`Deleted ${count}`)
              onDeleted()
            },
            onError: (error) => toast.error(errorMessage(error, "Couldn't delete the entries.")),
          })
        }
      />
    </div>
  )
}

function TagAction({
  workspaceId,
  label,
  only,
  onPick,
  children,
}: {
  workspaceId: string
  label: string
  only?: string[]
  onPick: (tagId: string) => void
  children: ReactNode
}) {
  const [open, setOpen] = useState(false)
  const { data: tags = [] } = useQuery(tagsQuery(workspaceId))
  const offered = only ? tags.filter((tag) => only.includes(tag.id)) : tags

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>{children}</PopoverTrigger>
      <PopoverContent className="w-60 p-0" side="top">
        <Command filter={matchKeywords}>
          <CommandInput placeholder={`${label}…`} />
          <CommandList>
            <CommandEmpty>No tags.</CommandEmpty>
            <CommandGroup>
              {offered.map((tag) => (
                <CommandItem
                  key={tag.id}
                  value={tag.id}
                  keywords={[tag.name]}
                  onSelect={() => {
                    setOpen(false)
                    onPick(tag.id)
                  }}
                >
                  {tag.name}
                </CommandItem>
              ))}
            </CommandGroup>
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  )
}
