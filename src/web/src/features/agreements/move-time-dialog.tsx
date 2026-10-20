import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { Project } from '@/api/types'
import { CONNECTION_ERROR, hasErrors, toFormErrors, type FormErrors } from '@/auth/form-errors'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { dayKey } from '@/lib/time/zone'
import { useWorkspace } from '../workspace'
import { useMoveTimeToAgreement, type Agreement } from './queries'

const FIELDS = ['from', 'to', 'taskId'] as const
type Field = (typeof FIELDS)[number]

interface MoveTimeDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  project: Project
  agreement: Agreement
}

/** Moves the project's time without a task, e.g. tracked before the agreement was added, onto the agreement. */
export function MoveTimeDialog({ open, onOpenChange, ...props }: MoveTimeDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">{open && <MoveTimeForm {...props} onDone={() => onOpenChange(false)} />}</DialogContent>
    </Dialog>
  )
}

function MoveTimeForm({ project, agreement, onDone }: Omit<MoveTimeDialogProps, 'open' | 'onOpenChange'> & { onDone: () => void }) {
  const { workspaceId, timeZone } = useWorkspace()
  const move = useMoveTimeToAgreement(workspaceId, project.id)
  const today = dayKey(new Date(), timeZone)
  const [from, setFrom] = useState(agreement.startsOn)
  const [to, setTo] = useState(agreement.endsOn ?? today)
  const [errors, setErrors] = useState<FormErrors<Field>>({})

  const tasks = project.tasks.filter((task) => agreement.taskIds.includes(task.id))
  const target = tasks.find((task) => task.name.toLowerCase() === agreement.name.toLowerCase()) ?? [...tasks].sort((a, b) => a.name.localeCompare(b.name))[0]

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const validation: FormErrors<Field> = {
      from: from ? undefined : 'Choose the first day.',
      to: !to ? 'Choose the last day.' : to < from ? 'Choose a last day on or after the first one.' : undefined,
    }
    setErrors(validation)
    if (hasErrors(validation)) {
      return
    }

    try {
      const { count } = await move.mutateAsync({ agreementId: agreement.id, from, to })
      toast.success(count === 0 ? 'No time without a task on those days' : `${count} ${count === 1 ? 'entry' : 'entries'} moved to “${target?.name}”`)
      onDone()
    } catch (error) {
      setErrors(error instanceof ApiError ? toFormErrors(error, FIELDS) : { form: CONNECTION_ERROR })
    }
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>Move earlier time to {agreement.name}</DialogTitle>
        <DialogDescription>
          {target
            ? `Time tracked on ${project.name} without a task, on these days, moves to the task “${target.name}” and counts towards the agreement. Time on other tasks stays where it is.`
            : 'The agreement has no task to move time to yet. Edit it to choose one.'}
        </DialogDescription>
      </DialogHeader>

      {(errors.form || errors.taskId) && (
        <Alert variant="destructive">
          <AlertDescription>{errors.form ?? errors.taskId}</AlertDescription>
        </Alert>
      )}

      <div className="grid gap-4 sm:grid-cols-2">
        <TextField id="move-from" label="From" type="date" value={from} max={to || undefined} onChange={(e) => setFrom(e.target.value)} error={errors.from} />
        <TextField id="move-to" label="To" type="date" value={to} min={from || undefined} onChange={(e) => setTo(e.target.value)} error={errors.to} />
      </div>

      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={move.isPending || !target}>
          Move time
        </Button>
      </DialogFooter>
    </form>
  )
}
