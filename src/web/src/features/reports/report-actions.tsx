import { ChevronDown, Download, Ellipsis, Loader2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import { CONNECTION_ERROR } from '@/auth/form-errors'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { TextField } from '@/components/text-field'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { errorMessage } from '@/lib/error-message'
import { formatDayRange, PERIOD_LABELS, type DayRange, type PeriodKey } from './period'
import { useDeleteSavedReport, useSaveReport, type ExportFormat, type SavedReport } from './queries'
import { savedParameters, type ReportSearch } from './search'
import type { BuiltInView } from './views'

interface ViewActionsProps {
  workspaceId: string
  search: ReportSearch
  period: PeriodKey
  range: DayRange
  builtIn?: BuiltInView
  savedReport?: SavedReport
  edited: boolean
  onReset: () => void
  /** A view was saved, as a new one or over the one open. */
  onSaved: (report: SavedReport) => void
  onDeleted: () => void
}

type OpenDialog = 'save' | 'rename' | 'delete' | null

/**
 * What can be done with the view open: once it is changed, reset it or keep the change, over the person's own view or
 * as a new one; and rename or delete a view of their own.
 */
export function ViewActions({ workspaceId, search, period, range, builtIn, savedReport, edited, onReset, onSaved, onDeleted }: ViewActionsProps) {
  const save = useSaveReport(workspaceId)
  const remove = useDeleteSavedReport(workspaceId)
  const [dialog, setDialog] = useState<OpenDialog>(null)

  function saveChanges() {
    if (!savedReport) {
      return
    }
    save.mutate(
      { id: savedReport.id, name: savedReport.name, parameters: savedParameters(search) },
      {
        onSuccess: (report) => {
          toast.success(`“${report.name}” saved`)
          onSaved(report)
        },
        onError: (error) => toast.error(errorMessage(error, "Couldn't save the view.")),
      },
    )
  }

  const close = (open: boolean) => !open && setDialog(null)

  return (
    <>
      {edited && (
        <Button variant="ghost" onClick={onReset}>
          Reset
        </Button>
      )}
      {savedReport
        ? edited && (
            <div className="flex items-center">
              <Button variant="outline" className="rounded-r-none" disabled={save.isPending} onClick={saveChanges}>
                Save
              </Button>
              <DropdownMenu>
                <DropdownMenuTrigger asChild>
                  <Button variant="outline" size="icon" className="-ml-px rounded-l-none" aria-label="More ways to save">
                    <ChevronDown />
                  </Button>
                </DropdownMenuTrigger>
                <DropdownMenuContent align="end">
                  <DropdownMenuItem onSelect={() => setDialog('save')}>Save as a new view…</DropdownMenuItem>
                </DropdownMenuContent>
              </DropdownMenu>
            </div>
          )
        : (edited || !builtIn) && (
            <Button variant={edited ? 'default' : 'outline'} onClick={() => setDialog('save')}>
              Save as a view…
            </Button>
          )}

      {(savedReport || (builtIn && !edited)) && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="outline" size="icon" aria-label="More for this view">
              <Ellipsis />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="w-52">
            {savedReport ? (
              <>
                <DropdownMenuItem onSelect={() => setDialog('rename')}>Rename…</DropdownMenuItem>
                <DropdownMenuItem onSelect={() => setDialog('save')}>Save as a new view…</DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem variant="destructive" onSelect={() => setDialog('delete')}>
                  Delete…
                </DropdownMenuItem>
              </>
            ) : (
              <DropdownMenuItem onSelect={() => setDialog('save')}>Save a copy as a view…</DropdownMenuItem>
            )}
          </DropdownMenuContent>
        </DropdownMenu>
      )}

      <Dialog open={dialog === 'save' || dialog === 'rename'} onOpenChange={close}>
        <DialogContent className="sm:max-w-sm">
          {dialog === 'save' && (
            <ViewForm
              title="Save as a view"
              description="Keeps the filters and grouping, so you can open it again in one click."
              initialName={savedReport ? `${savedReport.name} (copy)` : (builtIn?.label ?? '')}
              period={period}
              range={range}
              onSave={async (name, fixed) => {
                const parameters = savedParameters(fixed ? { ...search, period: 'custom', from: range.from, to: range.to } : search)
                const report = await save.mutateAsync({ name, parameters })
                toast.success(`“${report.name}” saved`)
                setDialog(null)
                onSaved(report)
              }}
              onCancel={() => setDialog(null)}
            />
          )}
          {dialog === 'rename' && savedReport && (
            <ViewForm
              title="Rename the view"
              initialName={savedReport.name}
              onSave={async (name) => {
                // Only the name changes, whatever is on screen.
                const report = await save.mutateAsync({ id: savedReport.id, name, parameters: savedReport })
                toast.success(`Renamed to “${report.name}”`)
                setDialog(null)
              }}
              onCancel={() => setDialog(null)}
            />
          )}
        </DialogContent>
      </Dialog>

      {savedReport && (
        <ConfirmDialog
          open={dialog === 'delete'}
          onOpenChange={close}
          title={`Delete “${savedReport.name}”?`}
          description="The view goes; the time in it stays."
          confirmLabel="Delete"
          onConfirm={() =>
            remove.mutate(savedReport.id, {
              onSuccess: () => {
                toast.success(`“${savedReport.name}” deleted`)
                onDeleted()
              },
              onError: (error) => toast.error(errorMessage(error, "Couldn't delete the view.")),
            })
          }
        />
      )}
    </>
  )
}

