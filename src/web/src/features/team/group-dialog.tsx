import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { Group, Member } from '@/api/types'
import { CONNECTION_ERROR, toFormErrors, type FormErrors } from '@/auth/form-errors'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useSaveGroup } from './queries'

const FIELDS = ['name', 'memberIds'] as const
type Field = (typeof FIELDS)[number]

/** Above this many people the list gets a filter. */
const FILTER_FROM = 8

interface GroupDialogProps {
  workspaceId: string
  members: Member[]
  open: boolean
  onOpenChange: (open: boolean) => void
  /** The group to edit; without it a new group is created. */
  group?: Group
}

export function GroupDialog({ open, onOpenChange, ...props }: GroupDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">{open && <GroupForm {...props} onDone={() => onOpenChange(false)} />}</DialogContent>
    </Dialog>
  )
}

function GroupForm({ workspaceId, members, group, onDone }: Omit<GroupDialogProps, 'open' | 'onOpenChange'> & { onDone: () => void }) {
  const save = useSaveGroup(workspaceId)
  const [name, setName] = useState(group?.name ?? '')
  const [memberIds, setMemberIds] = useState(() => new Set(group?.memberIds))
  const [filter, setFilter] = useState('')
  const [errors, setErrors] = useState<FormErrors<Field>>({})

  const needle = filter.trim().toLowerCase()
  const shown = needle ? members.filter((m) => m.fullName.toLowerCase().includes(needle) || m.email.toLowerCase().includes(needle)) : members

  function toggle(userId: string, checked: boolean) {
    setMemberIds((current) => {
      const next = new Set(current)
      if (checked) {
        next.add(userId)
      } else {
        next.delete(userId)
      }
      return next
    })
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!name.trim()) {
      setErrors({ name: 'Enter a name for the group.' })
      return
    }
    try {
      // People who left the workspace in the meantime are no longer offered, so they drop out here.
      const ids = members.filter((m) => memberIds.has(m.userId)).map((m) => m.userId)
      const saved = await save.mutateAsync({ id: group?.id, body: { name: name.trim(), memberIds: ids } })
      toast.success(group ? 'Group saved' : `Group “${saved.name}” created`)
      onDone()
    } catch (error) {
      setErrors(error instanceof ApiError ? toFormErrors(error, FIELDS) : { form: CONNECTION_ERROR })
    }
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>{group ? 'Edit group' : 'New group'}</DialogTitle>
        <DialogDescription>Groups bring people together, for example a department, to look at their time together in reports.</DialogDescription>
      </DialogHeader>

      {errors.form && (
        <Alert variant="destructive">
          <AlertDescription>{errors.form}</AlertDescription>
        </Alert>
      )}

      <TextField id="group-name" label="Name" value={name} onChange={(e) => setName(e.target.value)} autoFocus error={errors.name} />

      <fieldset className="grid gap-2">
        <legend className="mb-2 text-sm font-medium">
          Members <span className="text-muted-foreground font-normal">· {memberIds.size} selected</span>
        </legend>
        {members.length > FILTER_FROM && <Input aria-label="Filter people" placeholder="Filter people" value={filter} onChange={(e) => setFilter(e.target.value)} />}
        <ul className="max-h-64 divide-y overflow-y-auto rounded-lg border">
          {shown.map((member) => (
            <li key={member.userId}>
              <Label htmlFor={`group-member-${member.userId}`} className="hover:bg-muted/50 flex cursor-pointer items-center gap-3 px-3 py-2 font-normal">
                <Checkbox id={`group-member-${member.userId}`} checked={memberIds.has(member.userId)} onCheckedChange={(checked) => toggle(member.userId, checked === true)} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate">{member.fullName}</span>
                  <span className="text-muted-foreground block truncate text-xs">{member.email}</span>
                </span>
              </Label>
            </li>
          ))}
          {shown.length === 0 && <li className="text-muted-foreground px-3 py-4 text-center text-sm">No one matches “{filter.trim()}”.</li>}
        </ul>
        {errors.memberIds && <p className="text-destructive text-sm">{errors.memberIds}</p>}
      </fieldset>

      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={save.isPending}>
          {group ? 'Save' : 'Create group'}
        </Button>
      </DialogFooter>
    </form>
  )
}
