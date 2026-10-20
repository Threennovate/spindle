import { useQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { Building2, CalendarDays, ChartColumn, Clock3, FolderKanban, ListPlus, Play, Plus, Sheet as SheetIcon, Split, Tag as TagIcon, TriangleAlert } from 'lucide-react'
import { toast } from 'sonner'
import type { TimeEntry } from '@/api/types'
import { errorMessage } from '@/lib/error-message'
import { formatDurationAs, parseDuration } from '@/lib/time/duration'
import { dayKey, formatClock, formatDayHeading, readStartField } from '@/lib/time/zone'
import { useNow } from '@/lib/use-now'
import { ProjectDot } from '../catalog/project-label'
import { useSaveTag } from '../catalog/queries'
import { partName } from '../catalog/split-names'
import { SplitLabel } from '../catalog/split-label'
import { useCatalog } from '../catalog/use-catalog'
import { PERIOD_LABELS, PERIODS, type PeriodKey } from '../reports/period'
import { usePermissions } from '../settings/queries'
import { currentEntryQuery, dayEntriesQuery } from '../time-entries/queries'
import { missingDetails, missingInParts } from '../time-entries/rules'
import { addPercent, partsOf, setPercent, sharesRequest } from '../time-entries/split'
import { useTimerActions } from '../time-entries/timer-actions'
import { useWorkspace } from '../workspace'
import { rank } from './rank'
import { usePalette } from './palette-context'
import { projectPage, splitPage } from './palette-pages'
import { readDayRef, readSentence, resolveDay, resolveSentence, type Sentence, type SentenceChip, type SentenceSpan } from './sentence'
import type { Catalog } from './sentence-chips'
import { PALETTE_PARTS } from './split-state'
import { SentenceFacts, ShiftDetail } from './sentence-facts'
import type { PaletteAction, PaletteItem } from './types'
import { usageScore } from './usage'

/** A timer running this long, or an entry this long, was most likely given the wrong time. */
const LONG_SECONDS = 12 * 3600
const quoted = (text: string) => `“${text}”`

export interface SentenceItems {
  sentence: Sentence
  /** The line asks for something specific: a chip, a time or billable. Its preview then comes first. */
  explicit: boolean
  /** Commands the whole line stands for, e.g. `+15` on a running timer or `yesterday`; above everything else. */
  commands: PaletteItem[]
  /** What Enter saves: the entry the line describes. Null when the line can't be an entry, e.g. with a client. */
  preview: PaletteItem | null
  /** The line as written, without reading a time from it, for when the time was meant as text. */
  asWritten: PaletteItem | null
}

/** What the line on the palette's first page says, as rows: the entry it saves, and commands it stands for. */
export function useSentenceItems(query: string): SentenceItems {
  const { workspaceId, timeZone, hour12, dateFormat } = useWorkspace()
  const { settings, isLocked, lockMessage } = usePermissions()
  const catalog = useCatalog(workspaceId)
  const timer = useTimerActions(workspaceId)
  const navigate = useNavigate()
  const { sentence: line, controls } = usePalette()
  const { data: running = null } = useQuery(currentEntryQuery)
  const now = new Date(useNow())

  const options = { hour12, dateFormat }
  const sentence = readSentence(query, line.chips, options)
  const today = dayKey(now, timeZone)
  // A duration for another day goes after that day's entries, as on the timesheet.
  const otherDay = sentence.time?.kind === 'for' && sentence.time.day ? resolveDay(sentence.time.day, today) : null
  const { data: dayEntries = [] } = useQuery({ ...dayEntriesQuery(workspaceId, otherDay ?? today, timeZone), enabled: !!otherDay && otherDay !== today })
  const span = resolveSentence(sentence.time, {
    now,
    timeZone,
    hour12,
    dayEnds: () => dayEntries.filter((entry) => entry.stop).map((entry) => new Date(entry.stop!)),
  })

  const explicit = line.chips.length > 0 || !!sentence.time || sentence.billable
  const typed = query.trim()
  const commands: PaletteItem[] = []

  // ——— A running timer's start: "+15", "-10", "started 9:40" ———

  if (running && line.chips.length === 0) {
    const shift = /^([+-−])\s*(\S+)$/.exec(typed)
    const seconds = shift ? parseDuration(shift[2]) : null
    if (shift && seconds) {
      const earlier = shift[1] === '+'
      const minutes = seconds / 60
      const amount = minutes >= 60 ? formatDurationAs(seconds, 'hm') : `${minutes} min`
      commands.push(
        startChange(running, new Date(Date.parse(running.start) + (earlier ? -1 : 1) * seconds * 1000), {
          id: 'timer.shift',
          label: earlier ? `Started ${amount} earlier` : `Started ${amount} later`,
        }),
      )
    }
    const started = /^started\s+(?:at\s+)?(.+)$/i.exec(typed)
    const at = started && readStartField(started[1], now, timeZone)
    if (at) {
      commands.push(startChange(running, at, { id: 'timer.started', label: `Started at ${formatClock(at, timeZone, hour12)}${dayNote(at)}` }))
    }
  }

  function dayNote(instant: Date) {
    const key = dayKey(instant, timeZone)
    return key === today ? '' : ` ${formatDayHeading(key, timeZone, now).toLowerCase()}`
  }

  function startChange(entry: TimeEntry, start: Date, { id, label }: { id: string; label: string }): PaletteItem {
    const problem = start.getTime() > now.getTime() ? 'That is after now.' : isLocked(start) ? lockMessage : null
    return {
      id,
      section: 'running',
      label,
      icon: problem ? TriangleAlert : Clock3,
      detail: problem ?? <ShiftDetail from={entry.start} to={start} />,
      primary: {
        label: 'Change start',
        keepOpen: !!problem,
        run: () => {
          if (!problem) {
            void timer.change(entry, { start: start.toISOString() }, `Started at ${formatClock(start, timeZone, hour12)}${dayNote(start)}`)
          }
        },
      },
    }
  }

  // ——— A day, a period, or a report on a project or client ———

  const day = line.chips.length === 0 && !typed.includes(' ') ? readDayRef(typed.toLowerCase(), options) : null
  if (day) {
    const key = resolveDay(day, today)
    const heading = formatDayHeading(key, timeZone, now)
    commands.push(
      { id: 'day.calendar', section: 'go', label: `${heading} in the calendar`, icon: CalendarDays, primary: { label: 'Open', run: () => void navigate({ to: '/calendar', search: { date: key, view: 'day' } }) } },
      { id: 'day.timesheet', section: 'go', label: `${heading} on the timesheet`, icon: SheetIcon, primary: { label: 'Open', run: () => void navigate({ to: '/timesheet', search: { date: key } }) } },
    )
  }

  const period = PERIODS.find((key): key is Exclude<PeriodKey, 'custom'> => key !== 'custom' && PERIOD_LABELS[key].toLowerCase() === typed.toLowerCase())
  const client = catalog.client(sentence.clientId)
  const project = catalog.project(sentence.projectId)
  if ((project && period) || client) {
    const name = client?.name ?? project!.name
    const label = PERIOD_LABELS[period ?? 'thisMonth']
    commands.push({
      id: 'sentence.report',
      section: 'reports',
      label: `Report: ${name} · ${label}`,
      icon: ChartColumn,
      primary: {
        label: 'Open report',
        run: () => void navigate({ to: '/reports', search: { period: period ?? 'thisMonth', ...(client ? { clients: [client.id] } : { projects: [project!.id] }) } }),
      },
    })
  }

  // ——— "split @Beacon 40": the running timer's time shared with another project ———

  const splitWith = /^split(?:\s+(\d{1,2}))?$/i.exec(sentence.description)
  if (running && project && splitWith && !sentence.time) {
    const base = partsOf(running)
    if (base.length < PALETTE_PARTS && !base.some((part) => part.projectId === project.id && part.taskId === sentence.taskId)) {
      const added = [...base, { projectId: project.id, taskId: sentence.taskId, isBillable: project.isBillable, percent: 0 }]
      let percents = addPercent(base.map((part) => part.percent))
      if (splitWith[1]) percents = setPercent(percents, base.length, Number(splitWith[1]))
      const parts = added.map((part, i) => ({ ...part, percent: percents[i] }))
      const problem = isLocked(running.start) ? lockMessage : (Object.values(missingInParts(settings, parts, catalog.project))[0] ?? null)
      const adjust: PaletteAction = { label: 'Adjust the split…', icon: Split, run: (palette) => palette.push(splitPage(running, parts)) }
      const single = { projectId: running.projectId ?? null, taskId: running.taskId ?? null, isBillable: running.isBillable }
      commands.push({
        id: 'sentence.split',
        section: 'running',
        label: `Split with ${project.name}`,
        icon: Split,
        stacked: true,
        detail: problem ? <span className="text-destructive">{problem}</span> : <SplitLabel catalog={catalog} parts={parts} className="text-[13px]" />,
        primary: problem
          ? adjust
          : {
              label: 'Save split',
              icon: Split,
              run: () => void timer.change(running, sharesRequest(parts, single), `Split: ${parts.map((part) => `${partName(catalog, part)} ${part.percent}%`).join(' · ')}`),
            },
        secondary: problem ? undefined : adjust,
      })
    }
  }

  // ——— The entry the line saves ———

  let preview: PaletteItem | null = null
  if (!client && !(project && period) && !commands.some((item) => item.id === 'sentence.split')) {
    preview = entryItem(sentence, span)
  }

  function entryItem(read: Sentence, at: SentenceSpan): PaletteItem {
    const adding = at.stop !== null
    const description = read.description
    const entryProject = catalog.project(read.projectId)
    const fields = {
      description,
      projectId: read.projectId,
      taskId: read.taskId,
      tagIds: read.tagIds,
      // Left to the project's default unless `$` asks for billable.
      isBillable: read.billable || undefined,
    }
    const billable = read.billable || !!entryProject?.isBillable
    const seconds = ((at.stop ?? now).getTime() - at.start.getTime()) / 1000
    const summary = adding ? spanText(at) : at.start.getTime() < now.getTime() - 60_000 ? `since ${formatClock(at.start, timeZone, hour12)}${dayNote(at.start)}` : ''

    let problem: string | null = null
    let primary: PaletteAction
    const missing = adding ? missingDetails(settings, fields, entryProject) : {}
    if (!adding && at.start.getTime() > now.getTime() + 60_000) {
      problem = 'A timer can’t start after now.'
    } else if (adding && at.start.getTime() > now.getTime()) {
      problem = 'That starts after now.'
    } else if (isLocked(at.start)) {
      problem = lockMessage
    }

    if (problem) {
      primary = { label: 'Can’t save', keepOpen: true, run: () => line.focus() }
    } else if (missing.projectId || missing.taskId) {
      primary = {
        label: missing.taskId ? 'Choose a task' : 'Choose a project',
        run: (palette) =>
          palette.push(
            projectPage({
              title: missing.taskId ? 'Task' : 'Project',
              current: { projectId: read.projectId, taskId: read.taskId },
              keepOpen: true,
              onPick: (projectId, taskId) => {
                controls.back()
                if (projectId) line.addChip({ kind: 'project', projectId, taskId })
              },
            }),
          ),
      }
    } else if (missing.tagIds) {
      primary = { label: 'Add a tag', keepOpen: true, run: () => line.append('#') }
    } else if (missing.description) {
      primary = { label: 'Describe it', keepOpen: true, run: () => line.focus() }
    } else if (adding) {
      primary = { label: 'Add entry', icon: ListPlus, run: () => void timer.add({ ...fields, start: at.start, stop: at.stop! }, spanText(at)) }
    } else {
      primary = { label: 'Start timer', icon: Play, run: () => void timer.start({ ...fields, start: sentence.time ? at.start : undefined }) }
    }

    const needs = Object.entries(missing).map(([field]) => NEEDS[field as keyof typeof NEEDS])
    const warning = !problem && needs.length === 0 && seconds >= LONG_SECONDS ? 'More than 12 hours. Check the times.' : null
    const facts = (
      <SentenceFacts
        read={read}
        billable={billable}
        start={at.start}
        stop={at.stop}
        summary={summary}
        stopping={!adding && running ? running : null}
        note={problem ?? (needs.length > 0 ? needs.join(' · ') : warning)}
        tone={problem || needs.length > 0 ? 'blocked' : warning ? 'warning' : null}
      />
    )

    const verb = adding ? 'Add' : 'Start'
    return {
      id: 'sentence.entry',
      section: 'fallback',
      label: description ? `${verb} ${quoted(description)}` : adding ? 'Add time' : 'Start timer',
      icon: problem || needs.length > 0 || warning ? TriangleAlert : adding ? ListPlus : Play,
      detail: facts,
      stacked: explicit,
      primary,
      secondary:
        !problem && !read.projectId
          ? {
              label: `${verb} on a project…`,
              icon: FolderKanban,
              run: (palette) =>
                palette.push(
                  projectPage({
                    title: description ? `${verb} ${quoted(description)}` : verb,
                    onPick: (projectId, taskId, picked) => {
                      const chosen = { ...fields, projectId, taskId, isBillable: read.billable || picked?.isBillable }
                      if (adding) {
                        void timer.add({ ...chosen, start: at.start, stop: at.stop! }, spanText(at))
                      } else {
                        void timer.start({ ...chosen, start: sentence.time ? at.start : undefined })
                      }
                    },
                  }),
                ),
            }
          : undefined,
    }
  }

  function spanText({ start, stop }: SentenceSpan) {
    const key = dayKey(start, timeZone)
    const dayLabel = key === today ? '' : `${formatDayHeading(key, timeZone, now)} `
    return `${dayLabel}${formatClock(start, timeZone, hour12)}–${formatClock(stop!, timeZone, hour12)}`
  }

  // The time read from the line may have been meant as text, as in "Sprint 9:00-10:30"; offered below.
  const asWritten =
    preview && sentence.time ? { ...entryItem({ ...sentence, description: query.trim().replace(/\s+/g, ' '), time: null, timeText: '' }, { start: now, stop: null }), id: 'sentence.as-written', stacked: false } : null
  if (asWritten) {
    asWritten.detail = 'as written, without reading a time'
    asWritten.secondary = undefined
  }

  return { sentence, explicit, commands, preview, asWritten }
}

const NEEDS = {
  description: 'Needs a description',
  projectId: 'Needs a project',
  taskId: 'Needs a task',
  tagIds: 'Needs a tag',
} as const

// ——— Completing `@project` and `#tag` ———

/**
 * Rows to complete the mention being typed: projects, tasks and clients after `@`, tags after `#`. Creating a tag is
 * kept apart, so it never comes first by itself.
 */
export function useMentionItems(): { matches: PaletteItem[]; create: PaletteItem | null } | null {
  const { workspaceId } = useWorkspace()
  const { canCreateTags } = usePermissions()
  const catalog = useCatalog(workspaceId)
  const saveTag = useSaveTag(workspaceId)
  const { sentence: line, usage } = usePalette()
  // Only to weigh how lately something was used, so the time the app last ticked is near enough.
  const now = useNow(false)
  const mention = line.mention
  if (!mention) return null

  const query = mention.query.trim()
  const pick = (label: string, chip: Parameters<typeof line.addChip>[0]): PaletteAction => ({ label, keepOpen: true, run: () => line.addChip(chip) })
  const order = (items: PaletteItem[], limit: number) =>
    query
      ? rank(items, query, (id) => usageScore(usage[id], now)).map(({ item }) => item).slice(0, limit)
      : [...items].sort((a, b) => usageScore(usage[b.id], now) - usageScore(usage[a.id], now)).slice(0, limit)

  if (mention.kind === '@') {
    const items: PaletteItem[] = []
    for (const project of catalog.projects.filter((p) => !p.archivedAt)) {
      const client = catalog.client(project.clientId)
      items.push({
        id: `mention:project:${project.id}`,
        section: 'mention',
        label: project.name,
        keywords: client ? [client.name] : [],
        detail: client?.name,
        leading: <ProjectDot color={project.color} />,
        primary: pick('Pick project', { kind: 'project', projectId: project.id, taskId: null }),
      })
      for (const task of project.tasks.filter((t) => !t.isDone)) {
        items.push({
          id: `mention:task:${task.id}`,
          section: 'mention',
          label: task.name,
          keywords: [project.name, client?.name ?? ''],
          detail: project.name,
          leading: <ProjectDot color={project.color} className="size-2 opacity-80" />,
          boost: -3,
          primary: pick('Pick task', { kind: 'project', projectId: project.id, taskId: task.id }),
        })
      }
    }
    // Clients only for reports, so only when asked for by name.
    if (query) {
      for (const client of catalog.clients.filter((c) => !c.archivedAt)) {
        items.push({
          id: `mention:client:${client.id}`,
          section: 'mention',
          label: client.name,
          icon: Building2,
          detail: 'Client, for a report',
          boost: -5,
          primary: pick('Pick client', { kind: 'client', clientId: client.id }),
        })
      }
    }
    return { matches: order(items, 8), create: null }
  }

  const tags: PaletteItem[] = catalog.tags
    .filter((tag) => !line.chips.some((chip) => chip.kind === 'tag' && chip.tagId === tag.id))
    .map((tag) => ({ id: `mention:tag:${tag.id}`, section: 'mention', label: tag.name, icon: TagIcon, primary: pick('Add tag', { kind: 'tag', tagId: tag.id }) }))
  const matches = order(tags, 8)
  let create: PaletteItem | null = null
  if (query && canCreateTags && !catalog.tags.some((tag) => tag.name.toLowerCase() === query.toLowerCase())) {
    create = {
      id: 'mention:create-tag',
      section: 'mention',
      label: `Create tag ${quoted(query)}`,
      icon: Plus,
      primary: {
        label: 'Create and add',
        keepOpen: true,
        run: async () => {
          try {
            const tag = await saveTag.mutateAsync({ name: query })
            line.addChip({ kind: 'tag', tagId: tag.id })
          } catch (error) {
            toast.error(errorMessage(error, "Couldn't create the tag."))
          }
        },
      },
    }
  }
  return { matches, create }
}

/** How a chip reads as text again, e.g. when Backspace turns it back: `@Acme`, `#review`, `$`. */
export function chipText(chip: SentenceChip, catalog: Catalog): string {
  switch (chip.kind) {
    case 'project':
      return `@${catalog.task(chip.taskId)?.name ?? catalog.project(chip.projectId)?.name ?? ''}`
    case 'client':
      return `@${catalog.client(chip.clientId)?.name ?? ''}`
    case 'tag':
      return `#${catalog.tag(chip.tagId)?.name ?? ''}`
    case 'billable':
      return '$'
  }
}