interface ViewFormProps {
  title: string
  description?: string
  initialName: string
  /** The period on screen, to ask whether the view moves with time or keeps these days; left out when renaming. */
  period?: PeriodKey
  range?: DayRange
  onSave: (name: string, fixed: boolean) => Promise<void>
  onCancel: () => void
}

function ViewForm({ title, description, initialName, period, range, onSave, onCancel }: ViewFormProps) {
  const [name, setName] = useState(initialName)
  const [fixed, setFixed] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)
  const moving = period !== undefined && period !== 'custom'

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!name.trim()) {
      setError('Enter a name for the view.')
      return
    }
    setPending(true)
    try {
      await onSave(name.trim(), fixed || !moving)
    } catch (failure) {
      setError(failure instanceof ApiError ? (failure.fieldErrors.name?.[0] ?? failure.message) : CONNECTION_ERROR)
    } finally {
      setPending(false)
    }
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>{title}</DialogTitle>
        {description && <DialogDescription>{description}</DialogDescription>}
      </DialogHeader>
      <TextField id="saved-report-name" label="Name" value={name} onChange={(e) => setName(e.target.value)} autoFocus error={error ?? undefined} placeholder="e.g. Monthly billing" />
      {period && range && (
        <fieldset className="grid gap-2">
          <legend className="mb-2 text-sm font-medium">Period</legend>
          {moving ? (
            <>
              <PeriodChoice checked={!fixed} onChange={() => setFixed(false)} label="Moves with time" detail={`Always ${PERIOD_LABELS[period].toLowerCase()}`} />
              <PeriodChoice checked={fixed} onChange={() => setFixed(true)} label="Fixed" detail={formatDayRange(range)} />
            </>
          ) : (
            <p className="text-muted-foreground text-sm">Fixed: {formatDayRange(range)}</p>
          )}
        </fieldset>
      )}
      <DialogFooter>
        <Button type="button" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={pending}>
          Save
        </Button>
      </DialogFooter>
    </form>
  )
}

function PeriodChoice({ checked, onChange, label, detail }: { checked: boolean; onChange: () => void; label: string; detail: string }) {
  return (
    <label className="flex cursor-pointer items-start gap-2 text-sm">
      <input type="radio" name="view-period" checked={checked} onChange={onChange} className="accent-primary mt-0.5 size-4" />
      <span className="grid">
        {label}
        <span className="text-muted-foreground">{detail}</span>
      </span>
    </label>
  )
}

/** Downloads the report as CSV, Excel or PDF. */
export function ExportMenu({ onExport }: { onExport: (format: ExportFormat) => Promise<void> }) {
  const [pending, setPending] = useState<ExportFormat | null>(null)

  async function run(format: ExportFormat) {
    setPending(format)
    try {
      await onExport(format)
    } catch (error) {
      toast.error(errorMessage(error, "Couldn't export the report."))
    } finally {
      setPending(null)
    }
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" disabled={pending !== null}>
          {pending ? <Loader2 className="animate-spin" /> : <Download />}
          Export
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onSelect={() => void run('pdf')}>PDF</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void run('xlsx')}>Excel (.xlsx)</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => void run('csv')}>CSV</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
