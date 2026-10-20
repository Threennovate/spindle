import { useQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { Ellipsis, UserPlus } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { Project, ProjectMember } from '@/api/types'
import { CONNECTION_ERROR } from '@/auth/form-errors'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Command, CommandEmpty, CommandInput, CommandItem, CommandList } from '@/components/ui/command'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/error-message'
import { formatRate, parseAmount } from '@/lib/money'
import { matchKeywords } from '../catalog/use-catalog'
import { projectMembersQuery, useRemoveProjectMember, useSetProjectMember } from '../catalog/queries'
import { useRateInput } from '../rates/queries'
import { RateField } from '../rates/rate-field'
import { usePermissions } from '../settings/queries'
import { membersQuery } from '../team/queries'
import { useWorkspace } from '../workspace'

/** Who is on a project: they see it when it is private, managers run it, and admins can give them a rate for it. */
export function ProjectMembersCard({ project }: { project: Project }) {
  const { workspaceId } = useWorkspace()
  const members = useQuery(projectMembersQuery(workspaceId, project.id))
  const [rateFor, setRateFor] = useState<ProjectMember | null>(null)

  return (
    <Card>
      <CardHeader>
        <CardTitle>Members</CardTitle>
        <CardDescription>
          {project.isPrivate
            ? 'Only these people and admins see this private project and track time on it. Managers can change it and its tasks.'
            : 'Everyone in the workspace can track time on this project. Managers can change it and its tasks.'}
        </CardDescription>
        {project.canManage && (
          <CardAction>
            <AddMember project={project} onMembers={members.data ?? []} />
          </CardAction>
        )}
      </CardHeader>
      <CardContent>
        {members.isPending && <Skeleton className="h-10 w-full" />}
        {members.isError && <p className="text-muted-foreground text-sm">{errorMessage(members.error, "Couldn't load the members.")}</p>}
        {members.data?.length === 0 && <p className="text-muted-foreground text-sm">No one is on this project yet.</p>}
        {!!members.data?.length && (
          <ul aria-label="Project members" className="divide-y rounded-lg border">
            {members.data.map((member) => (
              <MemberRow key={member.userId} member={member} project={project} onSetRate={() => setRateFor(member)} />
            ))}
          </ul>
        )}
      </CardContent>
      <RateDialog projectId={project.id} member={rateFor} onOpenChange={(open) => !open && setRateFor(null)} />
    </Card>
  )
}

function AddMember({ project, onMembers }: { project: Project; onMembers: ProjectMember[] }) {
  const { workspaceId } = useWorkspace()
  const [open, setOpen] = useState(false)
  const workspaceMembers = useQuery({ ...membersQuery(workspaceId), enabled: open })
  const add = useSetProjectMember(workspaceId, project.id)

  const onProject = new Set(onMembers.map((m) => m.userId))
  const candidates = workspaceMembers.data?.filter((m) => !onProject.has(m.userId)) ?? []

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm">
          <UserPlus />
          Add people
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-72 p-0" align="end">
        <Command filter={matchKeywords}>
          <CommandInput placeholder="Search people" />
          <CommandList>
            <CommandEmpty>{workspaceMembers.isPending ? 'Loading…' : 'Everyone is already on this project.'}</CommandEmpty>
            {candidates.map((member) => (
              <CommandItem
                key={member.userId}
                value={member.userId}
                keywords={[member.fullName, member.email]}
                onSelect={() => {
                  setOpen(false)
                  add.mutate(
                    { userId: member.userId, body: { isManager: false } },
                    {
                      onSuccess: () => toast.success(`${member.fullName} added to ${project.name}`),
                      onError: (error) => toast.error(errorMessage(error, "Couldn't add them to the project.")),
                    },
                  )
                }}
              >
                <span className="min-w-0">
                  <span className="block truncate">{member.fullName}</span>
                  <span className="text-muted-foreground block truncate text-xs">{member.email}</span>
                </span>
              </CommandItem>
            ))}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  )
}

