import { cn } from 'cn'
import { DollarSign, Folder, Tag as TagIcon } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { ProjectLabel } from '../catalog/project-label'
import { ProjectPicker } from '../catalog/project-picker'
import { SplitLabel } from '../catalog/split-label'
import { splitDescription } from '../catalog/split-names'
import { TagPicker } from '../catalog/tag-picker'
import type { Catalog } from '../catalog/use-catalog'
import type { SplitPart } from './split'

/**
 * The project button of the timer: the chosen project, or an icon to choose one. A split entry shows its parts and
 * opens the entry, where the split is changed; `onSplit` offers to split the time from the picker.
 */
export function ProjectButton({
  workspaceId,
  catalog,
  projectId,
  taskId,
  onChange,
  parts,
  onEditSplit,
  onSplit,
}: {
  workspaceId: string
  catalog: Catalog
  projectId: string | null
  taskId: string | null
  onChange: (projectId: string | null, taskId: string | null) => void
  parts?: SplitPart[]
  onEditSplit?: () => void
  onSplit?: () => void
}) {
  const project = catalog.project(projectId)

  if (parts && parts.length > 1) {
    return (
      <Button variant="ghost" className="max-w-72 px-2" aria-label={`Split: ${splitDescription(catalog, parts)}`} onClick={onEditSplit}>
        <SplitLabel catalog={catalog} parts={parts} />
      </Button>
    )
  }

  return (
    <ProjectPicker workspaceId={workspaceId} projectId={projectId} taskId={taskId} onChange={onChange} onSplit={onSplit}>
      {project ? (
        <Button variant="ghost" className="max-w-56 px-2" aria-label={`Project: ${project.name}`}>
          <ProjectLabel project={project} task={catalog.task(taskId)} />
        </Button>
      ) : (
        <Button variant="ghost" size="icon-lg" aria-label="Choose project">
          <Folder className="text-muted-foreground" />
        </Button>
      )}
    </ProjectPicker>
  )
}

export function TagsButton({
  workspaceId,
  tagIds,
  onChange,
}: {
  workspaceId: string
  tagIds: string[]
  onChange: (tagIds: string[]) => void
}) {
  return (
    <TagPicker workspaceId={workspaceId} value={tagIds} onChange={onChange}>
      <Button variant="ghost" size={tagIds.length ? 'lg' : 'icon-lg'} aria-label={tagIds.length ? `Tags: ${tagIds.length} selected` : 'Choose tags'}>
        <TagIcon className={cn(tagIds.length ? 'text-primary-text' : 'text-muted-foreground')} />
        {tagIds.length > 0 && <span className="text-primary-text text-xs font-semibold">{tagIds.length}</span>}
      </Button>
    </TagPicker>
  )
}

export function BillableButton({ isBillable, onChange }: { isBillable: boolean; onChange: (isBillable: boolean) => void }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <Button
          variant="ghost"
          size="icon-lg"
          aria-label="Billable"
          aria-pressed={isBillable}
          onClick={() => onChange(!isBillable)}
          className={cn(isBillable && 'bg-primary/10 hover:bg-primary/15')}
        >
          <DollarSign className={cn(isBillable ? 'text-primary-text' : 'text-muted-foreground')} />
        </Button>
      </TooltipTrigger>
      <TooltipContent>{isBillable ? 'Billable' : 'Non-billable'}</TooltipContent>
    </Tooltip>
  )
}
