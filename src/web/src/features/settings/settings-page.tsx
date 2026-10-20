import { Link } from '@tanstack/react-router'
import { useState, type FormEvent, type ReactNode } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { WorkspaceSettings } from '@/api/types'
import { CONNECTION_ERROR } from '@/auth/form-errors'
import { InlineNameForm } from '@/components/inline-name'
import { PageHeader } from '@/components/page-header'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { usePageTitle } from '@/lib/title'
import { rateChanged, useRateChangeStart } from '../rates/queries'
import { RateField } from '../rates/rate-field'
import { useReadOnly } from '../subscriptions/read-only'
import { useWorkspace } from '../workspace'
import { useRenameOrganization } from '../workspace-queries'
import { usePermissions, useUpdateSettings } from './queries'

const CURRENCIES = ['EUR', 'USD', 'GBP', 'CHF', 'BAM', 'RSD', 'HUF', 'PLN', 'CZK', 'RON', 'BGN', 'SEK', 'NOK', 'DKK', 'CAD', 'AUD', 'NZD', 'JPY']

export function SettingsPage() {
  usePageTitle('Settings')
  const { settings, isAdmin } = usePermissions()

  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <PageHeader title="Workspace settings" description="How time is kept in this workspace: money, permissions, locked days and required details." />
      {!isAdmin && (
        <Alert>
          <AlertDescription>Only workspace admins can change these settings.</AlertDescription>
        </Alert>
      )}
      <OrganizationName />
      <SettingsForm key={JSON.stringify(settings)} settings={settings} readOnly={!isAdmin} />
      {isAdmin && (
        <Card>
          <CardHeader>
            <CardTitle>Import time</CardTitle>
            <CardDescription>Bring time tracked in Toggl Track, Clockify or Harvest into this workspace, from their detailed CSV reports.</CardDescription>
          </CardHeader>
          <CardContent>
            <Button asChild variant="outline">
              <Link to="/import">Import from a CSV file</Link>
            </Button>
          </CardContent>
        </Card>
      )}
    </div>
  )
}

/** The organization's name, for its owners and admins: invitations, the plan and the workspace switcher show it. */
function OrganizationName() {
  const { organization, canManage } = useReadOnly()
  const rename = useRenameOrganization()
  if (!organization || !canManage) return null

  return (
    <Card>
      <CardHeader>
        <CardTitle>Organization</CardTitle>
        <CardDescription>The company or team behind this workspace. Invitations and the plan show its name.</CardDescription>
      </CardHeader>
      <CardContent className="max-w-md">
        <InlineNameForm
          key={organization.name}
          label="Organization name"
          initialValue={organization.name}
          submitLabel="Rename"
          autoFocus={false}
          onSubmit={async (name) => {
            try {
              await rename.mutateAsync({ organizationId: organization.id, name })
              toast.success('Organization renamed')
              return null
            } catch (error) {
              return error instanceof ApiError ? (error.fieldErrors.name?.[0] ?? error.message) : CONNECTION_ERROR
            }
          }}
        />
      </CardContent>
    </Card>
  )
}

