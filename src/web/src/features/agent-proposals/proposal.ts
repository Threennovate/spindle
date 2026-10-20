import type { AgentProposal, TimeEntry } from '@/api/types'
import { formatClock, dayKey } from '@/lib/time/zone'
import { splitDescription } from '../catalog/split-names'
import type { Catalog } from '../catalog/use-catalog'
import { partsOf, type SplitPart } from '../time-entries/split'

const CLIENT_NAMES: Record<string, string> = {
  'claude-code': 'Claude Code',
  'claude-ai': 'Claude',
  codex: 'Codex',
  'codex-mcp-client': 'Codex',
}

/** Who proposed it, as people know the agent: "Claude Code" for `claude-code`. */
export function clientName(client: string | null | undefined): string | null {
  if (!client) {
    return null
  }
  return CLIENT_NAMES[client.toLowerCase()] ?? client
}

/**
 * The entry saving a proposal would store, shaped as an entry, to show it and to fill the entry editor. A new entry
 * has no ID yet; a change keeps its entry's.
 */
export function draftOf(proposal: AgentProposal): TimeEntry | null {
  const saves = proposal.saves
  if (!saves?.start) {
    return null
  }
  return {
    id: proposal.entryId ?? `proposal-${proposal.id}`,
    workspaceId: proposal.workspaceId,
    description: saves.description ?? '',
    projectId: saves.projectId ?? null,
    taskId: saves.taskId ?? null,
    tagIds: saves.tagIds ?? [],
    isBillable: saves.isBillable ?? false,
    start: saves.start,
    stop: saves.stop ?? null,
    shares: (saves.shares ?? []).map((share) => ({
      projectId: share.projectId ?? null,
      taskId: share.taskId ?? null,
      isBillable: share.isBillable ?? false,
      percent: share.percent ?? 0,
      description: share.description ?? null,
    })),
    // Not saved yet, so no version; the web saves without one.
    version: '',
  }
}

/** The time a proposal is about: what it would save, or the entry as it was when the entry is gone. */
export function startOf(proposal: AgentProposal): string {
  return proposal.saves?.start ?? proposal.original?.start ?? proposal.proposedAt
}

/** Proposals grouped by the day they are about, in the person's time zone, days and proposals in order. */
export function byDay<T extends AgentProposal>(proposals: readonly T[], timeZone: string): { day: string; proposals: T[] }[] {
  const days = new Map<string, T[]>()
  // By instant: the server writes some times in UTC and others with the offset the agent gave.
  for (const proposal of [...proposals].sort((a, b) => Date.parse(startOf(a)) - Date.parse(startOf(b)))) {
    const key = dayKey(startOf(proposal), timeZone)
    days.set(key, [...(days.get(key) ?? []), proposal])
  }
  return [...days.entries()].sort(([a], [b]) => a.localeCompare(b)).map(([day, list]) => ({ day, proposals: list }))
}

/** One line of what a change does: what it is about, how it was, and how it would be. */
export interface ChangeLine {
  label: 'Time' | 'Project' | 'Description'
  from: string
  to: string
}

/** What a proposed change does to its entry, a line per thing it changes. */
export function changeLines(proposal: AgentProposal, catalog: Catalog, timeZone: string, hour12: boolean): ChangeLine[] {
  const original = proposal.original
  const fields = proposal.fields
  if (!original) {
    return []
  }
  const lines: ChangeLine[] = []
  if (fields.start != null || fields.stop != null) {
    const range = (start: string, stop: string | null | undefined) =>
      `${formatClock(start, timeZone, hour12)}–${stop ? formatClock(stop, timeZone, hour12) : 'now'}`
    lines.push({
      label: 'Time',
      from: range(original.start, original.stop),
      to: range(fields.start ?? original.start, fields.stop ?? original.stop),
    })
  }
  if (fields.work) {
    lines.push({
      label: 'Project',
      from: workName(catalog, partsOf(original)),
      to: workName(catalog, partsOf(fields.work)),
    })
  }
  if (fields.description != null) {
    lines.push({
      label: 'Description',
      from: original.description,
      to: fields.description,
    })
  }
  return lines
}

function workName(catalog: Catalog, parts: readonly SplitPart[]): string {
  return parts.length > 1 ? splitDescription(catalog, parts) : (catalog.project(parts[0]?.projectId)?.name ?? 'No project')
}

/** "2 new entries and 1 change": what is waiting, in words. */
export function waitingSummary(proposals: readonly AgentProposal[]): string {
  const added = proposals.filter((p) => !p.entryId).length
  const changed = proposals.length - added
  const parts = [added > 0 && `${added} new ${added === 1 ? 'entry' : 'entries'}`, changed > 0 && `${changed} ${changed === 1 ? 'change' : 'changes'}`].filter(
    Boolean,
  )
  return parts.join(' and ')
}

/** Who proposed what is waiting: the agent's name when it was one, otherwise "Your agents". */
export function proposedBy(proposals: readonly AgentProposal[]): string {
  const names = new Set(proposals.map((p) => clientName(p.client) ?? ''))
  const [only] = names
  return names.size === 1 && only ? only : 'Your agents'
}

/** Seconds between a start and a stop; none for a running entry. */
export function secondsOf(entry: { start: string; stop?: string | null }): number | null {
  return entry.stop ? (new Date(entry.stop).getTime() - new Date(entry.start).getTime()) / 1000 : null
}

/** How much a proposal adds to the day: a new entry's length, or what a change adds or takes away. */
export function addedSeconds(proposal: AgentProposal): number {
  const after = proposal.saves?.start
    ? (secondsOf({
        start: proposal.saves.start,
        stop: proposal.saves.stop,
      }) ?? 0)
    : 0
  const before = proposal.original ? (secondsOf(proposal.original) ?? 0) : 0
  return after - before
}
