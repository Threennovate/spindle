import { addPercent, equalPercents, joinDescriptions, MAX_PARTS, sharesRequest, type SplitPart } from './split'

/**
 * Typing `@Project` in a description says what was done on which project: the text after a mention belongs to that
 * project, up to the next mention. Text before the first mention belongs to the entry's own project. A mention is a
 * project's exact name, so a typo stays plain text and nothing is guessed; the field shows how it was read.
 */

export interface MentionProject {
  id: string
  name: string
  isBillable: boolean
}

/** Where a mention is in the text, and which project it names. */
export interface Mention {
  start: number
  end: number
  projectId: string
}

export interface ParsedMentions {
  /** What was typed before the first mention. */
  lead: string
  /** What was typed after each mention, in order; a project mentioned twice appears twice. */
  segments: { projectId: string; text: string }[]
  mentions: Mention[]
}

const WORD = /[\p{L}\p{N}_]/u

/** Text between mentions, without the separators that joined it to them, e.g. "Review PR #42;" is "Review PR #42". */
function clean(text: string): string {
  return text.replace(/^[\s;,.·–-]+|[\s;,·–-]+$/g, '').replace(/\s+/g, ' ')
}

/** The mentions in `text`: an `@` at the start or after a space, followed by a project's name and no more of a word. */
export function findMentions(text: string, projects: readonly MentionProject[]): Mention[] {
  // The longest name first, so "Acme website" wins over "Acme".
  const names = [...projects].sort((a, b) => b.name.length - a.name.length)
  const mentions: Mention[] = []
  for (let i = 0; i < text.length; i++) {
    if (text[i] !== '@' || (i > 0 && !/\s/.test(text[i - 1]))) {
      continue
    }
    const rest = text.slice(i + 1).toLowerCase()
    const project = names.find((p) => p.name && rest.startsWith(p.name.toLowerCase()) && !WORD.test(rest[p.name.length] ?? ''))
    if (project) {
      mentions.push({ start: i, end: i + 1 + project.name.length, projectId: project.id })
      i += project.name.length
    }
  }
  return mentions
}

export function parseMentions(text: string, projects: readonly MentionProject[]): ParsedMentions {
  const mentions = findMentions(text, projects)
  return {
    lead: clean(text.slice(0, mentions[0]?.start ?? text.length)),
    segments: mentions.map((mention, i) => ({ projectId: mention.projectId, text: clean(text.slice(mention.end, mentions[i + 1]?.start ?? text.length)) })),
    mentions,
  }
}

/** The mention being typed at the caret: from an `@` to the caret, with no space typed yet after a word is complete. */
export function mentionAt(text: string, caret: number): { start: number; query: string } | null {
  const before = text.slice(0, caret)
  const at = before.lastIndexOf('@')
  if (at < 0 || (at > 0 && !/\s/.test(before[at - 1]))) {
    return null
  }
  const query = before.slice(at + 1)
  // A project name can have spaces, so the query may too, but not a line of words: two spaces or a separator end it.
  return /\s\s|[;,@]/.test(query) || query.length > 60 ? null : { start: at, query }
}

/** Puts a mention of `name` where `@query` was typed, followed by a space to go on typing. */
export function insertMention(text: string, at: { start: number; query: string }, name: string): { text: string; caret: number } {
  const end = at.start + 1 + at.query.length
  const after = text.slice(end).replace(/^ /, '')
  const inserted = `@${name} `
  return { text: text.slice(0, at.start) + inserted + after, caret: at.start + inserted.length }
}

/** What an entry is about before the mentions are applied: its one project, or its parts when already split. */
export interface MentionBase {
  projectId: string | null
  taskId: string | null
  isBillable: boolean
  parts?: readonly SplitPart[] | null
}

export interface MentionResult {
  description: string
  projectId: string | null
  taskId: string | null
  isBillable: boolean
  /** Two or more parts, each described on its own; null when it is all on one project. */
  parts: SplitPart[] | null
}

/**
 * Where the time goes once the mentions are applied. A mentioned project not yet in the split joins it with an equal
 * share; parts keep the share they had. Each part's description is what was typed for it, the entry's own project
 * also getting what was typed before the first mention; a part nothing was typed for keeps its own description.
 * Without mentions, nothing changes but the description. Null when more projects are mentioned than a split allows.
 */
