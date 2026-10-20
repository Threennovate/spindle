import { useQuery } from '@tanstack/react-query'
import { formatDistanceToNow } from 'date-fns'
import { ArrowLeft, Copy, Link2, Pencil, Plus } from 'lucide-react'
import { useState, type FormEvent } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import { CONNECTION_ERROR } from '@/auth/form-errors'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { TextField } from '@/components/text-field'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Switch } from '@/components/ui/switch'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { errorMessage } from '@/lib/error-message'
import { useCatalog, type Catalog } from '../catalog/use-catalog'
import { formatDayRange, type DayRange, type PeriodKey } from '../reports/period'
import type { ReportFilter, RoundingMode } from '../reports/search'
import { useWorkspace } from '../workspace'
import {
  sharedReportsQuery,
  useDeleteSharedReport,
  useNewSharedReportAddress,
  useSaveSharedReport,
  type HourFormat,
  type SharedPeriod,
  type SharedReport,
  type SharedReportRequest,
  type SharedSender,
} from './queries'

const RELATIVE_PERIODS = ['thisMonth', 'lastMonth', 'thisWeek', 'lastWeek'] as const satisfies readonly SharedPeriod[]
const PERIOD_LABELS: Record<SharedPeriod, string> = {
  thisMonth: 'This month',
  lastMonth: 'Last month',
  thisWeek: 'This week',
  lastWeek: 'Last week',
  fixed: 'Fixed days',
}

interface ShareDialogProps {
  workspaceId: string
  open: boolean
  onOpenChange: (open: boolean) => void
  /** The report on screen, which a new link is made from. */
  filter: ReportFilter
  period: PeriodKey
  range: DayRange
}

/** The workspace's timesheet links, and a new one from the report on screen. For admins. */
export function ShareDialog({ workspaceId, open, onOpenChange, filter, period, range }: ShareDialogProps) {
  const query = useQuery({ ...sharedReportsQuery(workspaceId), enabled: open })
  // null: the list; 'new': a link from this report; otherwise the link being changed.
  const [editing, setEditing] = useState<'new' | SharedReport | null>(null)
  const reports = query.data ?? []
  const view = editing ?? (query.isSuccess && reports.length === 0 ? 'new' : null)

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        onOpenChange(next)
        if (!next) {
          setEditing(null)
        }
      }}
    >
      <DialogContent className="sm:max-w-lg">
        {view === null ? (
          <LinkList loading={query.isPending} reports={reports} onNew={() => setEditing('new')} onEdit={setEditing} />
        ) : (
          <LinkForm
            key={view === 'new' ? 'new' : view.id}
            workspaceId={workspaceId}
            report={view === 'new' ? null : view}
            filter={view === 'new' ? linkFilter(filter) : view}
            defaultPeriod={(RELATIVE_PERIODS as readonly string[]).includes(period) ? (period as SharedPeriod) : 'fixed'}
            range={view === 'new' || !view.from || !view.to ? range : { from: view.from, to: view.to }}
            onBack={reports.length > 0 ? () => setEditing(null) : undefined}
            onDone={() => setEditing(null)}
          />
        )}
      </DialogContent>
    </Dialog>
  )
}

/** Which time a link counts: a report filter without its days. */
interface LinkFilter {
  userIds: string[]
  groupIds: string[]
  projectIds: string[]
  clientIds: string[]
  taskIds: string[]
  tagIds: string[]
  description?: string | null
  rounding?: RoundingMode
  roundingMinutes?: number
}

function linkFilter(filter: ReportFilter): LinkFilter {
  return {
    userIds: filter.userIds ?? [],
    groupIds: filter.groupIds ?? [],
    projectIds: filter.projectIds ?? [],
    clientIds: filter.clientIds ?? [],
    taskIds: filter.taskIds ?? [],
    tagIds: filter.tagIds ?? [],
    description: filter.description,
    rounding: filter.rounding,
    roundingMinutes: filter.roundingMinutes,
  }
}

