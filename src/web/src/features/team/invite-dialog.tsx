import { Check, Copy } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { ApiError } from '@/api/problem'
import type { SentInvitation, WorkspaceRole } from '@/api/types'
import { CONNECTION_ERROR, EMAIL_PATTERN } from '@/auth/form-errors'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useInvite } from './queries'

export function InviteDialog({ workspaceId, workspaceName, open, onOpenChange }: { workspaceId: string; workspaceName: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">{open && <InviteForm workspaceId={workspaceId} workspaceName={workspaceName} onDone={() => onOpenChange(false)} />}</DialogContent>
    </Dialog>
  )
}

function InviteForm({ workspaceId, workspaceName, onDone }: { workspaceId: string; workspaceName: string; onDone: () => void }) {
  const invite = useInvite(workspaceId)
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<WorkspaceRole>('member')
  const [error, setError] = useState<string | null>(null)
  const [sent, setSent] = useState<SentInvitation | null>(null)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!EMAIL_PATTERN.test(email.trim())) {
      setError('Enter a valid email address.')
      return
    }
    try {
      setSent(await invite.mutateAsync({ email: email.trim(), role }))
      setError(null)
    } catch (failure) {
      setError(failure instanceof ApiError ? (failure.fieldErrors.email?.[0] ?? failure.message) : CONNECTION_ERROR)
    }
  }

  if (sent) {
    return <InvitationSent sent={sent} onInviteAnother={() => { setSent(null); setEmail('') }} onDone={onDone} />
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>Invite to {workspaceName}</DialogTitle>
        <DialogDescription>They get an email with a link to join. It is valid for 14 days.</DialogDescription>
      </DialogHeader>
      <TextField id="invite-email" label="Email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoFocus error={error ?? undefined} />
      <div className="grid gap-2">
        <Label htmlFor="invite-role">Role</Label>
        <Select value={role} onValueChange={(value) => setRole(value as WorkspaceRole)}>
          <SelectTrigger id="invite-role">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="member">Member: tracks their own time</SelectItem>
            <SelectItem value="admin">Admin: also manages the team and settings</SelectItem>
          </SelectContent>
        </Select>
      </div>
      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={invite.isPending}>
          Send invitation
        </Button>
      </DialogFooter>
    </form>
  )
}

/** After inviting: confirms the email went out, or offers the link to share another way. */
export function InvitationSent({ sent, onInviteAnother, onDone }: { sent: SentInvitation; onInviteAnother?: () => void; onDone: () => void }) {
  const [copied, setCopied] = useState(false)

  async function copy() {
    await navigator.clipboard.writeText(sent.inviteUrl)
    setCopied(true)
  }

  return (
    <div className="grid gap-5">
      <DialogHeader>
        <DialogTitle>{sent.emailSent ? 'Invitation sent' : 'Share the invitation link'}</DialogTitle>
        <DialogDescription>
          {sent.emailSent
            ? `We emailed ${sent.invitation.email} a link to join. You can also share the link yourself.`
            : `The email to ${sent.invitation.email} could not be sent. Send them this link another way; it works the same.`}
        </DialogDescription>
      </DialogHeader>
      {!sent.emailSent && (
        <Alert>
          <AlertDescription>Check the email settings of the server if this keeps happening.</AlertDescription>
        </Alert>
      )}
      <div className="flex gap-2">
        <Input readOnly value={sent.inviteUrl} aria-label="Invitation link" onFocus={(e) => e.currentTarget.select()} />
        <Button type="button" variant="outline" onClick={() => void copy()} aria-label="Copy invitation link">
          {copied ? <Check /> : <Copy />}
          {copied ? 'Copied' : 'Copy'}
        </Button>
      </div>
      <DialogFooter>
        {onInviteAnother && (
          <Button type="button" variant="ghost" onClick={onInviteAnother}>
            Invite someone else
          </Button>
        )}
        <Button type="button" onClick={onDone}>
          Done
        </Button>
      </DialogFooter>
    </div>
  )
}
