import { ApiError } from '@/api/problem'
import type { Project, WorkspaceSettings } from '@/api/types'
import type { SplitPart } from './split'

export const DETAIL_FIELDS = ['description', 'projectId', 'taskId', 'tagIds'] as const
export type DetailField = (typeof DETAIL_FIELDS)[number]

export interface EntryDetails {
  description?: string | null
  projectId?: string | null
  taskId?: string | null
  tagIds: readonly string[]
  /** A split entry's parts; each is checked for a project and task, and its own description, on its own (see `missingInParts`). */
  shares?: readonly PartDetails[]
}

/** A part's details as the rules check them; a description of null or left out means the parts share one. */
type PartDetails = Pick<SplitPart, 'projectId' | 'taskId'> & { description?: string | null }

type RequiredSettings = Pick<WorkspaceSettings, 'requireDescription' | 'requireProject' | 'requireTask' | 'requireTag'>

/** Whether a task must be chosen on this project: only when the workspace asks for one and the project has open tasks. */
export function requiresTask(settings: RequiredSettings, project: Project | undefined): boolean {
  return settings.requireTask && !!project?.tasks.some((task) => !task.isDone)
}

/**
 * The details the workspace requires that a finished entry is missing, with the server's messages.
 * `project` is the entry's project, to know whether it has tasks to choose from.
 */
export function missingDetails(settings: RequiredSettings, details: EntryDetails, project: Project | undefined): Partial<Record<DetailField, string>> {
  const missing: Partial<Record<DetailField, string>> = {}
  const split = (details.shares?.length ?? 0) > 1
  const describedParts = split && details.shares!.some((part) => part.description != null)
  if (settings.requireDescription && !describedParts && !details.description?.trim()) {
    missing.description = 'A description is required in this workspace.'
  }
  if (!split && settings.requireProject && !details.projectId) {
    missing.projectId = 'A project is required in this workspace.'
  }
  if (!split && !details.taskId && details.projectId && requiresTask(settings, project)) {
    missing.taskId = 'A task is required on this project.'
  }
  if (settings.requireTag && details.tagIds.length === 0) {
    missing.tagIds = 'At least one tag is required in this workspace.'
  }
  return missing
}

/** What each part of a split entry is missing that the workspace requires, by the part's position. */
export function missingInParts(
  settings: RequiredSettings,
  parts: readonly PartDetails[],
  project: (id: string | null) => Project | undefined,
): Record<number, string> {
  const missing: Record<number, string> = {}
  parts.forEach((part, i) => {
    if (settings.requireProject && !part.projectId) {
      missing[i] = 'A project is required in this workspace.'
    } else if (!part.taskId && part.projectId && requiresTask(settings, project(part.projectId))) {
      missing[i] = 'A task is required on this project.'
    } else if (settings.requireDescription && part.description != null && !part.description.trim()) {
      missing[i] = 'A description is required in this workspace.'
    }
  })
  return missing
}

/** Whether the server refused because of the entry's details, which the person can fix in the entry. */
export function isDetailsError(error: unknown): error is ApiError {
  return (
    error instanceof ApiError &&
    error.status === 400 &&
    Object.keys(error.fieldErrors).some((field) => (DETAIL_FIELDS as readonly string[]).includes(field) || field.startsWith('shares'))
  )
}