export function applyMentions(
  parsed: ParsedMentions,
  base: MentionBase,
  projects: readonly MentionProject[],
  /** The text is the whole of the parts' descriptions (see `mentionText`), so a part it leaves out has none. */
  whole = false,
): MentionResult | null {
  if (parsed.mentions.length === 0) {
    return {
      description: parsed.lead,
      projectId: base.projectId,
      taskId: base.taskId,
      isBillable: base.isBillable,
      parts: base.parts && base.parts.length > 1 ? base.parts.map((part) => ({ ...part, description: undefined })) : null,
    }
  }

  const split = !!base.parts && base.parts.length > 1
  let parts: SplitPart[] = split ? base.parts!.map((part) => ({ ...part })) : []
  // Without a project of its own, an entry is only what was mentioned; what came first goes to the first mention.
  const ownProject = split ? (base.parts!.reduce((best, part) => (part.percent > best.percent ? part : best)).projectId ?? null) : base.projectId
  if (!split && (base.projectId || !parsed.segments.length)) {
    parts.push({ projectId: base.projectId, taskId: base.taskId, isBillable: base.isBillable, percent: 100 })
  }
  const leadProject = ownProject ?? parsed.segments[0].projectId

  for (const { projectId } of parsed.segments) {
    if (parts.some((part) => part.projectId === projectId)) {
      continue
    }
    if (parts.length >= MAX_PARTS) {
      return null
    }
    const project = projects.find((p) => p.id === projectId)
    const percents = split ? addPercent(parts.map((part) => part.percent)) : []
    parts = [...parts.map((part, i) => (split ? { ...part, percent: percents[i] } : part)), { projectId, taskId: null, isBillable: project?.isBillable ?? false, percent: percents[parts.length] ?? 0 }]
  }
  if (!split) {
    const percents = equalPercents(parts.length)
    parts = parts.map((part, i) => ({ ...part, percent: percents[i] }))
  }

  const written = new Map<string | null, string[]>()
  const write = (projectId: string | null, text: string) => text && written.set(projectId, [...(written.get(projectId) ?? []), text])
  write(leadProject, parsed.lead)
  parsed.segments.forEach((segment) => write(segment.projectId, segment.text))
  // The first part on a project gets its text, should the split have two parts on one project with different tasks.
  const firstOf = new Set(parts.map((part) => parts.findIndex((p) => p.projectId === part.projectId)))
  parts = parts.map((part, i) => ({
    ...part,
    description: (firstOf.has(i) && written.get(part.projectId)?.join('; ')) || (split && !whole ? (part.description ?? '') : ''),
  }))

  if (parts.length === 1) {
    const [only] = parts
    return { description: only.description ?? '', projectId: only.projectId, taskId: only.taskId, isBillable: only.isBillable, parts: null }
  }
  const largest = parts.reduce((best, part) => (part.percent > best.percent ? part : best), parts[0])
  return { description: joinDescriptions(parts), projectId: largest.projectId, taskId: largest.taskId, isBillable: largest.isBillable, parts }
}

/** Projects that can be mentioned: the workspace's open ones. */
export function mentionable<P extends MentionProject & { archivedAt?: string | null }>(projects: readonly P[]): P[] {
  return projects.filter((project) => !project.archivedAt)
}

/**
 * A split's own descriptions written as mentions, "@AnchorNet Review PR #42 @FrameIt Fix the export", to edit them in
 * one field; null when a part with text is on no project that can be mentioned.
 */
export function mentionText(parts: readonly SplitPart[], projects: readonly MentionProject[]): string | null {
  const written = parts.filter((part) => part.description)
  const names = written.map((part) => projects.find((project) => project.id === part.projectId)?.name)
  return names.every(Boolean) ? written.map((part, i) => `@${names[i]} ${part.description}`).join(' ') : null
}

/** The request fields for an entry described with mentions, or null when they name more projects than a split allows. */
export function mentionRequest(text: string, base: MentionBase, projects: readonly MentionProject[], whole = false) {
  const result = applyMentions(parseMentions(text, projects), base, projects, whole)
  return result && { description: result.description, ...sharesRequest(result.parts, result) }
}
