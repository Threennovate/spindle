import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import {
  ArrowLeftRight,
  BadgeDollarSign,
  Bookmark,
  Building2,
  CalendarDays,
  ChartColumn,
  CircleUser,
  Clock3,
  FileDown,
  FolderKanban,
  HandCoins,
  History,
  Keyboard,
  ListPlus,
  LogOut,
  Monitor,
  Moon,
  PencilLine,
  Play,
  Plus,
  Settings,
  Sheet as SheetIcon,
  Split,
  Square,
  Sun,
  Tag as TagIcon,
  House,
  List,
  Timer,
  Trash2,
  UserPlus,
  Users,
} from 'lucide-react'
import { useState, useSyncExternalStore } from 'react'
import { toast } from 'sonner'
import type { TimeEntry, TimeEntrySuggestion } from '@/api/types'
import { session } from '@/auth/session'
import { errorMessage } from '@/lib/error-message'
import { formatHoursMinutes } from '@/lib/time/duration'
import { dayKey, formatClock } from '@/lib/time/zone'
import { useDebouncedValue } from '@/lib/use-debounced-value'
import { getAppearance, setAppearance, subscribeToAppearance, type Appearance } from '@/lib/theme'
import { ProjectDot } from '../catalog/project-label'
import { useSaveClient, useSaveTag } from '../catalog/queries'
import { SplitDots } from '../catalog/split-label'
import { useCatalog } from '../catalog/use-catalog'
import { PERIOD_LABELS, PERIODS, type PeriodKey } from '../reports/period'
import { savedReportsQuery } from '../reports/queries'
import { parseSavedParameters, type ReportSearch } from '../reports/search'
import { usePermissions } from '../settings/queries'
import { showKeyboardShortcuts } from '../shortcuts/events'
import { SHORTCUTS } from '../shortcuts/shortcuts'
import { membersQuery } from '../team/queries'
import { editEntry } from '../time-entries/events'
import { requestDescriptionFocus } from '../time-entries/focus'
import { currentEntryQuery, recentEntriesQuery, suggestionsQuery } from '../time-entries/queries'
import { equalPercents, isSplit, partsOf } from '../time-entries/split'
import { useTimerActions } from '../time-entries/timer-actions'
import { useWorkspace } from '../workspace'
import { useCreateWorkspace, useSwitchWorkspace } from '../workspace-queries'
import { namePage, projectPage, splitPage, startTimePage, tagsPage } from './palette-pages'
import { usePalette } from './palette-context'
import type { PaletteAction, PaletteItem } from './types'

const suggestionKey = (s: Pick<TimeEntrySuggestion, 'description' | 'projectId' | 'taskId'>) => `${s.description}|${s.projectId ?? ''}|${s.taskId ?? ''}`

/** Gaps shorter than this are breaks between entries, not time to add. */
const MIN_GAP_MS = 5 * 60_000
const MAX_GAP_MS = 12 * 3600_000

/** From an instant until now, e.g. to fill the gap since the last entry. */
function untilNow(start: string) {
  return { start: new Date(start), stop: new Date(Math.max(Date.now(), Date.parse(start) + 60_000)) }
}

/** The hour up to now, on five-minute marks, as a start for time added by hand. */
function lastHour() {
  const stop = new Date(Math.floor(Date.now() / 300_000) * 300_000)
  return { start: new Date(stop.getTime() - 3600_000), stop }
}

export interface RootItems {
  running: TimeEntry | null
  /** Commands and things to act on, ranked by the palette as the person types. */
  items: PaletteItem[]
  /** Earlier entries that fit what is typed, from the server; to continue. */
  entries: PaletteItem[]
  /** What the palette offers before anything is typed. */
  now: { timer: PaletteItem[]; continue: PaletteItem[] }
}