function SettingsForm({ settings, readOnly }: { settings: WorkspaceSettings; readOnly: boolean }) {
  const { workspaceId } = useWorkspace()
  const update = useUpdateSettings(workspaceId)
  const [values, setValues] = useState({ ...settings, defaultHourlyRate: settings.defaultHourlyRate?.toString() ?? '' })
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [rateFrom, setRateFrom] = useState<string | null>(useRateChangeStart().firstOfMonth)

  const set =<K extends keyof typeof values>(key: K, value: (typeof values)[K]) => setValues((current) => ({ ...current, [key]: value }))
  const currencies = CURRENCIES.includes(values.currency) ? CURRENCIES : [values.currency, ...CURRENCIES]

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    const rate = values.defaultHourlyRate.trim() === '' ? null : Number(values.defaultHourlyRate.replace(',', '.'))
    if (rate !== null && !(rate >= 0)) {
      setErrors({ defaultHourlyRate: 'Enter an amount per hour, e.g. 60 or 72.50.' })
      return
    }
    try {
      await update.mutateAsync({
        name: values.name.trim(),
        currency: values.currency,
        defaultHourlyRate: rate,
        defaultHourlyRateFrom: rateChanged(settings.defaultHourlyRate, values.defaultHourlyRate) ? rateFrom : null,
        onlyAdminsCreateProjects: values.onlyAdminsCreateProjects,
        onlyAdminsCreateTags: values.onlyAdminsCreateTags,
        lockedBefore: values.lockedBefore || null,
        requireDescription: values.requireDescription,
        requireProject: values.requireProject,
        requireTask: values.requireTask,
        requireTag: values.requireTag,
      })
      setErrors({})
      toast.success('Settings saved')
    } catch (error) {
      setErrors(error instanceof ApiError ? Object.fromEntries(Object.entries(error.fieldErrors).map(([key, messages]) => [key, messages[0]])) : { form: CONNECTION_ERROR })
    }
  }

  return (
    <form onSubmit={handleSubmit} className="grid gap-6">
      {errors.form && (
        <Alert variant="destructive">
          <AlertDescription>{errors.form}</AlertDescription>
        </Alert>
      )}
      <fieldset disabled={readOnly} className="grid gap-6">
        <Section title="General" description="The workspace's name and the currency its rates and amounts are in.">
          <Field id="settings-name" label="Name" error={errors.name}>
            <Input id="settings-name" value={values.name} onChange={(e) => set('name', e.target.value)} aria-invalid={errors.name ? true : undefined} />
          </Field>
          <Field id="settings-currency" label="Currency" error={errors.currency}>
            <Select value={values.currency} onValueChange={(currency) => set('currency', currency)} disabled={readOnly}>
              <SelectTrigger id="settings-currency" className="w-40">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {currencies.map((code) => (
                  <SelectItem key={code} value={code}>
                    {code}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
        </Section>

        {!readOnly && (
          <Section
            title="Billable rate"
            description="What an hour of billable time is worth when neither the project nor the member has a rate of their own."
          >
            <div className="max-w-md">
              <RateField
                id="settings-rate"
                label={`Default hourly rate (${values.currency})`}
                currency={values.currency}
                placeholder="No default rate"
                value={values.defaultHourlyRate}
                onChange={(rate) => set('defaultHourlyRate', rate)}
                saved={settings.defaultHourlyRate}
                from={rateFrom}
                onFromChange={setRateFrom}
                scope={{}}
                error={errors.defaultHourlyRate}
                fromError={errors.defaultHourlyRateFrom}
                className="w-48"
              />
            </div>
          </Section>
        )}

        <Section title="Permissions" description="Admins can always do everything. Members track their own time and, if allowed here, add to the workspace.">
          <SwitchField
            disabled={readOnly}
            id="settings-projects"
            label="Only admins create projects and clients"
            checked={values.onlyAdminsCreateProjects}
            onCheckedChange={(checked) => set('onlyAdminsCreateProjects', checked)}
          />
          <SwitchField disabled={readOnly} id="settings-tags" label="Only admins create tags" checked={values.onlyAdminsCreateTags} onCheckedChange={(checked) => set('onlyAdminsCreateTags', checked)} />
        </Section>

        <Section
          title="Lock time entries"
          description="Members cannot add, change or delete time on days before this date, for example once a month is invoiced. Admins still can."
        >
          <Field id="settings-lock" label="Lock entries before" error={errors.lockedBefore}>
            <div className="flex gap-2">
              <Input id="settings-lock" type="date" value={values.lockedBefore ?? ''} onChange={(e) => set('lockedBefore', e.target.value || null)} className="w-48" />
              {values.lockedBefore && !readOnly && (
                <Button type="button" variant="ghost" onClick={() => set('lockedBefore', null)}>
                  Unlock
                </Button>
              )}
            </div>
          </Field>
        </Section>

        <Section title="Required details" description="Finished time entries must have these. A running timer can start without them, but not stop.">
          <SwitchField disabled={readOnly} id="settings-require-description" label="Description" checked={values.requireDescription} onCheckedChange={(checked) => set('requireDescription', checked)} />
          <SwitchField disabled={readOnly} id="settings-require-project" label="Project" checked={values.requireProject} onCheckedChange={(checked) => set('requireProject', checked)} />
          <SwitchField
            disabled={readOnly}
            id="settings-require-task"
            label="Task, on projects that have open tasks"
            checked={values.requireTask}
            onCheckedChange={(checked) => set('requireTask', checked)}
          />
          <SwitchField disabled={readOnly} id="settings-require-tag" label="At least one tag" checked={values.requireTag} onCheckedChange={(checked) => set('requireTag', checked)} />
        </Section>
      </fieldset>

      {!readOnly && (
        <div className="flex justify-end">
          <Button type="submit" disabled={update.isPending}>
            Save settings
          </Button>
        </div>
      )}
    </form>
  )
}

function Section({ title, description, children }: { title: string; description: string; children: ReactNode }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">{children}</CardContent>
    </Card>
  )
}

function Field({ id, label, error, children }: { id: string; label: string; error?: string; children: ReactNode }) {
  return (
    <div className="grid gap-2">
      <Label htmlFor={id}>{label}</Label>
      {children}
      {error && <p className="text-destructive text-sm">{error}</p>}
    </div>
  )
}

interface SwitchFieldProps {
  id: string
  label: string
  checked: boolean
  onCheckedChange: (checked: boolean) => void
  disabled: boolean
}

function SwitchField({ id, label, checked, onCheckedChange, disabled }: SwitchFieldProps) {
  return (
    <div className="flex items-center justify-between gap-4">
      <Label htmlFor={id} className="font-normal">
        {label}
      </Label>
      <Switch id={id} checked={checked} onCheckedChange={onCheckedChange} disabled={disabled} />
    </div>
  )
}
