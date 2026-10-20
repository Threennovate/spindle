import { cn } from 'cn'
import type { Client, Project, Task } from '@/api/types'

export function ProjectDot({ color, className }: { color: string; className?: string }) {
  return <span aria-hidden="true" className={cn('inline-block size-2.5 shrink-0 rounded-full', className)} style={{ backgroundColor: color }} />
}

interface ProjectLabelProps {
  project: Project
  task?: Task
  client?: Client
  className?: string
}

/** "● Website · Design · Acme": the project with its task and client. */
export function ProjectLabel({ project, task, client, className }: ProjectLabelProps) {
  return (
    <span className={cn('flex min-w-0 items-center gap-1.5 text-sm', className)}>
      <ProjectDot color={project.color} />
      <span className="truncate font-medium">{project.name}</span>
      {task && <span className="text-muted-foreground truncate">· {task.name}</span>}
      {client && <span className="text-muted-foreground hidden truncate sm:inline">· {client.name}</span>}
    </span>
  )
}
