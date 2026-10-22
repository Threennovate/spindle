import { useNavigate } from '@tanstack/react-router'
import { Check, ChevronsUpDown, Plus } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { InlineNameForm } from '@/components/inline-name'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { describeNewWorkspace, useWorkspace } from '@/features/workspace'
import { useCreateWorkspace, useSwitchWorkspace } from '@/features/workspace-queries'
import { errorMessage } from '@/lib/error-message'

/** Shows the current workspace and switches to another one, or creates one. */
export function WorkspaceSwitcher({ onSwitched }: { onSwitched?: () => void }) {
  const { me, workspace, canCreateWorkspaces } = useWorkspace()
  const switchWorkspace = useSwitchWorkspace()
  const createWorkspace = useCreateWorkspace()
  const navigate = useNavigate()
  const [creating, setCreating] = useState(false)

  function done() {
    onSwitched?.()
    // Pages such as a project's belong to the old workspace, so start over on the timer.
    void navigate({ to: '/' })
  }

  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger className="hover:bg-sidebar-accent flex w-full items-center gap-2 rounded-md px-3 py-2 text-left outline-none focus-visible:ring-2 focus-visible:ring-ring">
          <span className="min-w-0 flex-1">
            <span className="text-muted-foreground block text-xs">Workspace</span>
            <span className="block truncate text-sm font-medium">{workspace.name}</span>
          </span>
          <ChevronsUpDown className="text-muted-foreground size-4 shrink-0" />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-60">
          <DropdownMenuLabel className="text-muted-foreground text-xs font-normal">Your workspaces</DropdownMenuLabel>
          {me.workspaces.map((option) => (
            <DropdownMenuItem
              key={option.id}
              onSelect={() => {
                if (option.id !== workspace.id) {
                  switchWorkspace.mutate(option.id, { onSuccess: done, onError: (error) => toast.error(errorMessage(error, "Couldn't switch workspaces.")) })
                }
              }}
            >
              <span className="flex-1 truncate">{option.name}</span>
              {option.id === workspace.id && <Check />}
            </DropdownMenuItem>
          ))}
          {canCreateWorkspaces && (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuItem onSelect={() => setCreating(true)}>
                <Plus />
                New workspace
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      <Dialog open={creating} onOpenChange={setCreating}>
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>New workspace</DialogTitle>
            <DialogDescription>{describeNewWorkspace(me, workspace)}</DialogDescription>
          </DialogHeader>
          <InlineNameForm
            label="Workspace name"
            placeholder="e.g. Consulting"
            submitLabel="Create"
            onSubmit={async (name) => {
              try {
                await createWorkspace.mutateAsync(name)
                setCreating(false)
                toast.success(`Switched to ${name}`)
                done()
                return null
              } catch (error) {
                return errorMessage(error, "Couldn't create the workspace.")
              }
            }}
          />
        </DialogContent>
      </Dialog>
    </>
  )
}
