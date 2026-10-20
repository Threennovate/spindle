import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { Member } from '@/api/types'
import { CONNECTION_ERROR, hasErrors, toFormErrors, type FormErrors } from '@/auth/form-errors'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { formatRate, parseAmount } from '@/lib/money'
import { useRateInput } from '../rates/queries'
import { RateField } from '../rates/rate-field'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { useUpdateMember } from './queries'

const FIELDS = ['hourlyRate', 'hourlyRateFrom', 'laborCost', 'laborCostFrom'] as const
type Field = (typeof FIELDS)[number]

const NOT_AN_AMOUNT = 'Enter an amount per hour, e.g. 60 or 72.50.'

export function MemberRatesDialog({ member, onOpenChange }: { member: Member | null; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={member !== null} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-md">{member && <MemberRatesForm member={member} onDone={() => onOpenChange(false)} />}</DialogContent>
    </Dialog>
  )
}

function MemberRatesForm({ member, onDone }: { member: Member; onDone: () => void }) {
  const { workspaceId } = useWorkspace()
  const { settings } = usePermissions()
  const update = useUpdateMember(workspaceId)
  const hourlyRate = useRateInput(member.hourlyRate)
  const laborCost = useRateInput(member.laborCost)
  const [errors, setErrors] = useState<FormErrors<Field>>({})

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    const rate = parseAmount(hourlyRate.value)
    const cost = parseAmount(laborCost.value)
    const validation: FormErrors<Field> = {
      hourlyRate: rate === undefined ? NOT_AN_AMOUNT : undefined,
      laborCost: cost === undefined ? NOT_AN_AMOUNT : undefined,
    }
    setErrors(validation)
    if (hasErrors(validation)) {
      return
    }

    try {
      await update.mutateAsync({
        userId: member.userId,
        role: member.role,
        hourlyRate: rate ?? null,
        hourlyRateFrom: hourlyRate.fromToSend,
        laborCost: cost ?? null,
        laborCostFrom: laborCost.fromToSend,
      })
      toast.success(`Rates for ${member.fullName} saved`)
      onDone()
    } catch (error) {
      setErrors(error instanceof ApiError ? toFormErrors(error, FIELDS) : { form: CONNECTION_ERROR })
    }
  }

  const defaultRate = settings.defaultHourlyRate == null ? 'No rate' : `Workspace default, ${formatRate(settings.defaultHourlyRate, settings.currency)}`

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>Rates for {member.fullName}</DialogTitle>
        <DialogDescription>Only admins see rates. Leave a field empty to use the workspace default.</DialogDescription>
      </DialogHeader>

      {errors.form && (
        <Alert variant="destructive">
          <AlertDescription>{errors.form}</AlertDescription>
        </Alert>
      )}

      <RateField
        id="member-rate"
        label={`Billable rate (${settings.currency} per hour)`}
        currency={settings.currency}
        placeholder={defaultRate}
        value={hourlyRate.value}
        onChange={hourlyRate.setValue}
        saved={member.hourlyRate}
        from={hourlyRate.from}
        onFromChange={hourlyRate.setFrom}
        scope={{ userId: member.userId }}
        error={errors.hourlyRate}
        fromError={errors.hourlyRateFrom}
        hint="What their billable time is worth, unless a project sets its own rate."
      />
      <RateField
        id="member-cost"
        label={`Labor cost (${settings.currency} per hour)`}
        currency={settings.currency}
        placeholder="Not set"
        value={laborCost.value}
        onChange={laborCost.setValue}
        saved={member.laborCost}
        from={laborCost.from}
        onFromChange={laborCost.setFrom}
        scope={{ kind: 'laborCost', userId: member.userId }}
        error={errors.laborCost}
        fromError={errors.laborCostFrom}
        hint="What an hour of their time costs you, to see what projects earn."
      />

      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" disabled={update.isPending}>
          Save rates
        </Button>
      </DialogFooter>
    </form>
  )
}
