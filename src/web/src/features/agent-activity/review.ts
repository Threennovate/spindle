import type { components } from '@/api/schema'
import type { SplitSuggestion, SplitTarget, TimeEntry, UntrackedTime, WorkspaceSettings } from '@/api/types'
import { dayKey, formatClock } from '@/lib/time/zone'
import type { Catalog } from '../catalog/use-catalog'
import { missingDetails, missingInParts, type DetailField } from '../time-entries/rules'
import { isSplit, sharesRequest, type SplitPart } from '../time-entries/split'
import { partSeconds, separateTitle, suggestedParts, suggestedStretches, type Stretch } from './suggestion'
import { untrackedId } from './untracked'

type TimeEntryRequest = components['schemas']['TimeEntryRequest']

/**
 * One thing the day's review offers, in its place on the day, as on the Mac: a split for a finished entry on one
 * project, or agent time nobody tracked. The Mac's third kind, a split whose work changed while it ran ("End split at
 * 11:05"), comes from the running timer, which only the desktop apps watch.
 */
export type ReviewItem =
  | { kind: 'split'; id: string; day: string; start: string; end: string; entry: TimeEntry; suggestion: SplitSuggestion }
  | { kind: 'untracked'; id: string; day: string; start: string; end: string; block: UntrackedTime }

interface ItemsInput {
  days: readonly string[]
  /** The person's entries on those days, to find what each suggestion is for. */
  entries: readonly TimeEntry[]
  suggestions: readonly SplitSuggestion[]
  untracked: readonly UntrackedTime[]
  timeZone: string
  isDayLocked: (day: string) => boolean
  /** Told "Not now" in the review, in this browser. */
  isPutOff: (id: string) => boolean
  /** Ghost blocks dismissed in the calendar, in this browser. */
  isDismissed: (block: UntrackedTime) => boolean
}

/**
 * What the review offers on these days, in the order of the day: splits of finished entries on one project and agent
 * time nobody tracked. Leaves out running entries, locked days, what was kept as it is (the server leaves those out),
 * ghosts dismissed in the calendar and what was put off with "Not now". Weak suggestions are here too; this is where
 * they wait.
 */
export function reviewItems({ days, entries, suggestions, untracked, timeZone, isDayLocked, isPutOff, isDismissed }: ItemsInput): ReviewItem[] {
  const wanted = new Set(days.filter((day) => !isDayLocked(day)))
  const byId = new Map(entries.map((entry) => [entry.id, entry]))
  const items: ReviewItem[] = []
  for (const suggestion of suggestions) {
    const entry = byId.get(suggestion.entryId)
    const day = entry && dayKey(entry.start, timeZone)
    if (!entry?.stop || isSplit(entry) || !day || !wanted.has(day)) {
      continue
    }
    items.push({ kind: 'split', id: `split|${entry.id}`, day, start: entry.start, end: entry.stop, entry, suggestion })
  }
  for (const block of untracked) {
    const day = dayKey(block.start, timeZone)
    if (wanted.has(day) && !isDismissed(block)) {
      items.push({ kind: 'untracked', id: `untracked|${untrackedId(block, timeZone)}`, day, start: block.start, end: block.end, block })
    }
  }
  return items.filter((item) => !isPutOff(item.id)).sort((a, b) => Date.parse(a.start) - Date.parse(b.start))
}

/** "2 splits, 1 untracked stretch", as the Mac counts them. */
export function breakdown(items: readonly ReviewItem[]): string {
  const splits = items.filter((item) => item.kind === 'split').length
  const untracked = items.length - splits
  return [
    splits === 0 ? null : splits === 1 ? '1 split' : `${splits} splits`,
    untracked === 0 ? null : untracked === 1 ? '1 untracked stretch' : `${untracked} untracked stretches`,
  ]
    .filter(Boolean)
    .join(', ')
}

/** The item's button: "Split", "Split at 10:42", "Split into 3 entries" or "Add entry". */
export function actionTitle(item: ReviewItem, timeZone: string, hour12: boolean): string {
  return item.kind === 'untracked' ? 'Add entry' : item.suggestion.stretches ? separateTitle(item.suggestion, timeZone, hour12) : 'Split'
}

/** What one click on an item said, for its notice: "Split saved", "Added Atlas 14:00–15:30". */
export function takenTitle(item: ReviewItem, catalog: Catalog, timeZone: string, hour12: boolean): string {
  if (item.kind === 'split') {
    return 'Split saved'
  }
  const name = catalog.project(item.block.projectIds[0])?.name ?? 'an entry'
  return `Added ${name} ${formatClock(item.start, timeZone, hour12)}–${formatClock(item.end, timeZone, hour12)}`
}

