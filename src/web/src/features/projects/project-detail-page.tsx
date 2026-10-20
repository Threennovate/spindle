import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { ArrowLeft, Ellipsis } from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { ApiError } from '@/api/problem'
import type { Project, Task } from '@/api/types'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { InlineNameForm } from '@/components/inline-name'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { errorMessage } from '@/lib/error-message'
import { formatRate } from '@/lib/money'
import { formatHoursMinutes } from '@/lib/time/duration'
import { usePageTitle } from '@/lib/title'
import { AgreementsCard } from '../agreements/agreements-card'
import { BillingCard } from '../billing/billing-card'
import { ProjectDot } from '../catalog/project-label'
import { projectQuery, useArchiveProject, useDeleteProject, useDeleteTask, useSaveTask } from '../catalog/queries'
import { useCatalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { EstimateProgress } from './estimate-progress'
import { ProjectDialog } from './project-dialog'
import { ProjectMembersCard } from './project-members'

export function ProjectDetailPage({ projectId }: { projectId: string }) {
  const { workspaceId } = useWorkspace()
  const query = useQuery(projectQuery(workspaceId, projectId))
  usePageTitle(query.data?.name ?? 'Project')

  if (query.isPending) {
    return (
      <div className="mx-auto grid max-w-6xl gap-4">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-40 w-full" />
      </div>
    )
  }

  if (query.isError) {
    const notFound = query.error instanceof ApiError && query.error.status === 404
    return (
      <div className="text-muted-foreground mx-auto grid max-w-6xl justify-items-start gap-3 text-sm">
        <p>{notFound ? 'This project does not exist or was deleted.' : errorMessage(query.error, "Couldn't load the project.")}</p>
        <Button asChild variant="outline">
          <Link to="/projects">Back to projects</Link>
        </Button>
      </div>
    )
  }

  return <ProjectDetail project={query.data} workspaceId={workspaceId} />
}

function ProjectDetail({ project, workspaceId }: { project: Project; workspaceId: string }) {
  const catalog = useCatalog(workspaceId)
  const { settings, isAdmin } = usePermissions()
  const navigate = useNavigate()
  const archive = useArchiveProject(workspaceId)
  const remove = useDeleteProject(workspaceId)
  const saveTask = useSaveTask(workspaceId, project.id)
  const [editing, setEditing] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const client = catalog.client(project.clientId)
  const openTasks = project.tasks.filter((task) => !task.isDone)
  const doneTasks = project.tasks.filter((task) => task.isDone)

  async function addTask(name: string): Promise<string | null> {
    try {
      await saveTask.mutateAsync({ body: { name, isDone: false } })
      return null
    } catch (error) {
      return errorMessage(error, "Couldn't add the task.")
    }
  }

  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <Link to="/projects" className="text-muted-foreground hover:text-foreground flex w-fit items-center gap-1 text-sm">
        <ArrowLeft className="size-4" />
        Projects
      </Link>

      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="grid gap-1">
          <h1 className="flex items-center gap-2 text-2xl font-semibold tracking-tight">
            <ProjectDot color={project.color} className="size-3.5" />
            {project.name}
          </h1>
          <div className="text-muted-foreground flex flex-wrap items-center gap-2 text-sm">
            <span>{client?.name ?? 'No client'}</span>
            {project.isBillable && (
              <Badge variant="secondary">Billable{project.hourlyRate != null && ` · ${formatRate(project.hourlyRate, settings.currency)}`}</Badge>
            )}
            {project.billingMode === 'perDay' && <Badge variant="secondary">Billed day by day</Badge>}
            {project.billingMode === 'fullDay' && <Badge variant="secondary">Billed by the full day{project.dailyHours != null && ` · ${project.dailyHours} h`}</Badge>}
            {project.isPrivate && <Badge variant="secondary">Private</Badge>}
            {project.archivedAt && <Badge variant="outline">Archived</Badge>}
          </div>
        </div>
        {project.canManage && (
          <div className="flex items-center gap-2">
            <Button variant="outline" onClick={() => setEditing(true)}>
              Edit project
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button variant="ghost" size="icon" aria-label="More project actions">
                  <Ellipsis />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                <DropdownMenuItem
                  onSelect={() =>
                    archive.mutate(
                      { id: project.id, archive: !project.archivedAt },
                      { onError: (error) => toast.error(errorMessage(error, "Couldn't change the project.")) },
                    )
                  }
                >
                  {project.archivedAt ? 'Restore' : 'Archive'}
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem variant="destructive" onSelect={() => setConfirmDelete(true)}>
                  Delete
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        )}
      </div>

      <div className="grid gap-4 sm:grid-cols-3">
        <Card>
          <CardHeader>
            <CardDescription>Tracked</CardDescription>
            <CardTitle className="text-2xl tabular-nums">{formatHoursMinutes(project.trackedSeconds)}</CardTitle>
          </CardHeader>
        </Card>
        <Card>
          <CardHeader>
            <CardDescription>Estimate</CardDescription>
            <CardTitle className="text-2xl tabular-nums">{project.estimateSeconds ? formatHoursMinutes(project.estimateSeconds) : '—'}</CardTitle>
            {!!project.estimateSeconds && <EstimateProgress trackedSeconds={project.trackedSeconds} estimateSeconds={project.estimateSeconds} />}
          </CardHeader>
        </Card>
        <Card>
          <CardHeader>
            <CardDescription>Tasks</CardDescription>
            <CardTitle className="text-2xl tabular-nums">{openTasks.length}</CardTitle>
            <p className="text-muted-foreground text-sm">{doneTasks.length} done</p>
          </CardHeader>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Tasks</CardTitle>
          <CardDescription>Track time against a task by choosing it under the project in the timer.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4">
          {project.canManage && <InlineNameForm label="New task name" placeholder="Add a task" submitLabel="Add task" onSubmit={addTask} />}
          {project.tasks.length === 0 ? (
            <p className="text-muted-foreground text-sm">No tasks yet.</p>
          ) : (
            <ul className="divide-y rounded-lg border">
              {[...openTasks, ...doneTasks].map((task) => (
                <TaskRow key={task.id} task={task} workspaceId={workspaceId} projectId={project.id} canManage={project.canManage} />
              ))}
            </ul>
          )}
        </CardContent>
      </Card>

      {isAdmin && <AgreementsCard project={project} />}

      {project.canManage && project.separateBilledTime && <BillingCard project={project} />}

      <ProjectMembersCard project={project} />

      <ProjectDialog workspaceId={workspaceId} open={editing} onOpenChange={setEditing} project={project} />
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete “${project.name}”?`}
        description="Its tasks are deleted. The time tracked on it stays, without a project. To keep the project in reports, archive it instead."
        confirmLabel="Delete project"
        onConfirm={() =>
          remove.mutate(project.id, {
            onSuccess: () => {
              toast.success(`“${project.name}” deleted`)
              void navigate({ to: '/projects' })
            },
            onError: (error) => toast.error(errorMessage(error, "Couldn't delete the project.")),
          })
        }
      />
    </div>
  )
}

function TaskRow({ task, workspaceId, projectId, canManage }: { task: Task; workspaceId: string; projectId: string; canManage: boolean }) {
  const saveTask = useSaveTask(workspaceId, projectId)
  const deleteTask = useDeleteTask(workspaceId, projectId)
  const [renaming, setRenaming] = useState(false)
  const [confirmDelete, setConfirmDelete] = useState(false)

  function save(changes: Partial<Pick<Task, 'name' | 'isDone'>>) {
    return saveTask.mutateAsync({
      id: task.id,
      body: { name: changes.name ?? task.name, isDone: changes.isDone ?? task.isDone, estimateSeconds: task.estimateSeconds ?? null },
    })
  }

  if (renaming) {
    return (
      <li className="p-2">
        <InlineNameForm
          label="Task name"
          initialValue={task.name}
          submitLabel="Save"
          onCancel={() => setRenaming(false)}
          onSubmit={async (name) => {
            try {
              await save({ name })
              setRenaming(false)
              return null
            } catch (error) {
              return errorMessage(error, "Couldn't rename the task.")
            }
          }}
        />
      </li>
    )
  }

  return (
    <li className="flex items-center gap-3 px-3 py-2">
      <Checkbox
        checked={task.isDone}
        disabled={!canManage}
        aria-label={task.isDone ? `Reopen ${task.name}` : `Mark ${task.name} done`}
        onCheckedChange={(checked) =>
          void save({ isDone: checked === true }).catch((error: unknown) => toast.error(errorMessage(error, "Couldn't update the task.")))
        }
      />
      <span className={task.isDone ? 'text-muted-foreground min-w-0 flex-1 truncate text-sm line-through' : 'min-w-0 flex-1 truncate text-sm'}>{task.name}</span>
      <span className="text-muted-foreground text-sm tabular-nums">{formatHoursMinutes(task.trackedSeconds)}</span>
      {canManage && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" aria-label={`Actions for ${task.name}`}>
              <Ellipsis />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={() => setRenaming(true)}>Rename</DropdownMenuItem>
            <DropdownMenuItem variant="destructive" onSelect={() => setConfirmDelete(true)}>
              Delete
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      )}
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete the task “${task.name}”?`}
        description="Time tracked on it stays on the project, without a task."
        confirmLabel="Delete task"
        onConfirm={() => deleteTask.mutate(task.id, { onError: (error) => toast.error(errorMessage(error, "Couldn't delete the task.")) })}
      />
    </li>
  )
}
