import { useQuery } from '@tanstack/react-query'
import { useMemo } from 'react'
import type { Client, Project, Tag, Task } from '@/api/types'
import { clientsQuery, projectsQuery, tagsQuery } from './queries'

export interface Catalog {
  /** All projects, archived included, since old entries may still point at them. */
  projects: Project[]
  clients: Client[]
  tags: Tag[]
  project: (id: string | null | undefined) => Project | undefined
  task: (id: string | null | undefined) => Task | undefined
  client: (id: string | null | undefined) => Client | undefined
  tag: (id: string) => Tag | undefined
}

/** Projects, tasks, clients and tags of the workspace, with lookups by ID. */
export function useCatalog(workspaceId: string): Catalog {
  const { data: projects = [] } = useQuery(projectsQuery(workspaceId, true))
  const { data: clients = [] } = useQuery(clientsQuery(workspaceId, true))
  const { data: tags = [] } = useQuery(tagsQuery(workspaceId))

  return useMemo(() => {
    const projectsById = new Map(projects.map((p) => [p.id, p]))
    const tasksById = new Map(projects.flatMap((p) => p.tasks).map((t) => [t.id, t]))
    const clientsById = new Map(clients.map((c) => [c.id, c]))
    const tagsById = new Map(tags.map((t) => [t.id, t]))

    return {
      projects,
      clients,
      tags,
      project: (id) => (id ? projectsById.get(id) : undefined),
      task: (id) => (id ? tasksById.get(id) : undefined),
      client: (id) => (id ? clientsById.get(id) : undefined),
      tag: (id) => tagsById.get(id),
    }
  }, [projects, clients, tags])
}

/** Substring search over an item's keywords, for cmdk lists. More predictable than fuzzy matching. */
export function matchKeywords(_value: string, search: string, keywords?: string[]): number {
  const term = search.trim().toLowerCase()
  if (!term) {
    return 1
  }
  return keywords?.some((keyword) => keyword.toLowerCase().includes(term)) ? 1 : 0
}
