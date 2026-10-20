import { useQuery } from '@tanstack/react-query'
import { FolderX, Plus, Split } from 'lucide-react'
import { Fragment, useState, type ReactNode } from 'react'
import type { Client, Project } from '@/api/types'
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList, CommandSeparator } from '@/components/ui/command'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { agentsNowQuery, useAgentActivitySync } from '../agent-activity/queries'
import { ProjectDialog } from '../projects/project-dialog'
import { usePermissions } from '../settings/queries'
import { ProjectDot } from './project-label'
import { matchKeywords, useCatalog } from './use-catalog'

interface ProjectPickerProps {
  workspaceId: string
  /** The project chosen now; null for "No project", undefined when choosing anew, e.g. another part of a split. */
  projectId: string | null | undefined
  taskId: string | null
  onChange: (projectId: string | null, taskId: string | null) => void
  /** The button that opens the picker. */
  children: ReactNode
  align?: 'start' | 'center' | 'end'
  /** Opens the picker from outside, e.g. right after a split is started. */
  open?: boolean
  onOpenChange?: (open: boolean) => void
  /** Offers to split the time between projects instead of choosing one. */
  onSplit?: () => void
  placeholder?: string
  /** Choices already taken by another part of a split, shown but not offered. */
  taken?: (projectId: string | null, taskId: string | null) => boolean
}

/** Picks a project or one of its tasks, grouped by client. A new project can be created on the spot. */
export function ProjectPicker({
  workspaceId,
  projectId,
  taskId,
  onChange,
  children,
  align = 'start',
  open: controlledOpen,
  onOpenChange,
  onSplit,
  taken = () => false,
  placeholder = 'Search projects, tasks or clients',
}: ProjectPickerProps) {
  const [ownOpen, setOwnOpen] = useState(false)
  const open = controlledOpen ?? ownOpen
  const setOpen = (next: boolean) => {
    setOwnOpen(next)
    onOpenChange?.(next)
  }
  const [search, setSearch] = useState('')
  const [newProjectName, setNewProjectName] = useState<string | null>(null)
  const catalog = useCatalog(workspaceId)
  const { canCreateProjects } = usePermissions()

  const groups = groupByClient(catalog.projects.filter((p) => !p.archivedAt || p.id === projectId), catalog)
  // Projects agents worked in during the last 15 minutes, by the person's own activity, while the picker is open.
  const sync = useAgentActivitySync()
  const agentsNow = useQuery({ ...agentsNowQuery(workspaceId, sync && open), refetchInterval: open ? 60_000 : false })
  const recent = (agentsNow.data ?? []).flatMap((recent) => catalog.projects.filter((project) => project.id === recent.projectId && !project.archivedAt))

  function select(nextProjectId: string | null, nextTaskId: string | null) {
    onChange(nextProjectId, nextTaskId)
    setOpen(false)
    setSearch('')
  }

  return (
    <>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>{children}</PopoverTrigger>
        <PopoverContent className="w-80 p-0" align={align}>
          <Command filter={matchKeywords}>
            <CommandInput placeholder={placeholder} value={search} onValueChange={setSearch} />
            <CommandList>
              <CommandEmpty>No matching projects.</CommandEmpty>
              {recent.length > 0 && !search.trim() && (
                <CommandGroup heading="Agents now">
                  {recent.map((project) => (
                    <CommandItem
                      key={project.id}
                      value={`agents-now-${project.id}`}
                      data-checked={project.id === projectId && !taskId}
                      disabled={taken(project.id, null)}
                      onSelect={() => select(project.id, null)}
                    >
                      <ProjectDot color={project.color} />
                      <span className="truncate">{project.name}</span>
                    </CommandItem>
                  ))}
                </CommandGroup>
              )}
              <CommandGroup>
                <CommandItem
                  value="no-project"
                  keywords={['no project']}
                  data-checked={projectId === null}
                  disabled={taken(null, null)}
                  onSelect={() => select(null, null)}
                >
                  <FolderX className="text-muted-foreground" />
                  No project
                </CommandItem>
              </CommandGroup>
              {groups.map(({ client, projects }) => (
                <CommandGroup key={client?.id ?? 'no-client'} heading={client?.name ?? 'No client'}>
                  {projects.map((project) => (
                    <Fragment key={project.id}>
                      <CommandItem
                        value={project.id}
                        keywords={[project.name, client?.name ?? '']}
                        data-checked={project.id === projectId && !taskId}
                        disabled={taken(project.id, null)}
                        onSelect={() => select(project.id, null)}
                      >
                        <ProjectDot color={project.color} />
                        <span className="truncate">{project.name}</span>
                      </CommandItem>
                      {project.tasks
                        .filter((task) => !task.isDone || task.id === taskId)
                        .map((task) => (
                          <CommandItem
                            key={task.id}
                            value={task.id}
                            keywords={[task.name, project.name, client?.name ?? '']}
                            data-checked={task.id === taskId}
                            disabled={taken(project.id, task.id)}
                            onSelect={() => select(project.id, task.id)}
                            className="pl-7"
                          >
                            <span className="text-muted-foreground truncate">{task.name}</span>
                          </CommandItem>
                        ))}
                    </Fragment>
                  ))}
                </CommandGroup>
              ))}
              {onSplit && !search.trim() && (
                <>
                  <CommandSeparator />
                  <CommandGroup>
                    <CommandItem
                      value="split-time"
                      onSelect={() => {
                        setOpen(false)
                        onSplit()
                      }}
                    >
                      <Split />
                      Split time between projects…
                    </CommandItem>
                  </CommandGroup>
                </>
              )}
              {canCreateProjects && (
                <>
                  <CommandSeparator />
                  <CommandGroup forceMount>
                    <CommandItem
                      value="create-project"
                      forceMount
                      onSelect={() => {
                        setNewProjectName(search.trim())
                        setOpen(false)
                      }}
                    >
                      <Plus />
                      {search.trim() ? `Create project “${search.trim()}”` : 'Create a new project'}
                    </CommandItem>
                  </CommandGroup>
                </>
              )}
            </CommandList>
          </Command>
        </PopoverContent>
      </Popover>

      <ProjectDialog
        workspaceId={workspaceId}
        open={newProjectName !== null}
        onOpenChange={(isOpen) => !isOpen && setNewProjectName(null)}
        initialName={newProjectName ?? ''}
        onSaved={(project) => {
          setNewProjectName(null)
          setSearch('')
          onChange(project.id, null)
        }}
      />
    </>
  )
}

function groupByClient(projects: Project[], catalog: { client: (id: string | null | undefined) => Client | undefined }) {
  const groups = new Map<string, { client: Client | undefined; projects: Project[] }>()
  for (const project of projects) {
    const key = project.clientId ?? ''
    const group = groups.get(key) ?? { client: catalog.client(project.clientId), projects: [] }
    group.projects.push(project)
    groups.set(key, group)
  }

  // Projects without a client first, then clients alphabetically.
  return [...groups.values()].sort((a, b) => {
    if (!a.client) return -1
    if (!b.client) return 1
    return a.client.name.localeCompare(b.client.name)
  })
}
