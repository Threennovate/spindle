import { useQuery } from '@tanstack/react-query'
import { Bookmark, Download, Loader2 } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import { CONNECTION_ERROR } from '@/auth/form-errors'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { TextField } from '@/components/text-field'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { errorMessage } from '@/lib/error-message'
import { savedReportsQuery, useDeleteSavedReport, useSaveReport, type ExportFormat, type SavedReport } from './queries'
import { parseSavedParameters, savedParameters, sameReport, type ReportSearch } from './search'

interface SavedReportsMenuProps {
  workspaceId: string
  search: ReportSearch
  onOpen: (report: SavedReport) => void
  onSaved: (report: SavedReport) => void
  onDeleted: () => void
}

/** Opens, saves, updates and deletes the person's own saved reports. */
export function SavedReportsMenu({ workspaceId, search, onOpen, onSaved, onDeleted }: SavedReportsMenuProps) {
  const { data: reports = [] } = useQuery(savedReportsQuery(workspaceId))
  const save = useSaveReport(workspaceId)
  const remove = useDeleteSavedReport(workspaceId)
  const [naming, setNaming] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const current = reports.find((report) => report.id === search.saved)
  const changed = current ? !sameReport(parseSavedParameters(current.parameters), savedParameters(search)) : false

  function update() {
    if (!current) {
      return
    }
    save.mutate(
      { id: current.id, name: current.name, parameters: savedParameters(search) },
      {
        onSuccess: (report) => {
          toast.success(`“${report.name}” saved`)
          onSaved(report)
        },
        onError: (error) => toast.error(errorMessage(error, "Couldn't save the report.")),
      },
    )
  }

  return (
    <>
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button variant="outline">
            <Bookmark />
            <span className="max-w-48 truncate">{current ? current.name : 'Saved reports'}</span>
            {changed && <span className="text-muted-foreground font-normal">· edited</span>}
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-64">
          {reports.length > 0 && (
            <>
              <DropdownMenuLabel>Saved reports</DropdownMenuLabel>
              {reports.map((report) => (
                <DropdownMenuCheckboxItem key={report.id} checked={report.id === current?.id} onSelect={() => onOpen(report)}>
                  <span className="truncate">{report.name}</span>
                </DropdownMenuCheckboxItem>
              ))}
              <DropdownMenuSeparator />
            </>
          )}
          {current && (
            <DropdownMenuItem disabled={!changed || save.isPending} onSelect={update}>
              Save changes to “{current.name}”
            </DropdownMenuItem>
          )}
          <DropdownMenuItem onSelect={() => setNaming(true)}>{current ? 'Save as a new report…' : 'Save this report…'}</DropdownMenuItem>
          {current && (
            <DropdownMenuItem variant="destructive" onSelect={() => setConfirmDelete(true)}>
              Delete “{current.name}”
            </DropdownMenuItem>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      <Dialog open={naming} onOpenChange={setNaming}>
        <DialogContent className="sm:max-w-sm">
          {naming && (
            <SaveReportForm
              initialName={current ? `${current.name} (copy)` : ''}
              onSave={async (name) => {
                const report = await save.mutateAsync({ name, parameters: savedParameters(search) })
                toast.success(`“${report.name}” saved`)
                setNaming(false)
                onSaved(report)
              }}
              onCancel={() => setNaming(false)}
            />
          )}
        </DialogContent>
      </Dialog>

      {current && (
        <ConfirmDialog
          open={confirmDelete}
          onOpenChange={setConfirmDelete}
          title={`Delete “${current.name}”?`}
          description="The saved report goes; the time in it stays."
          confirmLabel="Delete"
          onConfirm={() =>
            remove.mutate(current.id, {
              onSuccess: () => {
                toast.success(`“${current.name}” deleted`)
                onDeleted()
              },
              onError: (error) => toast.error(errorMessage(error, "Couldn't delete the report.")),
            })
          }
        />
      )}
    </>
  )
}

function SaveReportForm({ initialName, onSave, onCancel }: { initialName: string; onSave: (name: string) => Promise<void>; onCancel: () => void }) {
  const [name, setName] = useState(initialName)
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!name.trim()) {
      setError('Enter a name for the report.')
      return
    }
    setPending(true)
    try {
      await onSave(name.trim())
    } catch (failure) {
      setError(failure instanceof ApiError ? (failure.fieldErrors.name?.[0] ?? failure.message) : CONNECTION_ERROR)
    } finally {
      setPending(false)
    }
  }

  return (
    <form noValidate onSubmit={handleSubmit} className="grid gap-5">
      <DialogHeader>
        <DialogTitle>Save report</DialogTitle>
        <DialogDescription>Keeps the period, filters and grouping, so you can open this report again in one click.</DialogDescription>
      </DialogHeader>
      <TextField id="saved-report-name" label="Name" value={name} onChange={(e) => setName(e.target.value)} autoFocus error={error ?? undefined} placeholder="e.g. Monthly billing" />
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
