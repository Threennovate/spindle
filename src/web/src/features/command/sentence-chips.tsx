import { BadgeDollarSign, Building2, Hash, X } from 'lucide-react'
import { useEffect, useRef } from 'react'
import { ProjectDot } from '../catalog/project-label'
import type { useCatalog } from '../catalog/use-catalog'
import type { SentenceChip } from './sentence'

export type Catalog = ReturnType<typeof useCatalog>

/** The chips before the text in the palette's field, as one line with it. */
export function SentenceChips({ chips, catalog, onRemove }: { chips: readonly SentenceChip[]; catalog: Catalog; onRemove: (index: number) => void }) {
  const strip = useRef<HTMLUListElement>(null)
  // The chip just picked is the one to see.
  useEffect(() => {
    strip.current?.scrollTo({ left: strip.current.scrollWidth })
  }, [chips.length])

  return (
    <ul ref={strip} aria-label="Picked for the entry" className="flex max-w-[45%] shrink-0 sm:max-w-[60%] items-center gap-1 overflow-x-auto [scrollbar-width:none]">
      {chips.map((chip, index) => {
        const { content, name } = describe(chip, catalog)
        return (
          <li
            key={`${chip.kind}-${index}`}
            className="bg-muted text-foreground animate-in fade-in-0 zoom-in-95 flex h-7 max-w-48 shrink-0 items-center gap-1.5 rounded-md pr-0.5 pl-2 text-[13px] font-medium duration-100 motion-reduce:animate-none"
          >
            {content}
            <button
              type="button"
              tabIndex={-1}
              aria-label={`Remove ${name}`}
              onClick={() => onRemove(index)}
              className="text-muted-foreground hover:bg-background hover:text-foreground flex size-6 shrink-0 items-center justify-center rounded transition-colors"
            >
              <X className="size-3" />
            </button>
          </li>
        )
      })}
    </ul>
  )
}

function describe(chip: SentenceChip, catalog: Catalog) {
  switch (chip.kind) {
    case 'project': {
      const project = catalog.project(chip.projectId)
      const task = catalog.task(chip.taskId)
      const name = task ? `${project?.name ?? ''} · ${task.name}` : (project?.name ?? '')
      return {
        name,
        content: (
          <>
            <ProjectDot color={project?.color ?? 'currentColor'} />
            <span className="truncate">{task?.name ?? project?.name}</span>
          </>
        ),
      }
    }
    case 'client': {
      const name = catalog.client(chip.clientId)?.name ?? ''
      return {
        name,
        content: (
          <>
            <Building2 className="text-muted-foreground size-3.5 shrink-0" aria-hidden="true" />
            <span className="truncate">{name}</span>
          </>
        ),
      }
    }
    case 'tag': {
      const name = catalog.tag(chip.tagId)?.name ?? ''
      return {
        name: `tag ${name}`,
        content: (
          <>
            <Hash className="text-muted-foreground size-3.5 shrink-0" aria-hidden="true" />
            <span className="truncate">{name}</span>
          </>
        ),
      }
    }
    case 'billable':
      return {
        name: 'billable',
        content: (
          <>
            <BadgeDollarSign className="text-muted-foreground size-3.5 shrink-0" aria-hidden="true" />
            <span>Billable</span>
          </>
        ),
      }
  }
}
