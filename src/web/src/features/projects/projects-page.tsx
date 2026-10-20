import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { DollarSign, Ellipsis, Lock, Plus, Search } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import type { Project } from '@/api/types'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { PageHeader } from '@/components/page-header'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { errorMessage } from '@/lib/error-message'
import { formatHoursMinutes } from '@/lib/time/duration'
import { usePageTitle } from '@/lib/title'
import { usePersistentState } from '@/lib/use-persistent-state'
import { ProjectDot } from '../catalog/project-label'
import { projectsQuery, useArchiveProject, useDeleteProject } from '../catalog/queries'
import { useCatalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { EstimateProgress } from './estimate-progress'
import { ProjectDialog } from './project-dialog'

const STATUSES = ['active', 'archived', 'all'] as const
const ALL_CLIENTS = 'all'
const NO_CLIENT = 'none'

export function ProjectsPage() {
  usePageTitle('Projects')
  const { workspaceId } = useWorkspace()
  const { canCreateProjects } = usePermissions()
  const catalog = useCatalog(workspaceId)
  const query = useQuery(projectsQuery(workspaceId, true))
  const [status, setStatus] = usePersistentState('spindle:projects-status', 'active', STATUSES)
  const [search, setSearch] = useState('')
  const [clientFilter, setClientFilter] = useState(ALL_CLIENTS)
  const [dialog, setDialog] = useState<{ project?: Project } | null>(null)

  const term = search.trim().toLowerCase()
  const projects = (query.data ?? []).filter((project) => {
    const client = catalog.client(project.clientId)
    return (
      (status === 'all' || (status === 'archived') === !!project.archivedAt) &&
      (clientFilter === ALL_CLIENTS || (clientFilter === NO_CLIENT ? !project.clientId : project.clientId === clientFilter)) &&
      (!term || project.name.toLowerCase().includes(term) || client?.name.toLowerCase().includes(term))
    )
  })

  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <PageHeader
        title="Projects"
        description="Group your time by project, and by task within a project."
        actions={
          canCreateProjects && (
            <Button onClick={() => setDialog({})}>
              <Plus />
              New project
            </Button>
          )
        }
      />

      <div className="flex flex-wrap items-center gap-3">
        <Tabs value={status} onValueChange={(value) => setStatus(value as (typeof STATUSES)[number])}>
          <TabsList>
            <TabsTrigger value="active">Active</TabsTrigger>
            <TabsTrigger value="archived">Archived</TabsTrigger>
            <TabsTrigger value="all">All</TabsTrigger>
          </TabsList>
        </Tabs>
        <div className="relative w-full max-w-64">
          <Search className="text-muted-foreground absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
          <Input aria-label="Search projects" placeholder="Search projects or clients" value={search} onChange={(e) => setSearch(e.target.value)} className="pl-8" />
        </div>
        <Select value={clientFilter} onValueChange={setClientFilter}>
          <SelectTrigger aria-label="Filter by client" className="w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL_CLIENTS}>All clients</SelectItem>
            <SelectItem value={NO_CLIENT}>No client</SelectItem>
            {catalog.clients.map((client) => (
              <SelectItem key={client.id} value={client.id}>
                {client.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      <div className="bg-card overflow-hidden rounded-xl border">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead className="pl-4">Project</TableHead>
              <TableHead className="hidden md:table-cell">Client</TableHead>
              <TableHead className="text-right">Tracked</TableHead>
              <TableHead className="hidden lg:table-cell">Estimate</TableHead>
              <TableHead className="w-12" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {query.isPending &&
              [0, 1, 2].map((row) => (
                <TableRow key={row}>
                  <TableCell colSpan={5} className="px-4">
                    <Skeleton className="h-6 w-full" />
                  </TableCell>
                </TableRow>
              ))}
            {query.isError && (
              <TableRow>
                <TableCell colSpan={5} className="text-muted-foreground py-10 text-center">
                  {errorMessage(query.error, "Couldn't load projects.")}
                </TableCell>
              </TableRow>
            )}
            {query.isSuccess && projects.length === 0 && (
              <TableRow>
                <TableCell colSpan={5} className="text-muted-foreground py-10 text-center">
                  {query.data.length > 0
                    ? 'No projects match these filters.'
                    : canCreateProjects
                      ? 'No projects yet. Create one to start grouping your time.'
                      : 'No projects yet. Admins add the projects of this workspace.'}
                </TableCell>
              </TableRow>
            )}
            {projects.map((project) => (
              <ProjectRow key={project.id} project={project} clientName={catalog.client(project.clientId)?.name} workspaceId={workspaceId} onEdit={() => setDialog({ project })} />
            ))}
          </TableBody>
        </Table>
      </div>

      <ProjectDialog workspaceId={workspaceId} open={dialog !== null} onOpenChange={(open) => !open && setDialog(null)} project={dialog?.project} />
    </div>
  )
}

function ProjectRow({ project, clientName, workspaceId, onEdit }: { project: Project; clientName?: string; workspaceId: string; onEdit: () => void }) {
  const archive = useArchiveProject(workspaceId)
  const remove = useDeleteProject(workspaceId)
  const [confirmDelete, setConfirmDelete] = useState(false)

  function toggleArchive() {
    const archiving = !project.archivedAt
    archive.mutate(
      { id: project.id, archive: archiving },
      {
        onSuccess: () => toast.success(archiving ? `“${project.name}” archived` : `“${project.name}” restored`),
        onError: (error) => toast.error(errorMessage(error, "Couldn't change the project.")),
      },
    )
  }

  function deleteProject() {
    remove.mutate(project.id, {
      onSuccess: () => toast.success(`“${project.name}” deleted`),
      onError: (error) => toast.error(errorMessage(error, "Couldn't delete the project.")),
    })
  }

  return (
    <TableRow>
      <TableCell className="max-w-72 pl-4">
        <Link to="/projects/$projectId" params={{ projectId: project.id }} className="flex min-w-0 items-center gap-2 font-medium hover:underline">
          <ProjectDot color={project.color} />
          <span className="truncate">{project.name}</span>
          {project.isPrivate && <Lock aria-label="Private" className="text-muted-foreground size-3.5 shrink-0" />}
          {project.isBillable && <DollarSign aria-label="Billable" className="text-muted-foreground size-3.5 shrink-0" />}
          {project.archivedAt && (
            <Badge variant="outline" className="font-normal">
              Archived
            </Badge>
          )}
        </Link>
      </TableCell>
      <TableCell className="text-muted-foreground hidden md:table-cell">{clientName ?? '—'}</TableCell>
      <TableCell className="text-right tabular-nums">{formatHoursMinutes(project.trackedSeconds)}</TableCell>
      <TableCell className="hidden lg:table-cell">
        <EstimateProgress trackedSeconds={project.trackedSeconds} estimateSeconds={project.estimateSeconds} />
      </TableCell>
      <TableCell className="pr-2 text-right">
        {project.canManage && (
          <>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="ghost" size="icon" aria-label={`Actions for ${project.name}`}>
                  <Ellipsis />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem onSelect={onEdit}>Edit</DropdownMenuItem>
                <DropdownMenuItem onSelect={toggleArchive}>{project.archivedAt ? 'Restore' : 'Archive'}</DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem variant="destructive" onSelect={() => setConfirmDelete(true)}>
                  Delete
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
            <ConfirmDialog
              open={confirmDelete}
              onOpenChange={setConfirmDelete}
              title={`Delete “${project.name}”?`}
              description={
                project.trackedSeconds > 0
                  ? `Its tasks are deleted. The ${formatHoursMinutes(project.trackedSeconds)} tracked on it stay, without a project. To keep the project in reports, archive it instead.`
                  : 'Its tasks are deleted too.'
              }
              confirmLabel="Delete project"
              onConfirm={deleteProject}
            />
          </>
        )}
      </TableCell>
    </TableRow>
  )
}