function LinkList({ loading, reports, onNew, onEdit }: { loading: boolean; reports: SharedReport[]; onNew: () => void; onEdit: (report: SharedReport) => void }) {
  return (
    <div className="grid gap-5">
      <DialogHeader>
        <DialogTitle>Timesheet links</DialogTitle>
        <DialogDescription>Read-only pages of billed time, for clients. Anyone with a link can open it without an account.</DialogDescription>
      </DialogHeader>
      {loading ? (
        <Skeleton className="h-24" />
      ) : (
        <ul className="divide-y rounded-lg border">
          {reports.map((report) => (
            <li key={report.id} className="flex items-center gap-2 py-2 pr-2 pl-4">
              <div className="grid min-w-0 flex-1">
                <span className="truncate text-sm font-medium">{report.title}</span>
                <span className="text-muted-foreground truncate text-xs">{linkSummary(report)}</span>
              </div>
              <Button variant="ghost" size="icon" aria-label={`Copy the link to ${report.title}`} onClick={() => void copyLink(report.url)}>
                <Copy />
              </Button>
              <Button variant="ghost" size="icon" aria-label={`Change ${report.title}`} onClick={() => onEdit(report)}>
                <Pencil />
              </Button>
            </li>
          ))}
        </ul>
      )}
      <DialogFooter>
        <Button onClick={onNew}>
          <Plus />
          New link from this report
        </Button>
      </DialogFooter>
    </div>
  )
}

interface LinkFormProps {
  workspaceId: string
  /** The link being changed, or null for a new one. */
  report: SharedReport | null
  /** What the link counts: the report on screen for a new link, its own filter otherwise. */
  filter: LinkFilter
  defaultPeriod: SharedPeriod
  range: DayRange
  onBack?: () => void
  onDone: () => void
}