function MemberRow({ member, project, onSetRate }: { member: ProjectMember; project: Project; onSetRate: () => void }) {
  const { workspaceId, me } = useWorkspace()
  const { settings, isAdmin } = usePermissions()
  const navigate = useNavigate()
  const set = useSetProjectMember(workspaceId, project.id)
  const remove = useRemoveProjectMember(workspaceId, project.id)
  const [confirmLeave, setConfirmLeave] = useState(false)
  const isYou = member.userId === me.id

  function toggleManager() {
    set.mutate(
      { userId: member.userId, body: { isManager: !member.isManager, hourlyRate: member.hourlyRate } },
      { onError: (error) => toast.error(errorMessage(error, "Couldn't change their part in the project.")) },
    )
  }

  function removeMember() {
    remove.mutate(member.userId, {
      onSuccess: () => {
        toast.success(isYou ? `You left ${project.name}` : `${member.fullName} removed from ${project.name}`)
        // Without admin rights, leaving a private project means no longer seeing it.
        if (isYou && !isAdmin && project.isPrivate) {
          void navigate({ to: '/projects' })
        }
      },
      onError: (error) => toast.error(errorMessage(error, "Couldn't remove them from the project.")),
    })
  }

  return (
    <li className="flex items-center gap-3 px-3 py-2">
      <div className="min-w-0 flex-1">
        <p className="flex items-center gap-2 truncate text-sm font-medium">
          {member.fullName}
          {isYou && (
            <Badge variant="secondary" className="font-normal">
              You
            </Badge>
          )}
          {member.isManager && <Badge variant="outline">Manager</Badge>}
        </p>
        <p className="text-muted-foreground truncate text-xs">{member.email}</p>
      </div>
      {isAdmin && member.hourlyRate != null && <span className="text-sm tabular-nums">{formatRate(member.hourlyRate, settings.currency)}</span>}
      {project.canManage && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={`Actions for ${member.fullName} on the project`}>
              <Ellipsis />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={toggleManager}>{member.isManager ? 'Remove as manager' : 'Make manager'}</DropdownMenuItem>
            {isAdmin && <DropdownMenuItem onSelect={onSetRate}>Set rate on this project…</DropdownMenuItem>}
            <DropdownMenuSeparator />
            <DropdownMenuItem variant="destructive" onSelect={() => (isYou && !isAdmin ? setConfirmLeave(true) : removeMember())}>
              {isYou ? 'Leave project' : 'Remove from project'}
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      )}
      <ConfirmDialog
        open={confirmLeave}
        onOpenChange={setConfirmLeave}
        title={`Leave ${project.name}?`}
        description={
          project.isPrivate
            ? 'The project is private, so you will no longer see it. Your time on it stays.'
            : 'You will no longer manage this project. You can still track time on it.'
        }
        confirmLabel="Leave project"
        onConfirm={removeMember}
      />
    </li>
  )
}

function RateDialog({ projectId, member, onOpenChange }: { projectId: string; member: ProjectMember | null; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={member !== null} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-sm">{member && <RateForm projectId={projectId} member={member} onDone={() => onOpenChange(false)} />}</DialogContent>
    </Dialog>
  )
}

function RateForm({ projectId, member, onDone }: { projectId: string; member: ProjectMember; onDone: () => void }) {
  const { workspaceId } = useWorkspace()
  const { settings } = usePermissions()
  const set = useSetProjectMember(workspaceId, projectId)
  const rate = useRateInput(member.hourlyRate)
  const [error, setError] = useState<{ hourlyRate?: string; hourlyRateFrom?: string }>({})

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    const hourlyRate = parseAmount(rate.value)
    if (hourlyRate === undefined) {
      setError({ hourlyRate: 'Enter an amount per hour, e.g. 60 or 72.50.' })
      return
    }
    try {
      await set.mutateAsync({ userId: member.userId, body: { isManager: member.isManager, hourlyRate, hourlyRateFrom: rate.fromToSend } })
      toast.success(`Rate for ${member.fullName} saved`)
      onDone()
    } catch (failure) {
      setError(
        failure instanceof ApiError
          ? { hourlyRate: failure.fieldErrors.hourlyRate?.[0] ?? (failure.fieldErrors.hourlyRateFrom ? undefined : failure.message), hourlyRateFrom: failure.fieldErrors.hourlyRateFrom?.[0] }
          : { hourlyRate: CONNECTION_ERROR },
      )
    }
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>Rate for {member.fullName}</DialogTitle>
        <DialogDescription>Their billable time on this project is worth this, whatever their usual rate. Leave it empty to use the project's rate or, without one, their own.</DialogDescription>
      </DialogHeader>
      <RateField
        id="project-member-rate"
        label={`Hourly rate (${settings.currency})`}
        currency={settings.currency}
        placeholder="Project rate"
        value={rate.value}
        onChange={rate.setValue}
        saved={member.hourlyRate}
        from={rate.from}
        onFromChange={rate.setFrom}
        scope={{ projectId, userId: member.userId }}
        error={error.hourlyRate}
        fromError={error.hourlyRateFrom}
      />
      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={set.isPending}>
          Save rate
        </Button>
      </DialogFooter>
    </form>
  )
}
