import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { Project } from '@/api/types'
import { CONNECTION_ERROR, hasErrors, toFormErrors, type FormErrors } from '@/auth/form-errors'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { amountText, parseAmount } from '@/lib/money'
import { dayKey } from '@/lib/time/zone'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { useSaveAgreement, type Agreement, type AgreementKind } from './queries'

const FIELDS = ['name', 'kind', 'amount', 'hours', 'period', 'startsOn', 'endsOn', 'taskIds'] as const
type Field = (typeof FIELDS)[number]

type Period = 'month' | 'year'

/** The chosen kind stands out, as it decides what the rest of the form asks for. */
const KIND_ITEM = 'data-[state=on]:bg-primary data-[state=on]:text-primary-foreground data-[state=on]:border-primary flex-1'

const KIND_HINTS: Record<AgreementKind, string> = {
  fixedPrice: 'A price for a piece of work, e.g. building a website. Mark it done when it is, and later work no longer counts against it.',
  retainer: 'A fee each month or year, e.g. for maintenance. Hours included start again each period; unused ones don’t carry over.',
}

interface AgreementDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  project: Project
  /** The project's agreements, to tell which tasks already belong to one. */
  agreements: Agreement[]
  /** The agreement to edit; without it a new one is added. */
  agreement?: Agreement
}

export function AgreementDialog({ open, onOpenChange, ...props }: AgreementDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        {/* Mounted only while open, so every opening starts from the agreement as saved. */}
        {open && <AgreementForm {...props} onDone={() => onOpenChange(false)} />}
      </DialogContent>
    </Dialog>
  )
}

