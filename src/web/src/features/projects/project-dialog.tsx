import { useQuery } from '@tanstack/react-query'
import { Check } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { BillingMode, Project } from '@/api/types'
import { CONNECTION_ERROR, hasErrors, toFormErrors, type FormErrors } from '@/auth/form-errors'
import { TextField } from '@/components/text-field'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { formatRate, parseAmount } from '@/lib/money'
import { nextProjectColor, PROJECT_COLORS } from '../catalog/colors'
import { clientsQuery, projectsQuery, useSaveClient, useSaveProject } from '../catalog/queries'
import { useRateInput } from '../rates/queries'
import { RateField } from '../rates/rate-field'
import { usePermissions } from '../settings/queries'

const FIELDS = ['name', 'clientId', 'color', 'estimateSeconds', 'hourlyRate', 'hourlyRateFrom', 'dailyHours', 'dailyHoursFrom'] as const
type Field = (typeof FIELDS)[number]

const BILLING_MODES = ['asTracked', 'perDay', 'fullDay'] as const satisfies readonly BillingMode[]

const BILLING_MODE_LABELS: Record<BillingMode, string> = {
  asTracked: 'As tracked',
  perDay: 'Entered day by day',
  fullDay: 'Full working day',
}

const BILLING_MODE_HINTS: Record<BillingMode, string> = {
  asTracked: 'The billable time tracked is what the client is billed.',
  perDay: 'Enter what the client is billed day by day, under Billing on the project. Days left empty are billed as tracked.',
  fullDay:
    'Every working day with time tracked on the project is billed as a full day. Weekends are billed as tracked. Change single days under Billing on the project.',
}

const NO_CLIENT = 'none'

interface ProjectDialogProps {
  workspaceId: string
  open: boolean
  onOpenChange: (open: boolean) => void
  /** The project to edit; without it a new project is created. */
  project?: Project
  initialName?: string
  onSaved?: (project: Project) => void
}

export function ProjectDialog({ open, onOpenChange, ...props }: ProjectDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">
        {/* Mounted only while open, so every opening starts from the project's current values. */}
        {open && <ProjectForm {...props} onDone={() => onOpenChange(false)} />}
      </DialogContent>
    </Dialog>
  )
}