function LinkForm({ workspaceId, report, filter, defaultPeriod, range, onBack, onDone }: LinkFormProps) {
  const catalog = useCatalog(workspaceId)
  const save = useSaveSharedReport(workspaceId)
  const newAddress = useNewSharedReportAddress(workspaceId)
  const remove = useDeleteSharedReport(workspaceId)
  // Until it is typed in, the title follows the catalog, which may still be loading.
  const [typedTitle, setTitle] = useState<string | null>(report?.title ?? null)
  const title = typedTitle ?? suggestedTitle(filter, catalog)
  const [period, setPeriod] = useState<SharedPeriod>(report?.period ?? defaultPeriod)
  const [showPeople, setShowPeople] = useState(report?.showPeople ?? false)
  const [showAmounts, setShowAmounts] = useState(report?.showAmounts ?? false)
  // Off for a new link: today's time is half a day until the day is over, and billed time is often entered at its end.
  const [showToday, setShowToday] = useState(report?.showToday ?? false)
  const [hourFormat, setHourFormat] = useState<HourFormat>(report?.hourFormat ?? 'clock')
  const [sender, setSender] = useState<SharedSender>(report?.sender ?? 'workspace')
  const [senderName, setSenderName] = useState(report?.senderName ?? '')
  const { workspace } = useWorkspace()
  const [error, setError] = useState<string | null>(null)
  const [senderError, setSenderError] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<'address' | 'remove' | null>(null)
  const counts = describeFilter(filter, catalog)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    if (!title.trim()) {
      setError('Enter a title, such as the client’s name.')
      return
    }
    if (sender === 'custom' && !senderName.trim()) {
      setSenderError('Enter the name the client should see.')
      return
    }
    const body: SharedReportRequest = {
      title: title.trim(),
      period,
      from: period === 'fixed' ? range.from : null,
      to: period === 'fixed' ? range.to : null,
      userIds: filter.userIds,
      groupIds: filter.groupIds,
      projectIds: filter.projectIds,
      clientIds: filter.clientIds,
      taskIds: filter.taskIds,
      tagIds: filter.tagIds,
      description: filter.description,
      rounding: filter.rounding,
      roundingMinutes: filter.roundingMinutes,
      showPeople,
      showAmounts,
      showToday,
      hourFormat,
      sender,
      senderName: sender === 'custom' ? senderName.trim() : null,
    }
    try {
      const saved = await save.mutateAsync({ id: report?.id, body })
      if (report) {
        toast.success(`“${saved.title}” saved`)
      } else {
        await copyLink(saved.url, `Link to “${saved.title}” copied`)
      }
      onDone()
    } catch (failure) {
      setError(failure instanceof ApiError ? (Object.values(failure.fieldErrors)[0]?.[0] ?? failure.message) : CONNECTION_ERROR)
    }
  }

  return (
    // One column that may shrink, so the link's long address truncates instead of widening the dialog.
    <form noValidate onSubmit={handleSubmit} className="grid grid-cols-[minmax(0,1fr)] gap-5">
      <DialogHeader>
        <DialogTitle className="flex items-center gap-2">
          {onBack && (
            <Button type="button" variant="ghost" size="icon" className="-ml-2 size-7" aria-label="Back to the links" onClick={onBack}>
              <ArrowLeft />
            </Button>
          )}
          {report ? report.title : 'New timesheet link'}
        </DialogTitle>
        <DialogDescription>
          {report ? 'Counts' : 'Counts this report’s'} billable time{counts ? ` (${counts})` : ''}, as billed. Where billed time is entered separately, the client
          sees the days as billed, never the time tracked.
        </DialogDescription>
      </DialogHeader>

      <TextField
        id="shared-report-title"
        label="Title"
        value={title}
        onChange={(event) => setTitle(event.target.value)}
        error={error ?? undefined}
        placeholder="e.g. Acme"
        autoFocus={!report}
      />

      <div className="grid gap-2">
        <Label htmlFor="shared-report-sender">From</Label>
        <Select value={sender} onValueChange={(value) => setSender(value as SharedSender)}>
          <SelectTrigger id="shared-report-sender" className="w-full">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="workspace">{workspace.name}</SelectItem>
            <SelectItem value="person">The person who did the work</SelectItem>
            <SelectItem value="custom">Another name</SelectItem>
          </SelectContent>
        </Select>
        {sender === 'custom' && (
          <>
            <Input
              id="shared-report-sender-name"
              aria-label="Name the timesheet is from"
              aria-invalid={senderError ? true : undefined}
              aria-describedby={senderError ? 'shared-report-sender-error' : undefined}
              value={senderName}
              onChange={(event) => {
                setSenderName(event.target.value)
                setSenderError(null)
              }}
              placeholder="e.g. your name or your studio’s"
              maxLength={100}
              autoFocus
            />
            {senderError && (
              <p id="shared-report-sender-error" className="text-destructive text-sm">
                {senderError}
              </p>
            )}
          </>
        )}
        <p className="text-muted-foreground text-sm">
          {sender === 'person'
            ? `Their name while the period has one person’s time; ${workspace.name} when it has several people’s.`
            : 'The name at the top of the page and in its downloads.'}
        </p>
      </div>

      <div className="grid gap-2">
        <Label htmlFor="shared-report-period">Period</Label>
        <Select value={period} onValueChange={(value) => setPeriod(value as SharedPeriod)}>
          <SelectTrigger id="shared-report-period" className="w-full">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {RELATIVE_PERIODS.map((key) => (
              <SelectItem key={key} value={key}>
                {PERIOD_LABELS[key]}
              </SelectItem>
            ))}
            <SelectItem value="fixed">{formatDayRange(range)}</SelectItem>
          </SelectContent>
        </Select>
        <p className="text-muted-foreground text-sm">
          {period === 'fixed' ? 'Always these days.' : 'Moves on with the calendar. The client can also look back at earlier periods.'}
        </p>
      </div>

      <div className="grid gap-3">
        <SwitchField id="shared-report-people" label="Show who did the work" checked={showPeople} onCheckedChange={setShowPeople} />
        <SwitchField id="shared-report-amounts" label="Show amounts" checked={showAmounts} onCheckedChange={setShowAmounts} />
        <SwitchField id="shared-report-today" label="Show today’s time before the day is over" checked={showToday} onCheckedChange={setShowToday} />
        <div className="flex items-center justify-between gap-4">
          <span id="shared-report-hours" className="text-sm">
            Hours as
          </span>
          <ToggleGroup
            type="single"
            variant="outline"
            size="sm"
            value={hourFormat}
            onValueChange={(value) => value && setHourFormat(value as HourFormat)}
            aria-labelledby="shared-report-hours"
          >
            <ToggleGroupItem value="clock" className="px-3 tabular-nums">
              3:40
            </ToggleGroupItem>
            <ToggleGroupItem value="decimal" className="px-3 tabular-nums">
              3.67
            </ToggleGroupItem>
          </ToggleGroup>
        </div>
      </div>

      {report && (
        <div className="bg-muted/50 grid grid-cols-[minmax(0,1fr)] gap-2 rounded-lg p-3">
          <div className="flex items-center gap-2">
            <Link2 className="text-muted-foreground size-4 shrink-0" />
            <span className="min-w-0 flex-1 truncate font-mono text-xs">{report.url}</span>
            <Button type="button" variant="outline" size="sm" onClick={() => void copyLink(report.url)}>
              Copy
            </Button>
          </div>
          <div className="flex flex-wrap gap-x-3 text-sm">
            <Button type="button" variant="link" className="h-auto p-0" onClick={() => setConfirm('address')}>
              New address
            </Button>
            <Button type="button" variant="link" className="text-destructive h-auto p-0" onClick={() => setConfirm('remove')}>
              Remove link
            </Button>
          </div>
        </div>
      )}

      <DialogFooter>
        {onBack && (
          <Button type="button" variant="ghost" onClick={onBack}>
            Cancel
          </Button>
        )}
        <Button type="submit" disabled={save.isPending}>
          {report ? 'Save' : 'Create and copy link'}
        </Button>
      </DialogFooter>

      {report && (
        <>
          <ConfirmDialog
            open={confirm === 'address'}
            onOpenChange={(open) => setConfirm(open ? 'address' : null)}
            title="Give the link a new address?"
            description="The current address stops working at once. Send the new one to whoever should still see the timesheet."
            confirmLabel="New address"
            onConfirm={() =>
              newAddress.mutate(report.id, {
                onSuccess: (moved) => void copyLink(moved.url, 'New address copied'),
                onError: (failure) => toast.error(errorMessage(failure, "Couldn't change the address.")),
              })
            }
          />
          <ConfirmDialog
            open={confirm === 'remove'}
            onOpenChange={(open) => setConfirm(open ? 'remove' : null)}
            title={`Remove “${report.title}”?`}
            description="The link stops working for everyone who has it. The time stays."
            confirmLabel="Remove"
            onConfirm={() =>
              remove.mutate(report.id, {
                onSuccess: () => {
                  toast.success(`“${report.title}” removed`)
                  onDone()
                },
                onError: (failure) => toast.error(errorMessage(failure, "Couldn't remove the link.")),
              })
            }
          />
        </>
      )}
    </form>
  )
}