function AgreementForm({ project, agreements, agreement, onDone }: Omit<AgreementDialogProps, 'open' | 'onOpenChange'> & { onDone: () => void }) {
  const { workspaceId, timeZone } = useWorkspace()
  const { settings } = usePermissions()
  const save = useSaveAgreement(workspaceId, project.id)
  const today = dayKey(new Date(), timeZone)

  const [kind, setKind] = useState<AgreementKind>(agreement?.kind ?? 'fixedPrice')
  const [name, setName] = useState(agreement?.name ?? '')
  const [amount, setAmount] = useState(amountText(agreement?.amount))
  const [hours, setHours] = useState(amountText(agreement?.hours))
  const [period, setPeriod] = useState<Period>(agreement?.period ?? 'month')
  const [startsOn, setStartsOn] = useState(agreement?.startsOn ?? today)
  const [endsOn, setEndsOn] = useState(agreement?.endsOn ?? '')
  const [billsOverage, setBillsOverage] = useState(agreement?.billsOverage ?? false)
  const [taskIds, setTaskIds] = useState<string[]>(agreement?.taskIds ?? [])
  const [createTask, setCreateTask] = useState(true)
  const [errors, setErrors] = useState<FormErrors<Field>>({})

  const retainer = kind === 'retainer'
  const ownerOf = (taskId: string) => agreements.find((a) => a.id !== agreement?.id && a.taskIds.includes(taskId))
  // Open tasks can be added; tasks it has stay listed, done or not.
  const tasks = project.tasks.filter((task) => !task.isDone || taskIds.includes(task.id))
  const sameNameTask = project.tasks.find((task) => task.name.toLowerCase() === name.trim().toLowerCase())

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const money = parseAmount(amount)
    const time = parseAmount(hours)
    const validation: FormErrors<Field> = {
      name: name.trim() ? undefined : 'Enter a name, e.g. Website build or Maintenance.',
      amount:
        money === undefined
          ? 'Enter an amount, e.g. 2400 or 300.'
          : !retainer && money === null
            ? 'Enter the price agreed.'
            : retainer && money === null && time === null
              ? 'Enter the fee, the hours included, or both.'
              : undefined,
      hours: time === undefined || time === 0 ? 'Enter the hours as a number, e.g. 5 or 7.5.' : undefined,
      startsOn: startsOn ? undefined : 'Choose the day it starts.',
      endsOn: endsOn && endsOn < startsOn ? (retainer ? 'It ends on or after the day it starts.' : 'It is done on or after the day it starts.') : undefined,
    }
    setErrors(validation)
    if (hasErrors(validation)) {
      return
    }

    try {
      const saved = await save.mutateAsync({
        id: agreement?.id,
        body: {
          name: name.trim(),
          kind,
          amount: money ?? null,
          hours: time ?? null,
          period: retainer ? period : null,
          startsOn,
          endsOn: endsOn || null,
          billsOverage: retainer && billsOverage,
          taskIds,
          createTask: !agreement && createTask,
        },
      })
      toast.success(agreement ? 'Agreement saved' : `“${saved.name}” added`)
      onDone()
    } catch (error) {
      setErrors(error instanceof ApiError ? toFormErrors(error, FIELDS) : { form: CONNECTION_ERROR })
    }
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>{agreement ? 'Edit agreement' : 'New agreement'}</DialogTitle>
        <DialogDescription>What the client pays for part of {project.name}. Time tracked on its tasks counts towards it.</DialogDescription>
      </DialogHeader>

      {errors.form && (
        <Alert variant="destructive">
          <AlertDescription>{errors.form}</AlertDescription>
        </Alert>
      )}

      <div className="grid gap-2">
        <ToggleGroup
          type="single"
          variant="outline"
          value={kind}
          onValueChange={(value) => value && setKind(value as AgreementKind)}
          aria-label="Kind of agreement"
          className="w-full"
        >
          <ToggleGroupItem value="fixedPrice" className={KIND_ITEM}>
            Fixed price
          </ToggleGroupItem>
          <ToggleGroupItem value="retainer" className={KIND_ITEM}>
            Retainer
          </ToggleGroupItem>
        </ToggleGroup>
        <p className="text-muted-foreground text-xs">{KIND_HINTS[kind]}</p>
      </div>

      <TextField
        id="agreement-name"
        label="Name"
        placeholder={retainer ? 'Maintenance' : 'Website build'}
        value={name}
        onChange={(e) => setName(e.target.value)}
        autoFocus
        error={errors.name}
      />

      <div className="grid items-start gap-4 sm:grid-cols-2">
        <div className="grid content-start gap-2">
          <Label htmlFor="agreement-amount">{retainer ? `Fee (${settings.currency})` : `Price (${settings.currency})`}</Label>
          <div className="flex gap-2">
            <Input
              id="agreement-amount"
              inputMode="decimal"
              placeholder={retainer ? 'Optional' : '2400'}
              value={amount}
              onChange={(e) => setAmount(e.target.value)}
              aria-invalid={errors.amount ? true : undefined}
              aria-describedby={errors.amount ? 'agreement-amount-message' : undefined}
              className="min-w-0"
            />
            {retainer && (
              <Select value={period} onValueChange={(value) => setPeriod(value as Period)}>
                <SelectTrigger aria-label="Paid" className="w-32 shrink-0">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="month">a month</SelectItem>
                  <SelectItem value="year">a year</SelectItem>
                </SelectContent>
              </Select>
            )}
          </div>
          {errors.amount && (
            <p id="agreement-amount-message" className="text-destructive text-sm">
              {errors.amount}
            </p>
          )}
        </div>
        <TextField
          id="agreement-hours"
          label={retainer ? `Hours included a ${period}` : 'Hours quoted'}
          inputMode="decimal"
          placeholder="Optional"
          value={hours}
          onChange={(e) => setHours(e.target.value)}
          error={errors.hours}
          hint={retainer && parseAmount(amount) === null ? 'Without a fee, they’re paid at the project’s hourly rate.' : undefined}
        />
      </div>

      {retainer && (
        <div className="flex items-start justify-between gap-4">
          <div className="grid gap-0.5">
            <Label htmlFor="agreement-overage">Bill hours over the limit</Label>
            <p className="text-muted-foreground text-xs">Time beyond the hours included is billed on top, at the hourly rates.</p>
          </div>
          <Switch id="agreement-overage" checked={billsOverage} onCheckedChange={setBillsOverage} disabled={parseAmount(hours) == null} />
        </div>
      )}

      <div className="grid items-start gap-4 sm:grid-cols-2">
        <TextField id="agreement-starts" label="Starts on" type="date" value={startsOn} onChange={(e) => setStartsOn(e.target.value)} error={errors.startsOn} />
        <TextField
          id="agreement-ends"
          label={retainer ? 'Ends on' : 'Done on'}
          type="date"
          max={retainer ? undefined : today}
          value={endsOn}
          onChange={(e) => setEndsOn(e.target.value)}
          error={errors.endsOn}
          hint={retainer ? 'Leave empty while it goes on.' : 'Leave empty while it’s in progress.'}
        />
      </div>

      <fieldset className="grid gap-2">
        <legend className="mb-1 text-sm font-medium">Tasks</legend>
        <p className="text-muted-foreground text-xs">Time tracked on these tasks counts towards the agreement. The timer offers them under {project.name}.</p>
        {!agreement && (
          <label className="flex items-center gap-2 text-sm">
            <Checkbox checked={createTask} onCheckedChange={(checked) => setCreateTask(checked === true)} />
            {sameNameTask ? `Use the task “${sameNameTask.name}”` : `Create a task named “${name.trim() || (retainer ? 'Maintenance' : 'Website build')}”`}
          </label>
        )}
        {tasks.length > 0 && (
          <ul className="grid max-h-48 gap-1 overflow-y-auto rounded-md border p-2">
            {tasks
              .filter((task) => agreement || !createTask || task.id !== sameNameTask?.id)
              .map((task) => {
                const owner = ownerOf(task.id)
                return (
                  <li key={task.id}>
                    <label className="flex items-center gap-2 text-sm">
                      <Checkbox
                        checked={taskIds.includes(task.id)}
                        onCheckedChange={(checked) => setTaskIds((ids) => (checked === true ? [...ids, task.id] : ids.filter((id) => id !== task.id)))}
                      />
                      <span className="truncate">{task.name}</span>
                      {owner && <span className="text-muted-foreground ml-auto shrink-0 text-xs">moves from {owner.name}</span>}
                    </label>
                  </li>
                )
              })}
          </ul>
        )}
        {errors.taskIds && <p className="text-destructive text-sm">{errors.taskIds}</p>}
      </fieldset>

      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={save.isPending}>
          {agreement ? 'Save' : 'Add agreement'}
        </Button>
      </DialogFooter>
    </form>
  )
}