function ProjectForm({ workspaceId, project, initialName = '', onSaved, onDone }: Omit<ProjectDialogProps, 'open' | 'onOpenChange'> & { onDone: () => void }) {
  const { data: clients = [] } = useQuery(clientsQuery(workspaceId, true))
  const { data: projects = [] } = useQuery(projectsQuery(workspaceId, true))
  const saveProject = useSaveProject(workspaceId)
  const saveClient = useSaveClient(workspaceId)
  const { settings, isAdmin, canCreateProjects } = usePermissions()

  const [name, setName] = useState(project?.name ?? initialName)
  const [clientId, setClientId] = useState(project?.clientId ?? NO_CLIENT)
  const [color, setColor] = useState(project?.color ?? nextProjectColor(projects.length))
  const [isBillable, setIsBillable] = useState(project?.isBillable ?? false)
  const [isPrivate, setIsPrivate] = useState(project?.isPrivate ?? false)
  const [billingMode, setBillingMode] = useState<BillingMode>(project?.billingMode ?? 'asTracked')
  const dailyHours = useRateInput(project?.billingMode === 'fullDay' ? project.dailyHours : null)
  const [estimate, setEstimate] = useState(project?.estimateSeconds ? String(+(project.estimateSeconds / 3600).toFixed(2)) : '')
  const hourlyRate = useRateInput(project?.hourlyRate)
  const [newClient, setNewClient] = useState<string | null>(null)
  const [errors, setErrors] = useState<FormErrors<Field>>({})

  // Archived clients are only offered when the project already belongs to one.
  const clientOptions = clients.filter((c) => !c.archivedAt || c.id === project?.clientId)

  async function addClient() {
    const clientName = newClient?.trim()
    if (!clientName) {
      return
    }
    try {
      const client = await saveClient.mutateAsync({ name: clientName })
      setClientId(client.id)
      setNewClient(null)
    } catch (error) {
      setErrors({ clientId: error instanceof ApiError ? (error.fieldErrors.name?.[0] ?? error.message) : CONNECTION_ERROR })
    }
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const hours = estimate.trim() ? Number(estimate.replace(',', '.')) : null
    const rate = parseAmount(hourlyRate.value)
    const day = parseAmount(dailyHours.value)
    const validation: FormErrors<Field> = {
      name: name.trim() ? undefined : 'Enter a name for the project.',
      estimateSeconds: hours !== null && !(hours > 0) ? 'Enter the estimate in hours, e.g. 40 or 12.5.' : undefined,
      hourlyRate: rate === undefined ? 'Enter an amount per hour, e.g. 60 or 72.50.' : undefined,
      dailyHours: billingMode === 'fullDay' && !(day != null && day > 0 && day <= 24) ? 'Enter how many hours a full day is, e.g. 8 or 7.5.' : undefined,
    }
    setErrors(validation)
    if (hasErrors(validation)) {
      return
    }

    try {
      const saved = await saveProject.mutateAsync({
        id: project?.id,
        body: {
          name: name.trim(),
          clientId: clientId === NO_CLIENT ? null : clientId,
          color,
          isBillable,
          isPrivate,
          billingMode,
          dailyHours: billingMode === 'fullDay' ? day : undefined,
          dailyHoursFrom: billingMode === 'fullDay' && project?.billingMode === 'fullDay' ? dailyHours.fromToSend : undefined,
          estimateSeconds: hours === null ? null : Math.round(hours * 3600),
          // Only admins see and set rates; for anyone else the server keeps the project's rate.
          hourlyRate: isAdmin ? (rate ?? null) : undefined,
          hourlyRateFrom: isAdmin ? hourlyRate.fromToSend : undefined,
        },
      })
      toast.success(project ? 'Project saved' : `Project “${saved.name}” created`)
      onSaved?.(saved)
      onDone()
    } catch (error) {
      setErrors(error instanceof ApiError ? toFormErrors(error, FIELDS) : { form: CONNECTION_ERROR })
    }
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>{project ? 'Edit project' : 'New project'}</DialogTitle>
        <DialogDescription>Projects group your time. Give each one a color to recognize it at a glance.</DialogDescription>
      </DialogHeader>

      {errors.form && (
        <Alert variant="destructive">
          <AlertDescription>{errors.form}</AlertDescription>
        </Alert>
      )}

      <TextField id="project-name" label="Name" value={name} onChange={(e) => setName(e.target.value)} autoFocus error={errors.name} />

      <div className="grid gap-2">
        <Label htmlFor="project-client">Client</Label>
        {newClient === null ? (
          <div className="flex gap-2">
            <Select value={clientId} onValueChange={setClientId}>
              <SelectTrigger id="project-client" className="flex-1" aria-invalid={errors.clientId ? true : undefined}>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NO_CLIENT}>No client</SelectItem>
                {clientOptions.map((client) => (
                  <SelectItem key={client.id} value={client.id}>
                    {client.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {canCreateProjects && (
              <Button type="button" variant="outline" onClick={() => setNewClient('')}>
                New client
              </Button>
            )}
          </div>
        ) : (
          <div className="flex gap-2">
            <Input
              aria-label="New client name"
              placeholder="Client name"
              value={newClient}
              autoFocus
              onChange={(e) => setNewClient(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault()
                  void addClient()
                }
              }}
            />
            <Button type="button" onClick={() => void addClient()} disabled={!newClient.trim() || saveClient.isPending}>
              Add
            </Button>
            <Button type="button" variant="ghost" onClick={() => setNewClient(null)}>
              Cancel
            </Button>
          </div>
        )}
        {errors.clientId && <p className="text-destructive text-sm">{errors.clientId}</p>}
      </div>

      <fieldset className="grid gap-2">
        <legend className="mb-2 text-sm font-medium">Color</legend>
        <div role="radiogroup" aria-label="Color" className="flex flex-wrap gap-2">
          {PROJECT_COLORS.map((swatch) => (
            <button
              key={swatch}
              type="button"
              role="radio"
              aria-checked={color === swatch}
              aria-label={swatch}
              onClick={() => setColor(swatch)}
              className="ring-offset-background focus-visible:ring-ring flex size-7 items-center justify-center rounded-full outline-none focus-visible:ring-2 focus-visible:ring-offset-2"
              style={{ backgroundColor: swatch }}
            >
              {color === swatch && <Check className="size-4 text-white" />}
            </button>
          ))}
        </div>
        {errors.color && <p className="text-destructive text-sm">{errors.color}</p>}
      </fieldset>

      <TextField
        id="project-estimate"
        label="Estimate (hours)"
        inputMode="decimal"
        placeholder="Optional"
        value={estimate}
        onChange={(e) => setEstimate(e.target.value)}
        error={errors.estimateSeconds}
      />

      {isAdmin && (
        <RateField
          id="project-rate"
          label={`Hourly rate (${settings.currency})`}
          currency={settings.currency}
          placeholder={settings.defaultHourlyRate == null ? 'Member rates' : `Member rates, or ${formatRate(settings.defaultHourlyRate, settings.currency)}`}
          value={hourlyRate.value}
          onChange={hourlyRate.setValue}
          saved={project?.hourlyRate}
          from={hourlyRate.from}
          onFromChange={hourlyRate.setFrom}
          scope={project ? { projectId: project.id } : undefined}
          error={errors.hourlyRate}
          fromError={errors.hourlyRateFrom}
          hint="What billable time on this project is worth. Rates set for people on the project come first."
        />
      )}

      <div className="grid gap-3">
        <SwitchRow id="project-billable" label="Billable" description="New time on this project is billable by default." checked={isBillable} onCheckedChange={setIsBillable} />
        <SwitchRow id="project-private" label="Private" description="Only project members and admins can see it." checked={isPrivate} onCheckedChange={setIsPrivate} />
      </div>

      <div className="grid gap-2">
        <Label htmlFor="project-billing-mode">Billed time</Label>
        <Select value={billingMode} onValueChange={(mode) => setBillingMode(mode as BillingMode)}>
          <SelectTrigger id="project-billing-mode" className="w-full">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {BILLING_MODES.map((mode) => (
              <SelectItem key={mode} value={mode}>
                {BILLING_MODE_LABELS[mode]}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <p className="text-muted-foreground text-xs">{BILLING_MODE_HINTS[billingMode]}</p>
      </div>

      {billingMode === 'fullDay' && (
        <RateField
          id="project-daily-hours"
          label="Hours in a full day"
          currency={settings.currency}
          placeholder="e.g. 8"
          value={dailyHours.value}
          onChange={dailyHours.setValue}
          saved={project?.billingMode === 'fullDay' ? project.dailyHours : null}
          from={dailyHours.from}
          onFromChange={dailyHours.setFrom}
          scope={project && isAdmin ? { kind: 'dailyHours', projectId: project.id } : undefined}
          format={(hours) => `${hours} h`}
          error={errors.dailyHours}
          fromError={errors.dailyHoursFrom}
          className="w-32"
        />
      )}

      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={saveProject.isPending}>
          {project ? 'Save' : 'Create project'}
        </Button>
      </DialogFooter>
    </form>
  )
}

function SwitchRow({
  id,
  label,
  description,
  checked,
  onCheckedChange,
}: {
  id: string
  label: string
  description: string
  checked: boolean
  onCheckedChange: (checked: boolean) => void
}) {
  return (
    <div className="flex items-start justify-between gap-4">
      <div className="grid gap-0.5">
        <Label htmlFor={id}>{label}</Label>
        <p className="text-muted-foreground text-xs">{description}</p>
      </div>
      <Switch id={id} checked={checked} onCheckedChange={onCheckedChange} />
    </div>
  )
}
