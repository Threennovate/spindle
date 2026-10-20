import { useQuery } from '@tanstack/react-query'
import { Clock3, CornerDownLeft, FolderX, History, Plus, Tag as TagIcon, TriangleAlert } from 'lucide-react'
import { toast } from 'sonner'
import type { Project, TimeEntry } from '@/api/types'
import { errorMessage } from '@/lib/error-message'
import { formatDuration } from '@/lib/time/duration'
import { dayKey, formatClock, formatDayHeading, readStartField } from '@/lib/time/zone'
import { useNow } from '@/lib/use-now'
import { ProjectDot } from '../catalog/project-label'
import { useCatalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { currentEntryQuery, recentEntriesQuery } from '../time-entries/queries'
import { useTimerActions } from '../time-entries/timer-actions'
import { useWorkspace } from '../workspace'
import { useSaveTag } from '../catalog/queries'
import { rank } from './rank'
import { usePalette } from './palette-context'
import { PaletteGroup, PaletteNote, PaletteRow } from './palette-row'
import type { PaletteAction, PaletteControls, PaletteItem } from './types'

// ——— Picking a project ———

export interface ProjectPageOptions {
  title: string
  /** The project chosen now, marked in the list. */
  current?: { projectId: string | null; taskId: string | null }
  onPick: (projectId: string | null, taskId: string | null, project: Project | undefined) => unknown
  /** Picking only steps back with the choice, e.g. to show what a typed line will save, instead of saving. */
  keepOpen?: boolean
}

export function ProjectPage({ query, current, onPick, keepOpen }: ProjectPageOptions & { query: string }) {
  const { workspaceId } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { canCreateProjects } = usePermissions()
  const { usage, dialogs, controls } = usePalette()
  const typed = query.trim()

  const pick = (projectId: string | null, taskId: string | null): PaletteAction => ({
    label: 'Choose',
    keepOpen,
    run: () => onPick(projectId, taskId, catalog.project(projectId)),
  })

  const items: PaletteItem[] = [
    {
      id: 'pick:none',
      section: 'projects',
      label: 'No project',
      icon: FolderX,
      checked: current?.projectId === null,
      primary: pick(null, null),
    },
  ]
  for (const project of catalog.projects.filter((p) => !p.archivedAt || p.id === current?.projectId)) {
    const client = catalog.client(project.clientId)
    items.push({
      id: `project:${project.id}`,
      section: 'projects',
      label: project.name,
      keywords: client ? [client.name] : [],
      detail: client?.name,
      leading: <ProjectDot color={project.color} />,
      checked: current?.projectId === project.id && !current.taskId,
      primary: pick(project.id, null),
    })
    for (const task of project.tasks.filter((t) => !t.isDone || t.id === current?.taskId)) {
      items.push({
        id: `task:${task.id}`,
        section: 'tasks',
        label: task.name,
        keywords: [project.name, client?.name ?? ''],
        detail: project.name,
        leading: <ProjectDot color={project.color} className="size-1.5 opacity-70" />,
        checked: current?.taskId === task.id,
        primary: pick(project.id, task.id),
      })
    }
  }

  const create = canCreateProjects && (
    <PaletteGroup>
      <PaletteRow
        item={{
          id: 'pick:create-project',
          section: 'create',
          label: typed ? `Create project “${typed}”` : 'Create a new project',
          icon: Plus,
          primary: {
            label: 'Create',
            run: () => {
              controls.close()
              dialogs.project({ initialName: typed, onSaved: (project) => onPick(project.id, null, project) })
            },
          },
        }}
      />
    </PaletteGroup>
  )

  if (typed) {
    const ranked = rank(items, typed, (id) => (usage[id] ? 0.5 : 0)).slice(0, 12)
    return (
      <>
        {ranked.length > 0 && (
          <PaletteGroup>
            {ranked.map(({ item }) => (
              <PaletteRow key={item.id} item={item} query={typed} />
            ))}
          </PaletteGroup>
        )}
        {create}
      </>
    )
  }

  // Grouped by client, as in the project picker, so a long list stays readable.
  const groups = new Map<string, PaletteItem[]>()
  for (const item of items.slice(1)) {
    const projectId = item.id.startsWith('project:') ? item.id.slice(8) : catalog.task(item.id.slice(5))?.projectId
    const heading = catalog.client(catalog.project(projectId)?.clientId)?.name ?? 'No client'
    groups.set(heading, [...(groups.get(heading) ?? []), item])
  }
  const headings = [...groups.keys()].sort((a, b) => (a === 'No client' ? -1 : b === 'No client' ? 1 : a.localeCompare(b)))

  return (
    <>
      <PaletteGroup>
        <PaletteRow item={items[0]} />
      </PaletteGroup>
      {headings.map((heading) => (
        <PaletteGroup key={heading} heading={heading}>
          {groups.get(heading)!.map((item) => (
            <PaletteRow key={item.id} item={{ ...item, detail: undefined }} className={item.section === 'tasks' ? 'pl-7' : undefined} />
          ))}
        </PaletteGroup>
      ))}
      {create}
    </>
  )
}

// ——— Tags of the running entry ———

/** Ticks tags on and off the running entry; the page stays open, so several can be changed in a row. */
export function TagsPage({ query }: { query: string }) {
  const { workspaceId } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { canCreateTags } = usePermissions()
  const { data: running } = useQuery(currentEntryQuery)
  const timer = useTimerActions(workspaceId)
  const saveTag = useSaveTag(workspaceId)
  const typed = query.trim()

  if (!running) {
    return <PaletteNote>The timer stopped, so there are no tags to change.</PaletteNote>
  }

  const toggle = (entry: TimeEntry, tagId: string, name: string) => {
    const on = entry.tagIds.includes(tagId)
    void timer.change(entry, { tagIds: on ? entry.tagIds.filter((id) => id !== tagId) : [...entry.tagIds, tagId] }, on ? `Removed “${name}”` : `Tagged “${name}”`)
  }

  const items: PaletteItem[] = catalog.tags.map((tag) => ({
    id: `tag:${tag.id}`,
    section: 'tags',
    label: tag.name,
    icon: TagIcon,
    checked: running.tagIds.includes(tag.id),
    primary: { label: running.tagIds.includes(tag.id) ? 'Remove' : 'Add', keepOpen: true, run: () => toggle(running, tag.id, tag.name) },
  }))
  // Tags on the entry first, so what is set can be seen at a glance.
  const shown = typed ? rank(items, typed, () => 0).map(({ item }) => item) : [...items].sort((a, b) => Number(!!b.checked) - Number(!!a.checked))
  const exists = catalog.tags.some((tag) => tag.name.toLowerCase() === typed.toLowerCase())

  return (
    <>
      {shown.length > 0 ? (
        <PaletteGroup>
          {shown.map((item) => (
            <PaletteRow key={item.id} item={item} query={typed} />
          ))}
        </PaletteGroup>
      ) : (
        !typed && <PaletteNote>No tags yet. Type a name to create the first one.</PaletteNote>
      )}
      {typed && !exists && canCreateTags && (
        <PaletteGroup>
          <PaletteRow
            item={{
              id: 'tags:create',
              section: 'create',
              label: `Create tag “${typed}”`,
              icon: Plus,
              primary: {
                label: 'Create and add',
                keepOpen: true,
                run: async (palette) => {
                  try {
                    const tag = await saveTag.mutateAsync({ name: typed })
                    toggle(running, tag.id, tag.name)
                    palette.setQuery('')
                  } catch (error) {
                    toast.error(errorMessage(error, "Couldn't create the tag."))
                  }
                },
              },
            }}
          />
        </PaletteGroup>
      )}
    </>
  )
}

// ——— When the running timer started ———

/** A timer running this long was most likely started at the wrong time, e.g. 21:15 yesterday instead of 20:15 today. */
const LONG_RUN_SECONDS = 12 * 3600

export function StartTimePage({ query }: { query: string }) {
  const { workspaceId, timeZone, hour12 } = useWorkspace()
  const { isLocked, lockMessage } = usePermissions()
  const { data: running } = useQuery(currentEntryQuery)
  const timer = useTimerActions(workspaceId)
  const now = useNow()
  const today = dayKey(new Date(now), timeZone)
  const { data: recent } = useQuery(recentEntriesQuery(workspaceId, today, timeZone))

  if (!running) {
    return <PaletteNote>The timer stopped, so there is no start time to change.</PaletteNote>
  }

  const entry = running
  const clock = (instant: Date | string) => formatClock(instant, timeZone, hour12)
  const day = (instant: Date) => {
    const key = dayKey(instant, timeZone)
    return key === today ? '' : ` ${formatDayHeading(key, timeZone, new Date(now)).toLowerCase()}`
  }

  function option(id: string, start: Date, label: string, detail: string, warn = false): PaletteItem {
    const seconds = (now - start.getTime()) / 1000
    const locked = isLocked(start)
    return {
      id,
      section: 'timer',
      label,
      detail: locked ? lockMessage : detail,
      icon: warn || locked ? TriangleAlert : Clock3,
      meta: <span className="font-mono">{formatDuration(seconds)}</span>,
      primary: {
        label: 'Set start time',
        // A locked day can't be chosen; the row says why and the palette stays.
        keepOpen: locked,
        run: () => {
          if (!locked) {
            void timer.change(entry, { start: start.toISOString() }, `Started at ${clock(start)}${day(start)}`)
          }
        },
      },
    }
  }

  const typed = query.trim()
  if (typed) {
    const start = readStartField(typed, new Date(now), timeZone)
    if (!start) {
      return <PaletteNote>Type a time such as 9:40 or 940, or how long ago, such as -15 or -1:30.</PaletteNote>
    }
    const seconds = (now - start.getTime()) / 1000
    const long = seconds >= LONG_RUN_SECONDS
    return (
      <PaletteGroup>
        <PaletteRow
          item={option(
            'start:typed',
            start,
            `Started at ${clock(start)}${day(start)}`,
            long ? 'That is more than 12 hours ago. Check the day.' : `was ${clock(entry.start)}`,
            long,
          )}
        />
      </PaletteGroup>
    )
  }

  // The entry that ended last, which the timer usually picks up from.
  const previous = (recent ?? [])
    .filter((e): e is TimeEntry & { stop: string } => e.id !== entry.id && !!e.stop && Date.parse(e.stop) <= now)
    .reduce<(TimeEntry & { stop: string }) | null>((latest, e) => (!latest || e.stop > latest.stop ? e : latest), null)
  const started = Math.floor(Date.parse(entry.start) / 60_000) * 60_000
  const options: PaletteItem[] = []
  if (previous && Math.abs(Date.parse(previous.stop) - started) >= 60_000 && now - Date.parse(previous.stop) < LONG_RUN_SECONDS * 1000) {
    options.push({
      ...option('start:previous', new Date(previous.stop), `When the last entry ended, ${clock(previous.stop)}`, previous.description || 'No description'),
      icon: History,
    })
  }
  for (const minutes of [5, 15, 30, 60]) {
    options.push(option(`start:ago-${minutes}`, new Date(Math.floor((now - minutes * 60_000) / 60_000) * 60_000), minutes === 60 ? 'An hour ago' : `${minutes} minutes ago`, ''))
  }

  return (
    <>
      <div className="text-muted-foreground flex items-center gap-2 px-5 pt-3 pb-1 text-xs">
        <CornerDownLeft className="size-3" aria-hidden="true" />
        Started at {clock(entry.start)}
        {day(new Date(entry.start))}. Type another time, or pick one.
      </div>
      <PaletteGroup>
        {options.map((item) => (
          <PaletteRow key={item.id} item={item} />
        ))}
      </PaletteGroup>
    </>
  )
}

// ——— Naming something new ———

export interface NamePageOptions {
  title: string
  noun: string
  placeholder: string
  /** Creates it; returns an error to show, or nothing. */
  onCreate: (name: string) => Promise<string | null>
}

export function NamePage({ noun, onCreate, query }: NamePageOptions & { query: string }) {
  const typed = query.trim()
  if (!typed) {
    return <PaletteNote>Type a name for the new {noun}, then press Enter.</PaletteNote>
  }
  return (
    <PaletteGroup>
      <PaletteRow
        item={{
          id: `create:${noun}`,
          section: 'create',
          label: `Create ${noun} “${typed}”`,
          icon: Plus,
          primary: {
            label: 'Create',
            run: (palette) => {
              palette.close()
              void onCreate(typed)
            },
          },
        }}
      />
    </PaletteGroup>
  )
}

// ——— Everything that can be done with one item ———

export function ActionsPage({ item, query }: { item: PaletteItem; query: string }) {
  const { record } = usePalette()
  // Any of its actions counts as using the item.
  const actions = [item.primary, ...(item.secondary ? [item.secondary] : []), ...(item.more ?? [])].map((action) => ({
    ...action,
    run: (palette: PaletteControls) => {
      record(item.id)
      return action.run(palette)
    },
  }))
  const rows: PaletteItem[] = actions.map((action, index) => ({
    id: `${item.id}:action-${index}`,
    section: item.section,
    label: action.label,
    icon: action.icon,
    shortcut: index === 0 ? ['enter'] : index === 1 && item.secondary ? ['mod', 'enter'] : action.shortcut,
    primary: action,
  }))
  const shown = query.trim() ? rank(rows, query, () => 0).map(({ item: row }) => row) : rows
  return (
    <PaletteGroup>
      {shown.map((row) => (
        <PaletteRow key={row.id} item={row} query={query} />
      ))}
    </PaletteGroup>
  )
}
