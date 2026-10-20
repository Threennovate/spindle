import { useQuery } from '@tanstack/react-query'
import { cn } from 'cn'
import { Maximize2, Plus, Split, TriangleAlert, X } from 'lucide-react'
import { useCommandState } from 'cmdk'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { TimeEntry } from '@/api/types'
import { Keys } from '@/components/kbd'
import { formatDurationAs } from '@/lib/time/duration'
import { useNow } from '@/lib/use-now'
import { ProjectDot } from '../catalog/project-label'
import { partColor, partName, splitDescription } from '../catalog/split-names'
import type { Catalog } from '../catalog/use-catalog'
import { useCatalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { editEntry } from '../time-entries/events'
import { currentEntryQuery } from '../time-entries/queries'
import { missingInParts } from '../time-entries/rules'
import { addPercent, allocate, moveBoundary, removePercent, setPercent, sharesRequest, type SplitPart } from '../time-entries/split'
import { useTimerActions } from '../time-entries/timer-actions'
import { useWorkspace } from '../workspace'
import { projectPage } from './palette-pages'
import { usePalette } from './palette-context'
import { PaletteGroup, PaletteRow } from './palette-row'
import { PALETTE_PARTS, type SplitState } from './split-state'
import type { PaletteItem } from './types'

const STEP = 5

const withPercents = (parts: readonly SplitPart[], percents: readonly number[]) => parts.map((part, i) => ({ ...part, percent: percents[i] }))
const percentsOf = (parts: readonly SplitPart[]) => parts.map((part) => part.percent)

/**
 * Splits an entry's time between projects from the keyboard: ↑/↓ choose a part, ←/→ make it bigger or smaller
 * (by 5, or 1 with Shift), a number gives it that share, and + adds another project. Enter saves, with Undo.
 */
export function SplitPage({ state, query }: { state: SplitState; query: string }) {
    const { workspaceId } = useWorkspace()
    const { settings, isLocked, lockMessage } = usePermissions()
    const catalog = useCatalog(workspaceId)
    const timer = useTimerActions(workspaceId)
    const { controls, sentence } = usePalette()
    const { data: running } = useQuery(currentEntryQuery)
    const [parts, setPartsState] = useState(state.parts)
    const setParts = (next: SplitPart[]) => {
        state.setParts(next)
        setPartsState(next)
    }
    // The part being changed is the selected row.
    const selected = useCommandState((command) => command.value)
    const index = Math.min(Number(/^split-part:(\d+)$/.exec(selected)?.[1] ?? 0), parts.length - 1)
    // A running entry keeps growing; a finished one is what it was.
    const entry = running?.id === state.entry.id ? running : state.entry
    const now = useNow(!entry.stop)
    const seconds = ((entry.stop ? Date.parse(entry.stop) : now) - Date.parse(entry.start)) / 1000

    // What is typed is the selected part's share, shown before it is saved.
    const typed = /^\d{1,2}$/.test(query.trim()) ? Number(query.trim()) : null
    const typedInto = (base: readonly SplitPart[], at: number) =>
        typed !== null && base.length > 1 ? withPercents(base, setPercent(percentsOf(base), at, typed)) : [...base]
    const shown = typedInto(parts, index)
    const durations = allocate(seconds, percentsOf(shown))
    const missing = missingInParts(settings, shown, catalog.project)
    const locked = isLocked(entry.start)

    function grow(by: number) {
        controls.setQuery('', { keepSelected: selected })
        if (shown.length < 2) return setParts(shown)
        const percents = percentsOf(shown)
        const before = percents.slice(0, index).reduce((sum, percent) => sum + percent, 0)
        // A part grows into the next one; the last part grows into the one before it.
        const next = index < shown.length - 1 ? moveBoundary(percents, index, before + percents[index] + by) : moveBoundary(percents, index - 1, before - by)
        setParts(withPercents(shown, next))
    }

    function addPart() {
        if (parts.length >= PALETTE_PARTS) return
        const base = shown
        setParts(base)
        controls.setQuery('')
        controls.push(
            projectPage({
                title: 'Split with',
                keepOpen: true,
                onPick: (projectId, taskId, project) => {
                    const added = [
                        ...base,
                        {
                            projectId,
                            taskId,
                            isBillable: project?.isBillable ?? false,
                            percent: 0,
                        },
                    ]
                    setParts(withPercents(added, addPercent(percentsOf(base))))
                    controls.back()
                    controls.select(`split-part:${base.length}`)
                },
            }),
        )
    }

    useEffect(() => {
        state.handleKeys((event) => {
            if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
                grow((event.key === 'ArrowRight' ? 1 : -1) * (event.shiftKey ? 1 : STEP))
                return true
            }
            if (event.key === '+' && !event.metaKey && !event.ctrlKey) {
                addPart()
                return true
            }
            return false
        })
    })

    // A share typed for one part stays with it when ↑/↓ choose another.
    const previous = useRef(index)
    useLayoutEffect(() => {
        if (previous.current !== index && typed !== null) {
            setParts(typedInto(parts, previous.current))
            controls.setQuery('', { keepSelected: selected })
        }
        previous.current = index
    })

    const single = {
        projectId: entry.projectId ?? null,
        taskId: entry.taskId ?? null,
        isBillable: entry.isBillable,
    }
    const save = () =>
        void timer.change(entry, sharesRequest(shown, single), `Split: ${shown.map((part) => `${partName(catalog, part)} ${part.percent}%`).join(' · ')}`)
    const problem = locked ? lockMessage : (Object.values(missing)[0] ?? null)
    const full = {
        label: 'Open the full editor',
        icon: Maximize2,
        // The editor starts from the split made here.
        run: () =>
            editEntry({
                entry: {
                    ...entry,
                    ...sharesRequest(shown, single),
                } as TimeEntry,
                split: true,
            }),
    }

    const rows: PaletteItem[] = shown.map((part, i) => ({
        id: `split-part:${i}`,
        section: 'timer',
        label: partName(catalog, part),
        leading: <ProjectDot color={partColor(catalog, part)} />,
        detail: missing[i] ? <span className="text-destructive">{missing[i]}</span> : undefined,
        meta: (
            <span className="flex items-center gap-2">
                <span>{formatDurationAs(durations[i], 'hm')}</span>
                <span className={cn('text-foreground w-9 text-right font-medium', i === index && 'text-primary-text')}>{part.percent}%</span>
            </span>
        ),
        primary: problem
            ? {
                  label: 'Can’t save',
                  keepOpen: true,
                  run: () => sentence.focus(),
              }
            : { label: 'Save split', icon: Split, run: save },
        secondary: full,
        more:
            shown.length > 2
                ? [
                      {
                          label: 'Remove this part',
                          icon: X,
                          keepOpen: true,
                          run: () => {
                              setParts(
                                  withPercents(
                                      shown.filter((_, j) => j !== i),
                                      removePercent(percentsOf(shown), i),
                                  ),
                              )
                              controls.back()
                          },
                      },
                  ]
                : [],
    }))

    return (
        <>
            <div className="px-5 pt-3 pb-2">
                <SplitBar
                    catalog={catalog}
                    parts={shown}
                    selected={index}
                    onMove={(boundary, position) => {
                        controls.setQuery('', { keepSelected: selected })
                        setParts(withPercents(shown, moveBoundary(percentsOf(shown), boundary, position)))
                    }}
                />
                <p className="text-muted-foreground mt-2 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs">
                    <span className="inline-flex items-center gap-1">
                        <Keys keys={['←', '→']} compact sequence={false} /> resize, with ⇧ by 1
                    </span>
                    <span>Type a share, e.g. 60</span>
                    {parts.length < PALETTE_PARTS && (
                        <span className="inline-flex items-center gap-1">
                            <Keys keys={['+']} /> another project
                        </span>
                    )}
                    <span className="tabular-nums">{formatDurationAs(seconds, 'hm')} in all</span>
                </p>
            </div>
            <PaletteGroup>
                {rows.map((row) => (
                    <PaletteRow key={row.id} item={row} />
                ))}
            </PaletteGroup>
            {problem && (
                <p className="text-destructive flex items-center gap-2 px-5 pb-2 text-xs" role="status">
                    <TriangleAlert className="size-3.5" aria-hidden="true" />
                    {problem}
                </p>
            )}
            <PaletteGroup>
                {parts.length < PALETTE_PARTS ? (
                    <PaletteRow
                        item={{
                            id: 'split-add',
                            section: 'timer',
                            label: 'Add another project…',
                            icon: Plus,
                            shortcut: ['+'],
                            primary: { label: 'Choose', run: addPart },
                        }}
                    />
                ) : (
                    <PaletteRow
                        item={{
                            id: 'split-full',
                            section: 'timer',
                            label: 'More parts in the full editor…',
                            icon: Maximize2,
                            primary: full,
                        }}
                    />
                )}
            </PaletteGroup>
        </>
    )
}