/** Everything the palette's first page offers. */
export function useRootItems(query: string): RootItems {
  const { workspaceId, workspace, me, isAdmin, canCreateWorkspaces, timeZone, hour12 } = useWorkspace()
  const { canCreateProjects, canCreateTags, canManageClients } = usePermissions()
  const catalog = useCatalog(workspaceId)
  const navigate = useNavigate()
  const timer = useTimerActions(workspaceId)
  const { dialogs } = usePalette()
  const switchWorkspace = useSwitchWorkspace()
  const createWorkspace = useCreateWorkspace()
  const saveClient = useSaveClient(workspaceId)
  const saveTag = useSaveTag(workspaceId)
  const appearance = useSyncExternalStore(subscribeToAppearance, getAppearance)
  const { data: running = null } = useQuery(currentEntryQuery)
  const { data: members = [] } = useQuery({ ...membersQuery(workspaceId), enabled: isAdmin })
  const { data: savedReports = [] } = useQuery(savedReportsQuery(workspaceId))
  const search = useDebouncedValue(query.trim(), 120)
  const { data: suggestions = [] } = useQuery({ ...suggestionsQuery(workspaceId, search), placeholderData: keepPreviousData })
  // The moment the palette opened; it is open for seconds, so that is now enough.
  const [openedAt] = useState(() => Date.now())
  const today = dayKey(new Date(openedAt), timeZone)
  const { data: recent = [] } = useQuery(recentEntriesQuery(workspaceId, today, timeZone))

  const clock = (instant: string | Date) => formatClock(instant, timeZone, hour12)

  /** Splits an entry's time from the keyboard: a project to split with first, unless it is split already. */
  function splitItem(entry: TimeEntry, item: Pick<PaletteItem, 'id' | 'section' | 'label' | 'keywords' | 'boost'>): PaletteItem {
    const split = isSplit(entry)
    return {
      ...item,
      icon: Split,
      detail: split ? partsOf(entry).map((part) => `${catalog.project(part.projectId)?.name ?? 'No project'} ${part.percent}%`).join(' · ') : undefined,
      primary: {
        label: split ? 'Edit the split' : 'Choose a project',
        run: (palette) =>
          palette.push(
            split
              ? splitPage(entry, partsOf(entry))
              : projectPage({
                  title: 'Split with',
                  keepOpen: true,
                  onPick: (projectId, taskId, project) => {
                    const percents = equalPercents(2)
                    const [whole] = partsOf(entry)
                    palette.back()
                    palette.push(splitPage(entry, [{ ...whole, percent: percents[0] }, { projectId, taskId, isBillable: project?.isBillable ?? false, percent: percents[1] }]))
                  },
                }),
          ),
      },
      more: [{ label: 'Open the full editor', icon: PencilLine, run: () => editEntry({ entry, split: true }) }],
    }
  }
  const go = (to: string, search?: ReportSearch): PaletteAction => ({ label: 'Open', run: () => void navigate({ to, search } as never) })
  const report = (filter: ReportSearch, label = 'Report this month'): PaletteAction => ({
    label,
    icon: ChartColumn,
    run: () => void navigate({ to: '/reports', search: { period: 'thisMonth', ...filter } }),
  })

  // ——— The timer ———

  const timerItems: PaletteItem[] = running
    ? [
        {
          id: 'timer.stop',
          section: 'running',
          label: 'Stop timer',
          keywords: ['end', 'finish', 'pause', 'done'],
          icon: Square,
          shortcut: SHORTCUTS.toggleTimer.keys,
          primary: { label: 'Stop', run: () => timer.stop(running) },
        },
        {
          id: 'timer.project',
          section: 'running',
          label: 'Change project…',
          keywords: ['move', 'switch project', 'task'],
          icon: FolderKanban,
          detail: catalog.project(running.projectId)?.name ?? 'No project',
          primary: {
            label: 'Choose',
            run: (palette) =>
              palette.push(
                projectPage({
                  title: 'Change project',
                  current: { projectId: running.projectId ?? null, taskId: running.taskId ?? null },
                  onPick: (projectId, taskId, project) =>
                    timer.change(
                      running,
                      { projectId, taskId, isBillable: project?.isBillable ?? running.isBillable, shares: [] },
                      project ? `Moved to ${project.name}` : 'Project removed',
                    ),
                }),
              ),
          },
        },
        {
          id: 'timer.tags',
          section: 'running',
          label: 'Tags…',
          keywords: ['label', 'add tag'],
          icon: TagIcon,
          detail: running.tagIds.map((id) => catalog.tag(id)?.name).filter(Boolean).join(', ') || undefined,
          primary: { label: 'Choose', run: (palette) => palette.push(tagsPage()) },
        },
        {
          id: 'timer.billable',
          section: 'running',
          label: running.isBillable ? 'Make non-billable' : 'Make billable',
          keywords: ['billable', 'money', 'invoice'],
          icon: BadgeDollarSign,
          primary: {
            label: running.isBillable ? 'Make non-billable' : 'Make billable',
            run: () =>
              timer.change(
                running,
                { isBillable: !running.isBillable, shares: running.shares.map((share) => ({ ...share, isBillable: !running.isBillable })) },
                running.isBillable ? 'Now non-billable' : 'Now billable',
              ),
          },
        },
        {
          id: 'timer.start-time',
          section: 'running',
          label: 'Change start time…',
          keywords: ['started', 'earlier', 'forgot', 'correct'],
          icon: Clock3,
          detail: `started ${clock(running.start)}`,
          primary: { label: 'Change', run: (palette) => palette.push(startTimePage()) },
        },
        splitItem(running, {
          id: 'timer.split',
          section: 'running',
          label: isSplit(running) ? 'Edit the split…' : 'Split with…',
          keywords: ['parallel', 'agents', 'share', 'percent', 'split time between projects'],
        }),
        {
          id: 'timer.edit',
          section: 'running',
          label: 'Edit running entry…',
          keywords: ['description', 'details', 'open'],
          icon: PencilLine,
          primary: { label: 'Edit', run: () => editEntry({ entry: running }) },
        },
        {
          id: 'timer.discard',
          section: 'running',
          label: 'Discard timer',
          keywords: ['delete', 'cancel', 'throw away', 'remove'],
          icon: Trash2,
          boost: -10,
          primary: { label: 'Discard', tone: 'destructive', run: () => timer.discard(running) },
        },
      ]
    : [
        {
          id: 'timer.start',
          section: 'timer',
          label: 'Start timer',
          keywords: ['begin', 'track', 'go'],
          icon: Play,
          shortcut: SHORTCUTS.toggleTimer.keys,
          primary: { label: 'Start', run: () => timer.start({}) },
        },
        {
          id: 'timer.new',
          section: 'timer',
          label: 'New entry on the timer',
          keywords: ['description', 'type'],
          icon: PencilLine,
          shortcut: SHORTCUTS.newEntry.keys,
          primary: {
            label: 'Go',
            run: () => {
              requestDescriptionFocus()
              void navigate({ to: '/' })
            },
          },
        },
      ]

  // The time between the last entry and now, when it looks like work that went untracked.
  const lastStop = recent
    .filter((entry) => entry.stop)
    .reduce<string | null>((latest, entry) => (!latest || entry.stop! > latest ? entry.stop! : latest), null)
  const gap = lastStop ? openedAt - Date.parse(lastStop) : 0
  if (!running && lastStop && gap >= MIN_GAP_MS && gap <= MAX_GAP_MS && dayKey(lastStop, timeZone) === today) {
    timerItems.push({
      id: 'timer.gap',
      section: 'timer',
      label: `Add the time since ${clock(lastStop)}`,
      keywords: ['gap', 'untracked', 'forgot', 'fill', 'manual'],
      icon: History,
      detail: `${formatHoursMinutes(gap / 1000)} not tracked`,
      primary: { label: 'Add', run: () => editEntry({ newEntry: untilNow(lastStop) }) },
    })
  }
  // The entry that just ended, often time that ran on two things at once.
  const lastEnded = recent
    .filter((entry): entry is TimeEntry & { stop: string } => !!entry.stop && dayKey(entry.stop, timeZone) === today)
    .reduce<(TimeEntry & { stop: string }) | null>((latest, entry) => (!latest || entry.stop > latest.stop ? entry : latest), null)
  if (lastEnded) {
    timerItems.push(
      splitItem(lastEnded, {
        id: 'timer.split-last',
        section: running ? 'running' : 'timer',
        label: `${isSplit(lastEnded) ? 'Edit the split of' : 'Split'} “${lastEnded.description || 'No description'}” (ended ${clock(lastEnded.stop)})…`,
        keywords: ['parallel', 'agents', 'share', 'percent', 'last entry'],
        boost: -4,
      }),
    )
  }
  timerItems.push({
    id: 'timer.manual',
    section: running ? 'running' : 'timer',
    label: 'Add time manually…',
    keywords: ['manual', 'past', 'log', 'entry', 'create'],
    icon: ListPlus,
    boost: -5,
    primary: {
      label: 'Add',
      run: () => editEntry({ newEntry: lastHour() }),
    },
  })

  // ——— Earlier entries to continue ———

  const toEntry = (suggestion: TimeEntrySuggestion, index: number): PaletteItem => {
    const project = catalog.project(suggestion.projectId)
    const task = catalog.task(suggestion.taskId)
    const parts = suggestion.shares.length > 1 ? partsOf({ ...suggestion, projectId: suggestion.projectId ?? null, taskId: suggestion.taskId ?? null }) : null
    return {
      id: `entry:${suggestionKey(suggestion)}`,
      section: 'continue',
      label: suggestion.description || 'No description',
      keywords: [project?.name ?? '', task?.name ?? ''],
      leading: parts ? <SplitDots catalog={catalog} parts={parts} /> : project ? <ProjectDot color={project.color} /> : <Play className="text-muted-foreground size-3.5" />,
      detail: parts ? parts.map((part) => catalog.project(part.projectId)?.name ?? 'No project').join(' · ') : [project?.name, task?.name].filter(Boolean).join(' · ') || undefined,
      // The first one is what `c` continues.
      shortcut: index === 0 && !query.trim() ? SHORTCUTS.continueLatest.keys : undefined,
      primary: { label: running ? 'Switch to this' : 'Continue', icon: Play, run: () => timer.continueSuggestion(suggestion) },
    }
  }
  const runningKey = running ? suggestionKey(running) : null
  const entries = suggestions.filter((s) => suggestionKey(s) !== runningKey).map(toEntry)

  // ——— Things to act on ———

  const items: PaletteItem[] = [...timerItems]
  for (const project of catalog.projects.filter((p) => !p.archivedAt)) {
    const client = catalog.client(project.clientId)
    items.push({
      id: `project:${project.id}`,
      section: 'projects',
      label: project.name,
      keywords: [client?.name ?? ''],
      leading: <ProjectDot color={project.color} />,
      detail: client?.name,
      meta: project.trackedSeconds > 0 ? formatHoursMinutes(project.trackedSeconds) : undefined,
      primary: { label: running ? 'Start timer on it' : 'Start timer', icon: Play, run: () => timer.start({ projectId: project.id }) },
      secondary: { label: 'Open project', icon: FolderKanban, run: () => void navigate({ to: '/projects/$projectId', params: { projectId: project.id } }) },
      more: [
        ...(running && running.projectId !== project.id
          ? [
              {
                label: 'Move the running timer here',
                icon: ArrowLeftRight,
                run: () => timer.change(running, { projectId: project.id, taskId: null, isBillable: project.isBillable, shares: [] }, `Moved to ${project.name}`),
              },
            ]
          : []),
        report({ projects: [project.id] }),
        ...(project.canManage ? [{ label: 'Edit project…', icon: PencilLine, run: () => dialogs.project({ project }) }] : []),
      ],
    })
    for (const task of project.tasks.filter((t) => !t.isDone)) {
      items.push({
        id: `task:${task.id}`,
        section: 'tasks',
        label: task.name,
        keywords: [project.name, client?.name ?? ''],
        leading: <ProjectDot color={project.color} className="size-2 opacity-80" />,
        detail: project.name,
        meta: task.trackedSeconds > 0 ? formatHoursMinutes(task.trackedSeconds) : undefined,
        boost: -3,
        primary: { label: 'Start timer', icon: Play, run: () => timer.start({ projectId: project.id, taskId: task.id }) },
        secondary: { label: 'Open project', icon: FolderKanban, run: () => void navigate({ to: '/projects/$projectId', params: { projectId: project.id } }) },
        more:
          running && running.taskId !== task.id
            ? [
                {
                  label: 'Move the running timer here',
                  icon: ArrowLeftRight,
                  run: () => timer.change(running, { projectId: project.id, taskId: task.id, isBillable: project.isBillable, shares: [] }, `Moved to ${task.name}`),
                },
              ]
            : [],
      })
    }
  }
  for (const client of catalog.clients.filter((c) => !c.archivedAt)) {
    items.push({
      id: `client:${client.id}`,
      section: 'clients',
      label: client.name,
      icon: Building2,
      meta: client.activeProjectCount === 1 ? '1 project' : `${client.activeProjectCount} projects`,
      primary: report({ clients: [client.id] }),
      secondary: { label: 'Open clients', icon: Building2, run: () => void navigate({ to: '/clients' }) },
      more: [report({ clients: [client.id], period: 'lastMonth' }, 'Report last month'), report({ clients: [client.id], period: 'thisYear' }, 'Report this year')],
    })
  }
  for (const tag of catalog.tags) {
    const onRunning = running?.tagIds.includes(tag.id)
    items.push({
      id: `tag:${tag.id}`,
      section: 'tags',
      label: tag.name,
      icon: TagIcon,
      boost: -2,
      primary: report({ tags: [tag.id] }),
      secondary: running
        ? {
            label: onRunning ? 'Remove from the timer' : 'Add to the timer',
            icon: TagIcon,
            run: () =>
              timer.change(
                running,
                { tagIds: onRunning ? running.tagIds.filter((id) => id !== tag.id) : [...running.tagIds, tag.id] },
                onRunning ? `Removed “${tag.name}”` : `Tagged “${tag.name}”`,
              ),
          }
        : undefined,
    })
  }
  for (const member of members) {
    items.push({
      id: `member:${member.userId}`,
      section: 'members',
      label: member.fullName,
      keywords: [member.email],
      icon: CircleUser,
      detail: member.email,
      boost: -2,
      primary: report({ users: [member.userId] }),
      secondary: { label: 'Open team', icon: Users, run: () => void navigate({ to: '/team' }) },
    })
  }

  // ——— Reports ———

  for (const period of PERIODS.filter((p): p is Exclude<PeriodKey, 'custom'> => p !== 'custom')) {
    items.push({
      id: `report:${period}`,
      section: 'reports',
      label: `Report: ${PERIOD_LABELS[period]}`,
      keywords: ['summary', 'hours', 'total'],
      icon: ChartColumn,
      boost: period === 'thisWeek' || period === 'thisMonth' || period === 'lastMonth' ? 0 : -4,
      primary: go('/reports', { period }),
    })
  }
  items.push(
    { id: 'report:detailed', section: 'reports', label: 'Detailed report', keywords: ['entries', 'list'], icon: ChartColumn, primary: go('/reports', { tab: 'detailed' }) },
    { id: 'report:weekly', section: 'reports', label: 'Weekly report', keywords: ['week', 'days'], icon: ChartColumn, primary: go('/reports', { tab: 'weekly' }) },
  )
  for (const saved of savedReports) {
    items.push({
      id: `saved-report:${saved.id}`,
      section: 'reports',
      label: saved.name,
      keywords: ['saved report'],
      icon: Bookmark,
      detail: 'Saved report',
      primary: go('/reports', { ...parseSavedParameters(saved.parameters), saved: saved.id }),
    })
  }

  // ——— Pages ———

  type Page = [to: string, label: string, icon: typeof Timer, keywords: string[], shortcut?: readonly string[]]
  const pages: Page[] = [
    ['/', 'Home', House, ['timer', 'track', 'week', 'dashboard', 'start'], SHORTCUTS.goHome.keys],
    ['/entries', 'Entries', List, ['time entries', 'list', 'history', 'timer'], SHORTCUTS.goEntries.keys],
    ['/calendar', 'Calendar', CalendarDays, ['week', 'day', 'schedule'], SHORTCUTS.goCalendar.keys],
    ['/timesheet', 'Timesheet', SheetIcon, ['week', 'grid', 'hours'], SHORTCUTS.goTimesheet.keys],
    ['/reports', 'Reports', ChartColumn, ['analytics', 'summary', 'export'], SHORTCUTS.goReports.keys],
    ...(isAdmin ? [['/profitability', 'Profitability', HandCoins, ['agreements', 'retainer', 'fixed price', 'budget', 'margin']] satisfies Page] : []),
    ['/projects', 'Projects', FolderKanban, ['work', 'estimates'], SHORTCUTS.goProjects.keys],
    ['/clients', 'Clients', Building2, ['customers']],
    ['/tags', 'Tags', TagIcon, ['labels']],
    ['/team', 'Team', Users, ['members', 'people', 'rates', 'groups']],
    ['/settings', 'Workspace settings', Settings, ['preferences', 'lock', 'rules', 'billing']],
    ['/account', 'Account settings', CircleUser, ['profile', 'password', 'passkey', 'email', 'time zone']],
  ]
  if (isAdmin) {
    pages.push(['/import', 'Import data', FileDown, ['toggl', 'clockify', 'harvest', 'csv', 'export', 'migrate']])
  }
  for (const [to, label, icon, keywords, shortcut] of pages) {
    items.push({ id: `go:${to}`, section: 'go', label, keywords: [`Go to ${label}`, ...keywords], icon, shortcut, boost: -1, primary: go(to) })
  }

  // ——— Creating ———

  if (canCreateProjects) {
    items.push({
      id: 'create.project',
      section: 'create',
      label: 'New project…',
      keywords: ['add project', 'create'],
      icon: Plus,
      primary: { label: 'Create', run: () => dialogs.project({ onSaved: (project) => void navigate({ to: '/projects/$projectId', params: { projectId: project.id } }) }) },
    })
  }
  if (canManageClients) {
    items.push({
      id: 'create.client',
      section: 'create',
      label: 'New client…',
      keywords: ['add client', 'create', 'customer'],
      icon: Plus,
      primary: {
        label: 'Create',
        run: (palette) =>
          palette.push(
            namePage({
              title: 'New client',
              noun: 'client',
              placeholder: 'Client name',
              onCreate: async (name) => {
                try {
                  await saveClient.mutateAsync({ name })
                  toast.success(`Client “${name}” created`, { action: { label: 'Open', onClick: () => void navigate({ to: '/clients' }) } })
                  return null
                } catch (error) {
                  toast.error(errorMessage(error, "Couldn't create the client."))
                  return 'failed'
                }
              },
            }),
          ),
      },
    })
  }
  if (canCreateTags) {
    items.push({
      id: 'create.tag',
      section: 'create',
      label: 'New tag…',
      keywords: ['add tag', 'create', 'label'],
      icon: Plus,
      primary: {
        label: 'Create',
        run: (palette) =>
          palette.push(
            namePage({
              title: 'New tag',
              noun: 'tag',
              placeholder: 'Tag name',
              onCreate: async (name) => {
                try {
                  await saveTag.mutateAsync({ name })
                  toast.success(`Tag “${name}” created`)
                  return null
                } catch (error) {
                  toast.error(errorMessage(error, "Couldn't create the tag."))
                  return 'failed'
                }
              },
            }),
          ),
      },
    })
  }
  if (isAdmin) {
    items.push({ id: 'create.invite', section: 'create', label: 'Invite someone…', keywords: ['member', 'team', 'add person', 'email'], icon: UserPlus, primary: { label: 'Invite', run: () => dialogs.invite() } })
  }
  if (canCreateWorkspaces) {
    items.push({
      id: 'create.workspace',
      section: 'create',
      label: 'New workspace…',
      keywords: ['create', 'organization'],
      icon: Plus,
      boost: -4,
      primary: {
        label: 'Create',
        run: (palette) =>
          palette.push(
            namePage({
              title: 'New workspace',
              noun: 'workspace',
              placeholder: 'Workspace name, e.g. Consulting',
              onCreate: async (name) => {
                try {
                  await createWorkspace.mutateAsync(name)
                  toast.success(`Switched to ${name}`)
                  void navigate({ to: '/' })
                  return null
                } catch (error) {
                  toast.error(errorMessage(error, "Couldn't create the workspace."))
                  return 'failed'
                }
              },
            }),
          ),
      },
    })
  }

  // ——— Workspaces, appearance and account ———

  for (const other of me.workspaces.filter((w) => w.id !== workspace.id)) {
    items.push({
      id: `workspace:${other.id}`,
      section: 'workspaces',
      label: `Switch to ${other.name}`,
      keywords: ['workspace', other.name],
      icon: ArrowLeftRight,
      primary: {
        label: 'Switch',
        run: () =>
          switchWorkspace.mutate(other.id, {
            onSuccess: () => {
              toast.success(`Switched to ${other.name}`)
              // Pages such as a project's belong to the old workspace, so start over on Home.
              void navigate({ to: '/' })
            },
            onError: (error) => toast.error(errorMessage(error, "Couldn't switch workspaces.")),
          }),
      },
    })
  }

  const appearances: [Appearance, string, typeof Sun, string[]][] = [
    ['light', 'Light appearance', Sun, ['theme', 'light mode', 'day']],
    ['dark', 'Dark appearance', Moon, ['theme', 'dark mode', 'night']],
    ['system', 'Match the system appearance', Monitor, ['theme', 'auto', 'system']],
  ]
  for (const [value, label, icon, keywords] of appearances) {
    items.push({ id: `appearance:${value}`, section: 'appearance', label, keywords, icon, checked: appearance === value, boost: -3, primary: { label: 'Use', run: () => setAppearance(value) } })
  }

  items.push(
    { id: 'help.shortcuts', section: 'account', label: 'Keyboard shortcuts', keywords: ['keys', 'help', 'hotkeys'], icon: Keyboard, shortcut: SHORTCUTS.help.keys, primary: { label: 'Show', run: showKeyboardShortcuts } },
    { id: 'account.sign-out', section: 'account', label: 'Sign out', keywords: ['log out', 'logout', 'exit'], icon: LogOut, boost: -6, primary: { label: 'Sign out', run: () => void session.signOut() } },
  )

  return {
    running,
    items,
    entries: entries.map((entry) => ({ ...entry, section: 'entries' })),
    // Discarding stays out of the first view, found only when searched for.
    now: { timer: timerItems.filter((item) => item.primary.tone !== 'destructive'), continue: entries.slice(0, running ? 3 : 4) },
  }
}