/** One of the entries an item leaves behind, as the row shows it: on a project, or shared between several. */
export interface Outcome {
  start: string
  end: string
  /** A project's or repository's share of it; one for an entry on one project. */
  parts: { target: SplitTarget; seconds: number }[]
}

/**
 * What the time becomes once the item is taken: one entry split between projects, separate entries cut where the work
 * moved, or a new entry for untracked time, on one project or split. Repositories without a project show as they are.
 */
export function outcomes(item: ReviewItem): Outcome[] {
  const seconds = (Date.parse(item.end) - Date.parse(item.start)) / 1000
  const split = item.kind === 'split' ? item.suggestion : untrackedSplit(item.block)
  if (!split) {
    // Untracked time on one project, or only in repositories without one.
    const projectId = item.kind === 'untracked' ? (item.block.projectIds[0] ?? null) : null
    return [{ start: item.start, end: item.end, parts: [{ target: { projectId }, seconds }] }]
  }
  if (split.stretches) {
    return split.stretches.map((stretch, index, all) => {
      const start = index === 0 ? item.start : stretch.start
      const end = index === all.length - 1 ? item.end : all[index + 1].start
      return { start, end, parts: [{ target: stretch.target, seconds: (Date.parse(end) - Date.parse(start)) / 1000 }] }
    })
  }
  const times = partSeconds(split, seconds)
  return [{ start: item.start, end: item.end, parts: split.parts.map((part, index) => ({ target: part.target, seconds: times[index] })) }]
}

/**
 * How untracked time is saved, as `untrackedEntry` opens it: its stretches or its split once every repository has a
 * project, else one entry on the project most of it went to (null).
 */
function untrackedSplit(block: UntrackedTime): Pick<SplitSuggestion, 'parts' | 'stretches'> | null {
  if (!block.parts) {
    return null
  }
  const linked = (targets: readonly { target: SplitTarget }[]) => targets.every((each) => !!each.target.projectId)
  if (block.stretches && linked(block.stretches)) {
    return { parts: block.parts, stretches: block.stretches }
  }
  return linked(block.parts) ? { parts: block.parts, stretches: null } : null
}

/** What saving an item takes: the entry it changes and the entries it adds. */
export interface TakeRequests {
  update: { entry: TimeEntry; body: TimeEntryRequest } | null
  create: TimeEntryRequest[]
}

/**
 * Whether one click can take an item, and what it saves: ready, or why it needs the editor (a repository without a
 * project, or details the workspace requires that a suggestion can't fill in, such as a task).
 */
export type TakePlan = { status: 'ready'; requests: TakeRequests } | { status: 'unlinked' } | { status: 'details'; reason: string }

interface PlanContext {
  catalog: Catalog
  settings: Pick<WorkspaceSettings, 'requireDescription' | 'requireProject' | 'requireTask' | 'requireTag'>
}

/**
 * What taking an item saves, as the Mac's `SplitApplier` saves it. A split by percent changes the entry's shares; the
 * part on its own project keeps its task and billable setting, the others follow their project. Separate entries make
 * the entry the last stretch and add the earlier ones before it, each with the description and tags. Untracked time
 * becomes a new entry with no description, on its project, its split or its stretches.
 */
export function takePlan(item: ReviewItem, { catalog, settings }: PlanContext): TakePlan {
  if (item.kind === 'split') {
    const { entry, suggestion } = item
    const own = { projectId: entry.projectId ?? null, taskId: entry.taskId ?? null, isBillable: entry.isBillable }
    if (suggestion.stretches) {
      const stretches = suggestedStretches(suggestion)
      if (!stretches) {
        return { status: 'unlinked' }
      }
      const requests = stretchRequests(stretches, entry, own, catalog)
      const missing = requests && missingIn(requests, settings, catalog)
      return !requests
        ? { status: 'details', reason: 'The times no longer fit.' }
        : missing
          ? { status: 'details', reason: missing }
          : { status: 'ready', requests: { update: { entry, body: requests.at(-1)! }, create: requests.slice(0, -1) } }
    }
    const suggested = suggestedParts(suggestion, catalog)
    if (!suggested) {
      return { status: 'unlinked' }
    }
    const parts = suggested.map((part) => (part.projectId === own.projectId ? { ...part, taskId: own.taskId, isBillable: own.isBillable } : part))
    const body: TimeEntryRequest = {
      description: entry.description,
      ...sharesRequest(parts, own),
      tagIds: entry.tagIds,
      start: entry.start,
      stop: entry.stop,
    }
    const missing = missingIn([body], settings, catalog, parts)
    return missing ? { status: 'details', reason: missing } : { status: 'ready', requests: { update: { entry, body }, create: [] } }
  }

  const { block } = item
  const projectId = block.projectIds[0]
  if (!projectId) {
    return { status: 'unlinked' }
  }
  const split = { parts: block.parts ?? [], stretches: block.stretches ?? null }
  const stretches = block.parts ? suggestedStretches(split) : null
  const parts = !stretches && block.parts ? suggestedParts(split, catalog) : null
  const base = { description: '', tagIds: [] as string[] }
  let requests: TimeEntryRequest[] | null
  if (stretches) {
    requests = stretchRequests(stretches, { ...base, start: block.start, stop: block.end }, { projectId, taskId: null, isBillable: catalog.project(projectId)?.isBillable ?? false }, catalog)
  } else {
    const single = { projectId, taskId: null, isBillable: catalog.project(projectId)?.isBillable ?? false }
    requests = [{ ...base, ...sharesRequest(parts, single), start: block.start, stop: block.end }]
  }
  if (!requests) {
    return { status: 'details', reason: 'The times no longer fit.' }
  }
  const missing = missingIn(requests, settings, catalog, parts ?? undefined)
  return missing ? { status: 'details', reason: missing } : { status: 'ready', requests: { update: null, create: requests } }
}