/**
 * The split as a bar in the projects' colours. Its boundaries can be dragged, with a finger too, where ←/→ are not
 * at hand.
 */
function SplitBar({
    catalog,
    parts,
    selected,
    onMove,
}: {
    catalog: Catalog
    parts: readonly SplitPart[]
    selected: number
    onMove: (boundary: number, position: number) => void
}) {
    const bar = useRef<HTMLDivElement>(null)
    const [dragging, setDragging] = useState<number | null>(null)
    const positionAt = (clientX: number) => {
        const rect = bar.current!.getBoundingClientRect()
        return ((clientX - rect.left) / rect.width) * 100
    }
    // Where each boundary is: after the parts before it.
    const boundaries = parts.slice(0, -1).map((_, i) => parts.slice(0, i + 1).reduce((sum, part) => sum + part.percent, 0))

    return (
        <div ref={bar} className="relative py-1.5">
            <div role="img" aria-label={`Split: ${splitDescription(catalog, parts)}`} className="bg-muted flex h-3 overflow-hidden rounded-full">
                {parts.map((part, i) => (
                    <span
                        key={i}
                        className={cn(
                            'h-full not-first:border-l-2 not-first:border-(--popover)',
                            dragging === null && 'transition-[width,opacity] duration-150 ease-out motion-reduce:transition-none',
                            i !== selected && 'opacity-60',
                        )}
                        style={{
                            width: `${part.percent}%`,
                            backgroundColor: partColor(catalog, part),
                        }}
                    />
                ))}
            </div>
            {boundaries.map((at, i) => (
                <span
                    key={i}
                    aria-hidden="true"
                    onPointerDown={(event) => {
                        event.preventDefault()
                        event.currentTarget.setPointerCapture(event.pointerId)
                        setDragging(i)
                    }}
                    onPointerMove={(event) => dragging === i && onMove(i, positionAt(event.clientX))}
                    onPointerUp={() => setDragging(null)}
                    onPointerCancel={() => setDragging(null)}
                    className="group/handle absolute top-0 flex h-6 w-6 -translate-x-1/2 cursor-ew-resize touch-none items-center justify-center"
                    style={{ left: `${at}%` }}
                >
                    <span
                        className={cn(
                            'bg-background h-4 w-1.5 rounded-full shadow ring-1 ring-black/10 transition-transform group-hover/handle:scale-y-125 dark:ring-white/20',
                            dragging === i && 'scale-y-125',
                        )}
                    />
                </span>
            ))}
        </div>
    )
}
