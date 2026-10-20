import type { SplitPart } from '../time-entries/split'
import type { Catalog } from './use-catalog'

const NO_PROJECT_COLOR = 'var(--muted-foreground)'

/** The color a part is shown in: its project's, or grey without one. */
export function partColor(catalog: Catalog, part: Pick<SplitPart, 'projectId'>): string {
  return catalog.project(part.projectId)?.color ?? NO_PROJECT_COLOR
}

export function partName(catalog: Catalog, part: Pick<SplitPart, 'projectId' | 'taskId'>): string {
  const project = catalog.project(part.projectId)
  const task = catalog.task(part.taskId)
  return project ? (task ? `${project.name} · ${task.name}` : project.name) : 'No project'
}

/** "Website 60%, Research 40%", for labels read aloud. */
export function splitDescription(catalog: Catalog, parts: readonly SplitPart[]): string {
  return parts.map((part) => `${partName(catalog, part)} ${part.percent}%`).join(', ')
}