/**
 * The entries consecutive stretches become: each with the base's description and tags, a stretch on the base's own
 * project with its task and billable setting, the others as their project is. Null when a stretch would be empty.
 */
function stretchRequests(
  stretches: readonly Stretch[],
  base: Pick<TimeEntry, 'description' | 'tagIds' | 'start'> & { stop?: string | null },
  own: { projectId: string | null; taskId: string | null; isBillable: boolean },
  catalog: Catalog,
): TimeEntryRequest[] | null {
  const bounds = [Date.parse(base.start), ...stretches.slice(1).map((stretch) => stretch.start.getTime()), Date.parse(base.stop!)]
  if (stretches.length < 2 || bounds.some((at, index) => index > 0 && at <= bounds[index - 1])) {
    return null
  }
  return stretches.map((stretch, index) => {
    const isOwn = stretch.projectId === own.projectId
    return {
      description: base.description,
      projectId: stretch.projectId,
      taskId: isOwn ? own.taskId : null,
      isBillable: isOwn ? own.isBillable : (catalog.project(stretch.projectId)?.isBillable ?? false),
      tagIds: [...base.tagIds],
      shares: [],
      start: new Date(bounds[index]).toISOString(),
      stop: new Date(bounds[index + 1]).toISOString(),
    }
  })
}

const NEEDS: Record<DetailField, string> = {
  description: 'Needs a description',
  projectId: 'Needs a project',
  taskId: 'Needs a task',
  tagIds: 'Needs a tag',
}

/** What the first request still needs that the workspace requires, e.g. "Needs a task on Beacon"; null when nothing. */
function missingIn(requests: readonly TimeEntryRequest[], settings: PlanContext['settings'], catalog: Catalog, parts?: readonly SplitPart[]): string | null {
  for (const request of requests) {
    const project = catalog.project(request.projectId)
    const shares = request.shares && request.shares.length > 1 ? parts : undefined
    const missing = missingDetails(settings, { description: request.description, projectId: request.projectId, taskId: request.taskId, tagIds: request.tagIds ?? [], shares }, project)
    const field = (Object.keys(missing) as DetailField[])[0]
    if (field) {
      return field === 'taskId' && project ? `${NEEDS.taskId} on ${project.name}` : NEEDS[field]
    }
    if (shares) {
      const inParts = missingInParts(settings, shares, catalog.project)
      const index = Object.keys(inParts).map(Number)[0]
      if (index !== undefined) {
        const name = catalog.project(shares[index].projectId)?.name
        return inParts[index].includes('task') && name ? `${NEEDS.taskId} on ${name}` : NEEDS.description
      }
    }
  }
  return null
}

/** The suggestion's split as an entry the editor can start from, for "Change…". */
export function draftWithParts(entry: TimeEntry, parts: readonly SplitPart[]): TimeEntry {
  const own = { projectId: entry.projectId ?? null, taskId: entry.taskId ?? null, isBillable: entry.isBillable }
  const kept = parts.map((part) => (part.projectId === own.projectId ? { ...part, taskId: own.taskId, isBillable: own.isBillable } : part))
  const request = sharesRequest(kept, own)
  return {
    ...entry,
    projectId: request.projectId,
    taskId: request.taskId,
    isBillable: request.isBillable,
    shares: kept.map((part) => ({ projectId: part.projectId, taskId: part.taskId, isBillable: part.isBillable, percent: part.percent, description: null })),
  }
}