function SwitchField({ id, label, checked, onCheckedChange }: { id: string; label: string; checked: boolean; onCheckedChange: (checked: boolean) => void }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <Label htmlFor={id} className="font-normal">
        {label}
      </Label>
      <Switch id={id} checked={checked} onCheckedChange={onCheckedChange} />
    </div>
  )
}

async function copyLink(url: string, message = 'Link copied') {
  try {
    await navigator.clipboard.writeText(url)
    toast.success(message)
  } catch {
    toast.message('Copy the link', { description: url })
  }
}

/** "This month · opened 2 days ago". */
function linkSummary(report: SharedReport): string {
  const period = report.period === 'fixed' && report.from && report.to ? formatDayRange({ from: report.from, to: report.to }) : PERIOD_LABELS[report.period]
  const opened = report.lastOpenedAt ? `opened ${formatDistanceToNow(new Date(report.lastOpenedAt), { addSuffix: true })}` : 'not opened yet'
  return `${period} · ${opened}`
}

/** The client or project the report is filtered by, as a starting title. */
function suggestedTitle(filter: LinkFilter, catalog: Catalog): string {
  if (filter.clientIds.length === 1) {
    return catalog.client(filter.clientIds[0])?.name ?? ''
  }
  if (filter.projectIds.length === 1) {
    return catalog.project(filter.projectIds[0])?.name ?? ''
  }
  return ''
}

/** What the filter narrows the time to, in a few words: "Website, Shop · 2 people". */
function describeFilter(filter: LinkFilter, catalog: Catalog): string {
  const names = (ids: string[], name: (id: string) => string | undefined) => ids.map(name).filter((value): value is string => !!value)
  const parts = [
    ...names(filter.clientIds, (id) => catalog.client(id)?.name),
    ...names(filter.projectIds, (id) => catalog.project(id)?.name),
  ]
  const shown = parts.length > 3 ? [...parts.slice(0, 2), `${parts.length - 2} more`] : parts
  const people = filter.userIds.length + filter.groupIds.length
  return [shown.join(', '), people > 0 ? `${people} ${people === 1 ? 'person or group' : 'people or groups'}` : '', filter.tagIds.length > 0 ? 'some tags' : '']
    .filter(Boolean)
    .join(' · ')
}
