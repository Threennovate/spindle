import { useQuery } from '@tanstack/react-query'
import { Ellipsis, Plus, UserPlus } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import type { Group, Invitation, Member, SentInvitation, WorkspaceRole } from '@/api/types'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { PageHeader } from '@/components/page-header'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { errorMessage } from '@/lib/error-message'
import { formatRate } from '@/lib/money'
import { dayKey, formatDayAs } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { GroupDialog } from './group-dialog'
import { InvitationSent, InviteDialog } from './invite-dialog'
import { MemberRatesDialog } from './member-rates-dialog'
import {
  groupsQuery,
  invitationsQuery,
  membersQuery,
  useDeleteGroup,
  useRemoveMember,
  useResendInvitation,
  useRevokeInvitation,
  useUpdateMember,
} from './queries'


export function TeamPage() {
  usePageTitle('Team')
  const { workspaceId, workspace, isAdmin, me } = useWorkspace()
  const members = useQuery(membersQuery(workspaceId))
  const invitations = useQuery({ ...invitationsQuery(workspaceId), enabled: isAdmin })
  const [inviting, setInviting] = useState(false)
  const [resent, setResent] = useState<SentInvitation | null>(null)
  const [editingRates, setEditingRates] = useState<Member | null>(null)
  const columns = isAdmin ? 7 : 4

  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <PageHeader
        title="Team"
        description={`People in ${workspace.name}. Admins manage the team, projects and settings; members track their own time.`}
        actions={
          isAdmin && (
            <Button onClick={() => setInviting(true)}>
              <UserPlus />
              Invite people
            </Button>
          )
        }
      />

      <section aria-labelledby="members-heading" className="grid gap-3">
        <h2 id="members-heading" className="text-sm font-medium">
          Members {members.data && <span className="text-muted-foreground">· {members.data.length}</span>}
        </h2>
        <div className="bg-card overflow-hidden rounded-xl border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="pl-4">Name</TableHead>
                <TableHead className="hidden md:table-cell">Email</TableHead>
                <TableHead>Role</TableHead>
                {isAdmin && <TableHead className="hidden text-right sm:table-cell">Billable rate</TableHead>}
                {isAdmin && <TableHead className="hidden text-right sm:table-cell">Labor cost</TableHead>}
                <TableHead className="hidden lg:table-cell">Joined</TableHead>
                {isAdmin && <TableHead className="w-12" />}
              </TableRow>
            </TableHeader>
            <TableBody>
              {members.isPending && (
                <TableRow>
                  <TableCell colSpan={columns} className="px-4">
                    <Skeleton className="h-6 w-full" />
                  </TableCell>
                </TableRow>
              )}
              {members.isError && (
                <TableRow>
                  <TableCell colSpan={columns} className="text-muted-foreground py-8 text-center">
                    {errorMessage(members.error, "Couldn't load the team.")}
                  </TableCell>
                </TableRow>
              )}
              {members.data?.map((member) => (
                <MemberRow
                  key={member.userId}
                  member={member}
                  workspaceId={workspaceId}
                  isAdmin={isAdmin}
                  isYou={member.userId === me.id}
                  onEditRates={() => setEditingRates(member)}
                />
              ))}
            </TableBody>
          </Table>
        </div>
      </section>

      <GroupsSection workspaceId={workspaceId} members={members.data ?? []} isAdmin={isAdmin} />

      {isAdmin && (
        <section aria-labelledby="invitations-heading" className="grid gap-3">
          <h2 id="invitations-heading" className="text-sm font-medium">
            Pending invitations {invitations.data && <span className="text-muted-foreground">· {invitations.data.length}</span>}
          </h2>
          <div className="bg-card overflow-hidden rounded-xl border">
            {invitations.data?.length === 0 ? (
              <p className="text-muted-foreground p-6 text-center text-sm">No pending invitations.</p>
            ) : (
              <ul className="divide-y">
                {invitations.data?.map((invitation) => (
                  <InvitationRow key={invitation.id} invitation={invitation} workspaceId={workspaceId} onResent={setResent} />
                ))}
              </ul>
            )}
          </div>
        </section>
      )}

      <InviteDialog workspaceId={workspaceId} workspaceName={workspace.name} open={inviting} onOpenChange={setInviting} />
      <MemberRatesDialog member={editingRates} onOpenChange={(open) => !open && setEditingRates(null)} />
      <Dialog open={resent !== null} onOpenChange={(open) => !open && setResent(null)}>
        <DialogContent className="sm:max-w-md">{resent && <InvitationSent sent={resent} onDone={() => setResent(null)} />}</DialogContent>
      </Dialog>
    </div>
  )
}

