import { cn } from 'cn'
import { useEffect, useLayoutEffect, useRef, type ReactNode, type RefObject } from 'react'
import { ProjectDot } from '../catalog/project-label'
import { partColor, partName } from '../catalog/split-names'
import type { Catalog } from '../catalog/use-catalog'
import { findMentions, type MentionResult } from './mentions'
import type { Mentions } from './use-mentions'

/** Tints the mentions behind a transparent input; same font and padding as the input, scrolled along with it. */
export function MentionHighlights({ value, mentions, catalog, inputRef, className }: { value: string; mentions: Mentions; catalog: Catalog; inputRef: RefObject<HTMLInputElement | null>; className?: string }) {
  const layer = useRef<HTMLDivElement>(null)
  // The input scrolls long text sideways; the layer follows it.
  const follow = () => {
    if (layer.current && inputRef.current) {
      layer.current.scrollLeft = inputRef.current.scrollLeft
    }
  }
  useLayoutEffect(follow)
  useEffect(() => {
    const input = inputRef.current
    input?.addEventListener('scroll', follow)
    return () => input?.removeEventListener('scroll', follow)
  })
  const found = findMentions(value, mentions.projects)
  if (found.length === 0) {
    return null
  }
  const pieces: ReactNode[] = []
  let at = 0
  for (const mention of found) {
    pieces.push(value.slice(at, mention.start))
    const color = catalog.project(mention.projectId)?.color ?? 'var(--muted-foreground)'
    pieces.push(
      <mark key={mention.start} className="rounded-sm text-transparent" style={{ backgroundColor: `color-mix(in oklab, ${color} 30%, transparent)`, boxShadow: `0 0 0 2px color-mix(in oklab, ${color} 30%, transparent)` }}>
        {value.slice(mention.start, mention.end)}
      </mark>,
    )
    at = mention.end
  }
  pieces.push(value.slice(at))
  return (
    <div ref={layer} aria-hidden="true" className={cn('pointer-events-none absolute inset-0 overflow-hidden whitespace-pre text-transparent', className)}>
      {pieces}
    </div>
  )
}

/** The projects offered for a mention, under the field. */
export function MentionList({ id, mentions, catalog }: { id: string; mentions: Mentions; catalog: Catalog }) {
  return (
    <ul
      id={id}
      role="listbox"
      aria-label="Mention a project"
      className="bg-popover text-popover-foreground ring-foreground/10 absolute top-full left-0 z-50 mt-2 w-full max-w-sm overflow-hidden rounded-lg p-1 shadow-md ring-1"
    >
      {mentions.matches.map((project, index) => {
        const client = catalog.client(project.clientId)
        return (
          <li
            key={project.id}
            id={`${id}-${index}`}
            role="option"
            aria-selected={index === mentions.active}
            onMouseDown={(event) => event.preventDefault()}
            onClick={() => mentions.pick(project.name)}
            className={cn('flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm', index === mentions.active && 'bg-muted')}
          >
            <ProjectDot color={project.color} />
            <span className="truncate">{project.name}</span>
            {client && <span className="text-muted-foreground ml-auto truncate text-xs">{client.name}</span>}
          </li>
        )
      })}
    </ul>
  )
}

/** "● AnchorNet 50% Review PR #42 · ● FrameIt 50% Fix the export": where the mentions send the time and the texts. */
export function MentionPreview({ result, catalog }: { result: MentionResult | null; catalog: Catalog }) {
  return (
    <div
      role="status"
      className="bg-popover text-popover-foreground ring-foreground/10 absolute top-full left-0 z-50 mt-2 grid w-full max-w-xl gap-1 rounded-lg px-3 py-2 text-sm shadow-md ring-1"
    >
      {!result ? (
        <span className="text-destructive">Split the time between at most 6 projects.</span>
      ) : (
        <>
          {(result.parts ?? [{ projectId: result.projectId, taskId: result.taskId, isBillable: result.isBillable, percent: 100, description: result.description }]).map((part, i) => (
            <div key={i} className="flex min-w-0 items-baseline gap-2">
              <ProjectDot color={partColor(catalog, part)} className="translate-y-px" />
              <span className="shrink-0 font-medium">{partName(catalog, part)}</span>
              {result.parts && <span className="text-muted-foreground shrink-0 tabular-nums">{part.percent}%</span>}
              <span className={cn('truncate', !part.description && 'text-muted-foreground')}>{part.description || 'No description'}</span>
            </div>
          ))}
          <span className="text-muted-foreground text-xs">Each project's reports and timesheets show only its own line.</span>
        </>
      )}
    </div>
  )
}
