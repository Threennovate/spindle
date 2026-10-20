import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { CircleCheck, Clock, FileUp, Loader2, TriangleAlert } from 'lucide-react'
import { useId, useState, type ChangeEvent, type DragEvent, type ReactNode } from 'react'
import { cn } from 'cn'
import { PageHeader } from '@/components/page-header'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { errorMessage } from '@/lib/error-message'
import { formatHoursMinutes } from '@/lib/time/duration'
import { formatDayAs } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { TimeZonePicker } from '../account/time-zone-picker'
import { membersQuery } from '../team/queries'
import { useWorkspace } from '../workspace'
import { useImportPreview, useRunImport, type DateOrder, type ImportInput, type ImportPreview, type ImportResult } from './queries'

/** The API takes files up to about 20 MB; a year of a busy team is far less. */
const MAX_FILE_BYTES = 20 * 1024 * 1024
const LEAVE_OUT = 'leave-out'

const DATE_ORDERS: [DateOrder, string][] = [
  ['auto', 'Work it out'],
  ['yearMonthDay', '2026-09-14'],
  ['monthDayYear', '09/14/2026 (month first)'],
  ['dayMonthYear', '14/09/2026 (day first)'],
]

/** Brings time over from Toggl Track, Clockify, Harvest or another Spindle workspace, after showing what will happen. */
export function ImportPage() {
  usePageTitle('Import')
  const { workspaceId, workspace, isAdmin, me } = useWorkspace()
  const [input, setInput] = useState<ImportInput | null>(null)
  const [fileName, setFileName] = useState<string | null>(null)
  const [fileError, setFileError] = useState<string | null>(null)
  const [result, setResult] = useState<{ imported: ImportResult; from?: string | null; to?: string | null } | null>(null)
  const preview = useImportPreview(workspaceId, input)
  const run = useRunImport(workspaceId)

  if (!isAdmin) {
    return (
      <div className="mx-auto grid max-w-3xl gap-6">
        <PageHeader title="Import time" />
        <Alert>
          <AlertDescription>Only workspace admins can import time. Ask one of them to bring your time over.</AlertDescription>
        </Alert>
      </div>
    )
  }

  async function choose(file: File) {
    setFileError(null)
    setResult(null)
    if (file.size > MAX_FILE_BYTES) {
      setFileError('The file is larger than 20 MB. Export a shorter period, e.g. a year per file.')
      return
    }
    const content = await file.text()
    setFileName(file.name)
    setInput({ fileKey: `${file.name}|${file.size}|${file.lastModified}`, content, timeZone: me.timeZone, dateOrder: 'auto', assignments: {} })
  }

  function reset() {
    setInput(null)
    setFileName(null)
    setResult(null)
    run.reset()
  }

  async function importNow() {
    if (!input) {
      return
    }
    const { from, to } = preview.data ?? {}
    setResult({ imported: await run.mutateAsync(input), from, to })
  }

  return (
    <div className="mx-auto grid max-w-4xl gap-6">
      <PageHeader title="Import time" description={`Bring time tracked in Toggl Track, Clockify or Harvest into ${workspace.name}.`} />

      {result ? (
        <ImportDone result={result.imported} from={result.from} to={result.to} onAgain={reset} />
      ) : (
        <>
          <Card>
            <CardHeader>
              <CardTitle>1. Choose the file</CardTitle>
              <CardDescription>
                The detailed report as CSV. In Toggl Track: Reports → Detailed → Export → Download CSV. In Clockify: Reports → Detailed → Export →
                Save as CSV. In Harvest: Reports → Detailed time → Export → CSV. A detailed CSV from Spindle works too.
              </CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4">
              <FileDrop fileName={fileName} onFile={(file) => void choose(file)} />
              {fileError && (
                <Alert variant="destructive">
                  <AlertDescription>{fileError}</AlertDescription>
                </Alert>
              )}
              {input && (
                <div className="grid gap-4 sm:grid-cols-2">
                  <div className="grid gap-2">
                    <Label htmlFor="import-zone">The file's times are in</Label>
                    <TimeZonePicker id="import-zone" value={input.timeZone} onChange={(timeZone) => setInput({ ...input, timeZone })} />
                  </div>
                  <div className="grid gap-2">
                    <Label htmlFor="import-dates">Dates are written like</Label>
                    <Select value={input.dateOrder} onValueChange={(dateOrder) => setInput({ ...input, dateOrder: dateOrder as DateOrder })}>
                      <SelectTrigger id="import-dates" className="w-full">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {DATE_ORDERS.map(([value, label]) => (
                          <SelectItem key={value} value={value}>
                            {label}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                </div>
              )}
            </CardContent>
          </Card>

          {input && preview.isPending && <p className="text-muted-foreground flex items-center gap-2 text-sm"><Loader2 className="size-4 animate-spin" /> Reading the file…</p>}
          {input && preview.isError && (
            <Alert variant="destructive">
              <AlertDescription>{errorMessage(preview.error, "Couldn't read the file.")}</AlertDescription>
            </Alert>
          )}
          {input && preview.data && (
            <PreviewCard
              preview={preview.data}
              workspaceId={workspaceId}
              updating={preview.isFetching}
              assignments={input.assignments}
              onAssign={(key, userId) => setInput({ ...input, assignments: { ...input.assignments, [key]: userId } })}
              onDateOrder={(dateOrder) => setInput({ ...input, dateOrder })}
            />
          )}

          {input && preview.data && (
            <div className="flex flex-wrap items-center justify-end gap-3">
              {run.isError && <p className="text-destructive text-sm">{errorMessage(run.error, "Couldn't import the file.")}</p>}
              <Button variant="ghost" onClick={reset} disabled={run.isPending}>
                Choose another file
              </Button>
              <Button onClick={() => void importNow().catch(() => undefined)} disabled={run.isPending || preview.isFetching || preview.data.ready === 0}>
                {run.isPending && <Loader2 className="animate-spin" />}
                {preview.data.ready === 0 ? 'Nothing to import' : `Import ${preview.data.ready.toLocaleString('en-GB')} ${preview.data.ready === 1 ? 'entry' : 'entries'}`}
              </Button>
            </div>
          )}
        </>
      )}
    </div>
  )
}

function FileDrop({ fileName, onFile }: { fileName: string | null; onFile: (file: File) => void }) {
  const id = useId()
  const [over, setOver] = useState(false)

  function drop(event: DragEvent) {
    event.preventDefault()
    setOver(false)
    const file = event.dataTransfer.files[0]
    if (file) {
      onFile(file)
    }
  }

  return (
    <label
      htmlFor={id}
      onDragOver={(event) => {
        event.preventDefault()
        setOver(true)
      }}
      onDragLeave={() => setOver(false)}
      onDrop={drop}
      className={cn(
        'hover:bg-muted/40 grid cursor-pointer justify-items-center gap-2 rounded-xl border border-dashed px-6 py-8 text-center transition-colors',
        over && 'border-primary bg-primary/5',
      )}
    >
      <FileUp aria-hidden="true" className="text-muted-foreground size-8" />
      <span className="text-sm font-medium">{fileName ?? 'Choose a CSV file, or drop it here'}</span>
      <span className="text-muted-foreground text-xs">{fileName ? 'Choose another to replace it.' : 'Up to 20 MB.'}</span>
      <input
        id={id}
        type="file"
        accept=".csv,text/csv"
        aria-label="CSV file to import"
        className="sr-only"
        onChange={(event: ChangeEvent<HTMLInputElement>) => {
          const file = event.target.files?.[0]
          if (file) {
            onFile(file)
          }
          event.target.value = ''
        }}
      />
    </label>
  )
}

interface PreviewCardProps {
  preview: ImportPreview
  workspaceId: string
  updating: boolean
  assignments: Record<string, string | null>
  onAssign: (key: string, userId: string | null) => void
  onDateOrder: (order: DateOrder) => void
}

function PreviewCard({ preview, workspaceId, updating, assignments, onAssign, onDateOrder }: PreviewCardProps) {
  const { dateFormat } = useWorkspace()
  const { data: members = [] } = useQuery(membersQuery(workspaceId))
  const period = preview.from && preview.to ? `${formatDayAs(preview.from, dateFormat)} – ${formatDayAs(preview.to, dateFormat)}` : null

  return (
    <Card className={cn('transition-opacity', updating && 'opacity-60')} aria-busy={updating}>
      <CardHeader>
        <CardTitle>2. Check what will be imported</CardTitle>
        <CardDescription>
          {preview.format} file · {preview.rows.toLocaleString('en-GB')} {preview.rows === 1 ? 'entry' : 'entries'}
          {period && ` · ${period}`}
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-6">
        <div className="grid gap-3 sm:grid-cols-4">
          <Figure label="To import" value={preview.ready.toLocaleString('en-GB')} note={formatHoursMinutes(preview.seconds)} />
          <Figure label="Already here" value={preview.duplicates.toLocaleString('en-GB')} note="Skipped as duplicates" />
          <Figure label="Left out" value={preview.skipped.toLocaleString('en-GB')} note="People not assigned" />
          <Figure label="Unreadable" value={preview.problemCount.toLocaleString('en-GB')} note="Rows with problems" />
        </div>

        {preview.dateOrderGuessed && (
          <Alert>
            <TriangleAlert />
            <AlertDescription>
              <span>
                Every date in the file could be read two ways, such as 03/04/2026. They are read with the month first.{' '}
                <Button variant="link" className="h-auto p-0" onClick={() => onDateOrder('dayMonthYear')}>
                  Read the day first instead
                </Button>
              </span>
            </AlertDescription>
          </Alert>
        )}

        {preview.placed > 0 && (
          <Alert>
            <Clock />
            <AlertDescription>
              {preview.placed === preview.rows
                ? 'The file has hours but no times of day.'
                : `${preview.placed.toLocaleString('en-GB')} ${preview.placed === 1 ? 'entry has' : 'entries have'} hours but no time of day.`}{' '}
              Each person's entries on a day are placed one after another from 9:00, in the order of the file, and earlier on a day of more than 15
              hours so it ends by midnight. Importing the file again doesn't add them twice.
            </AlertDescription>
          </Alert>
        )}

        <Section title="People" description="Each person's time goes to a member of this workspace. People without one are left out; invite them first to keep their time.">
          <ul aria-label="People in the file" className="divide-y rounded-lg border">
            {preview.people.map((person) => (
              <li key={person.key} className="flex flex-wrap items-center gap-3 px-3 py-2">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium">{person.name}</p>
                  <p className="text-muted-foreground truncate text-xs">
                    {person.email ?? 'No email in the file'} · {person.entries} {person.entries === 1 ? 'entry' : 'entries'} · {formatHoursMinutes(person.seconds)}
                  </p>
                </div>
                <Select value={(person.key in assignments ? assignments[person.key] : person.userId) ?? LEAVE_OUT} onValueChange={(value) => onAssign(person.key, value === LEAVE_OUT ? null : value)}>
                  <SelectTrigger size="sm" aria-label={`Whose time ${person.name}'s entries become`} className={cn('w-52', !person.userId && 'text-muted-foreground')}>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value={LEAVE_OUT}>Leave out</SelectItem>
                    {members.map((member) => (
                      <SelectItem key={member.userId} value={member.userId}>
                        {member.fullName}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </li>
            ))}
          </ul>
        </Section>

        {(preview.newClients.length > 0 || preview.newProjects.length > 0 || preview.newTasks.length > 0 || preview.newTags.length > 0) && (
          <Section title="Created on the way" description="What the file uses that this workspace does not have yet. Existing ones with the same names are used as they are.">
            <div className="grid gap-3 sm:grid-cols-2">
              <NameList label="Clients" names={preview.newClients} />
              <NameList label="Projects" names={preview.newProjects.map((p) => (p.client ? `${p.name} (${p.client})` : p.name))} />
              <NameList label="Tasks" names={preview.newTasks} />
              <NameList label="Tags" names={preview.newTags} />
            </div>
          </Section>
        )}

        {preview.problems.length > 0 && (
          <Section title="Rows that could not be read" description="These are left out. Fix them in the file and import it again; what is already imported is not added twice.">
            <ul aria-label="Problems" className="max-h-56 divide-y overflow-y-auto rounded-lg border text-sm">
              {preview.problems.map((problem) => (
                <li key={problem.line} className="flex gap-3 px-3 py-2">
                  <span className="text-muted-foreground w-16 shrink-0 tabular-nums">Line {problem.line}</span>
                  <span>{problem.message}</span>
                </li>
              ))}
            </ul>
            {preview.problemCount > preview.problems.length && (
              <p className="text-muted-foreground text-xs">And {preview.problemCount - preview.problems.length} more.</p>
            )}
          </Section>
        )}
      </CardContent>
    </Card>
  )
}

function ImportDone({ result, from, to, onAgain }: { result: ImportResult; from?: string | null; to?: string | null; onAgain: () => void }) {
  const created = [
    [result.newProjects, 'project', 'projects'],
    [result.newClients, 'client', 'clients'],
    [result.newTasks, 'task', 'tasks'],
    [result.newTags, 'tag', 'tags'],
  ]
    .filter(([count]) => (count as number) > 0)
    .map(([count, one, many]) => `${count} ${count === 1 ? one : many}`)

  return (
    <Card>
      <CardContent className="grid justify-items-center gap-3 py-10 text-center">
        <CircleCheck aria-hidden="true" className="size-10 text-green-600" />
        <p className="text-lg font-semibold">
          Imported {result.created.toLocaleString('en-GB')} {result.created === 1 ? 'entry' : 'entries'}
        </p>
        <p className="text-muted-foreground max-w-md text-sm">
          {created.length > 0 && `Created ${created.join(', ')}. `}
          {result.duplicates > 0 && `${result.duplicates} were here already. `}
          {result.skipped + result.problems > 0 && `${result.skipped + result.problems} left out.`}
        </p>
        <div className="flex flex-wrap justify-center gap-2 pt-2">
          {from && to && (
            <Button asChild>
              <Link to="/reports" search={{ period: 'custom', from, to }}>
                See it in reports
              </Link>
            </Button>
          )}
          <Button variant="outline" onClick={onAgain}>
            Import another file
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

function Section({ title, description, children }: { title: string; description: string; children: ReactNode }) {
  return (
    <section className="grid gap-2">
      <h3 className="text-sm font-medium">{title}</h3>
      <p className="text-muted-foreground -mt-1 text-xs">{description}</p>
      {children}
    </section>
  )
}

function Figure({ label, value, note }: { label: string; value: string; note: string }) {
  return (
    <div className="bg-muted/40 grid gap-0.5 rounded-lg p-3">
      <span className="text-muted-foreground text-xs">{label}</span>
      <span className="text-xl font-semibold tabular-nums">{value}</span>
      <span className="text-muted-foreground text-xs">{note}</span>
    </div>
  )
}

function NameList({ label, names }: { label: string; names: string[] }) {
  if (names.length === 0) {
    return null
  }
  return (
    <div className="grid content-start gap-1.5">
      <p className="text-xs font-medium">
        {label} <span className="text-muted-foreground">· {names.length}</span>
      </p>
      <div className="flex flex-wrap gap-1">
        {names.slice(0, 30).map((name) => (
          <Badge key={name} variant="secondary" className="font-normal">
            {name}
          </Badge>
        ))}
        {names.length > 30 && <span className="text-muted-foreground text-xs">and {names.length - 30} more</span>}
      </div>
    </div>
  )
}