interface MemberRowProps {
  member: Member
  workspaceId: string
  isAdmin: boolean
  isYou: boolean
  onEditRates: () => void
}

function MemberRow({ member, workspaceId, isAdmin, isYou, onEditRates }: MemberRowProps) {
  const { timeZone, dateFormat } = useWorkspace()
  const { settings } = usePermissions()
  const update = useUpdateMember(workspaceId)
  const remove = useRemoveMember(workspaceId)
  const [confirmRemove, setConfirmRemove] = useState(false)

  return (
    <TableRow>
      <TableCell className="pl-4 font-medium">
        {member.fullName}
        {isYou && (
          <Badge variant="secondary" className="ml-2 font-normal">
            You
          </Badge>
        )}
        <p className="text-muted-foreground text-xs font-normal md:hidden">{member.email}</p>
      </TableCell>
      <TableCell className="text-muted-foreground hidden md:table-cell">{member.email}</TableCell>
      <TableCell>
        {isAdmin ? (
          <Select
            value={member.role}
            onValueChange={(role) =>
              update.mutate(
                { userId: member.userId, role: role as WorkspaceRole, hourlyRate: member.hourlyRate, laborCost: member.laborCost },
                { onError: (error) => toast.error(errorMessage(error, "Couldn't change the role.")) },
              )
            }
          >
            <SelectTrigger aria-label={`Role of ${member.fullName}`} className="h-8 w-28">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="admin">Admin</SelectItem>
              <SelectItem value="member">Member</SelectItem>
            </SelectContent>
          </Select>
        ) : (
          <span className="capitalize">{member.role}</span>
        )}
      </TableCell>
      {isAdmin && (
        <TableCell className="hidden text-right tabular-nums sm:table-cell">
          {member.hourlyRate == null ? <span className="text-muted-foreground">Default</span> : formatRate(member.hourlyRate, settings.currency)}
        </TableCell>
      )}
      {isAdmin && (
        <TableCell className="hidden text-right tabular-nums sm:table-cell">
          {member.laborCost == null ? <span className="text-muted-foreground">—</span> : formatRate(member.laborCost, settings.currency)}
        </TableCell>
      )}
      <TableCell className="text-muted-foreground hidden lg:table-cell">{formatDayAs(dayKey(member.joinedAt, timeZone), dateFormat)}</TableCell>
      {isAdmin && (
        <TableCell className="pr-2 text-right">
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" aria-label={`Actions for ${member.fullName}`}>
                <Ellipsis />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem onSelect={onEditRates}>Edit rates…</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem variant="destructive" onSelect={() => setConfirmRemove(true)}>
                Remove from workspace
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
          <ConfirmDialog
            open={confirmRemove}
            onOpenChange={setConfirmRemove}
            title={`Remove ${member.fullName}?`}
            description="They lose access to this workspace. The time they tracked here stays in reports."
            confirmLabel="Remove"
            onConfirm={() =>
              remove.mutate(member.userId, {
                onSuccess: () => toast.success(`${member.fullName} removed`),
                onError: (error) => toast.error(errorMessage(error, "Couldn't remove the member.")),
              })
            }
          />
        </TableCell>
      )}
    </TableRow>
  )
}

function GroupsSection({ workspaceId, members, isAdmin }: { workspaceId: string; members: Member[]; isAdmin: boolean }) {
  const groups = useQuery(groupsQuery(workspaceId))
  const [editing, setEditing] = useState<Group | 'new' | null>(null)

  return (
    <section aria-labelledby="groups-heading" className="grid gap-3">
      <div className="flex items-center justify-between gap-4">
        <h2 id="groups-heading" className="text-sm font-medium">
          Groups {groups.data && <span className="text-muted-foreground">· {groups.data.length}</span>}
        </h2>
        {isAdmin && (
          <Button variant="outline" size="sm" onClick={() => setEditing('new')}>
            <Plus />
            New group
          </Button>
        )}
      </div>
      <div className="bg-card overflow-hidden rounded-xl border">
        {groups.isPending && <Skeleton className="m-4 h-6" />}
        {groups.isError && <p className="text-muted-foreground p-6 text-center text-sm">{errorMessage(groups.error, "Couldn't load the groups.")}</p>}
        {groups.data?.length === 0 && (
          <p className="text-muted-foreground p-6 text-center text-sm">
            No groups yet.{isAdmin && ' Group people, for example by department, to look at their time together in reports.'}
          </p>
        )}
        {!!groups.data?.length && (
          <ul className="divide-y">
            {groups.data.map((group) => (
              <GroupRow key={group.id} group={group} members={members} workspaceId={workspaceId} isAdmin={isAdmin} onEdit={() => setEditing(group)} />
            ))}
          </ul>
        )}
      </div>
      <GroupDialog
        workspaceId={workspaceId}
        members={members}
        open={editing !== null}
        onOpenChange={(open) => !open && setEditing(null)}
        group={editing === 'new' || editing === null ? undefined : editing}
      />
    </section>
  )
}

function GroupRow({ group, members, workspaceId, isAdmin, onEdit }: { group: Group; members: Member[]; workspaceId: string; isAdmin: boolean; onEdit: () => void }) {
  const remove = useDeleteGroup(workspaceId)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const names = members.filter((m) => group.memberIds.includes(m.userId)).map((m) => m.fullName)

  return (
    <li className="flex items-center gap-3 px-4 py-3">
      <div className="min-w-0 flex-1">
        <p className="truncate font-medium">{group.name}</p>
        <p className="text-muted-foreground truncate text-xs">
          {group.memberIds.length === 0 ? 'No members' : `${group.memberIds.length} ${group.memberIds.length === 1 ? 'member' : 'members'} · ${names.join(', ')}`}
        </p>
      </div>
      {isAdmin && (
        <>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon" aria-label={`Actions for the group ${group.name}`}>
                <Ellipsis />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem onSelect={onEdit}>Edit</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem variant="destructive" onSelect={() => setConfirmDelete(true)}>
                Delete
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
          <ConfirmDialog
            open={confirmDelete}
            onOpenChange={setConfirmDelete}
            title={`Delete the group ${group.name}?`}
            description="Its members stay in the workspace, and their time is kept."
            confirmLabel="Delete"
            onConfirm={() =>
              remove.mutate(group.id, {
                onSuccess: () => toast.success(`Group “${group.name}” deleted`),
                onError: (error) => toast.error(errorMessage(error, "Couldn't delete the group.")),
              })
            }
          />
        </>
      )}
    </li>
  )
}

function InvitationRow({ invitation, workspaceId, onResent }: { invitation: Invitation; workspaceId: string; onResent: (sent: SentInvitation) => void }) {
  const { timeZone, dateFormat } = useWorkspace()
  const resend = useResendInvitation(workspaceId)
  const revoke = useRevokeInvitation(workspaceId)

  return (
    <li className="flex flex-wrap items-center gap-3 px-4 py-3">
      <div className="min-w-0 flex-1">
        <p className="truncate font-medium">{invitation.email}</p>
        <p className="text-muted-foreground text-xs">
          <span className="capitalize">{invitation.role}</span> · invited by {invitation.invitedBy} ·{' '}
          {invitation.isExpired ? <span className="text-destructive">expired</span> : `expires ${formatDayAs(dayKey(invitation.expiresAt, timeZone), dateFormat)}`}
        </p>
      </div>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="ghost" size="icon" aria-label={`Actions for the invitation to ${invitation.email}`}>
            <Ellipsis />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem
            onSelect={() =>
              resend.mutate(invitation.id, {
                onSuccess: onResent,
                onError: (error) => toast.error(errorMessage(error, "Couldn't send the invitation again.")),
              })
            }
          >
            Send again
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuItem
            variant="destructive"
            onSelect={() =>
              revoke.mutate(invitation.id, {
                onSuccess: () => toast.success(`Invitation to ${invitation.email} withdrawn`),
                onError: (error) => toast.error(errorMessage(error, "Couldn't withdraw the invitation.")),
              })
            }
          >
            Withdraw
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </li>
  )
}
