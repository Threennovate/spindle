import { cn } from 'cn'
import { DollarSign, Equal, Plus, TextQuote, X } from 'lucide-react'
import { useRef, useState, type KeyboardEvent, type PointerEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { formatHoursMinutes, parseDuration } from '@/lib/time/duration'
import { ProjectLabel } from '../catalog/project-label'
import { ProjectPicker } from '../catalog/project-picker'
import { partColor, partName } from '../catalog/split-names'
import type { Catalog } from '../catalog/use-catalog'
import { useDescriptionCompletion } from './use-description-completion'
import {
  MAX_PARTS,
  addPercent,
  allocate,
  describesParts,
  equalPercents,
  moveBoundary,
  percentOfDuration,
  removePercent,
  setPercent,
  type SplitPart,
} from './split'

/** Dragging snaps to this many percent; holding Alt (Option) moves by one. */
const DRAG_STEP = 5

interface SplitEditorProps {
  workspaceId: string
  catalog: Catalog
  parts: SplitPart[]
  /** The entry's whole duration, to show each part's time. */
  seconds: number
  /** A running entry's duration keeps changing, so parts are set by percentage only. */
  running: boolean
  /** Errors for a part, by its position, or for the split as a whole. */
  errors: { parts: Record<number, string | undefined>; split?: string }
  onChange: (parts: SplitPart[]) => void
  /** Whether the picker for another part is open, e.g. right after the split began. */
  adding: boolean
  onAddingChange: (open: boolean) => void
  /** Goes back to one description for all parts, while each part has its own. */
  onDescribeAllAsOne: () => void
}

/**
 * Splits an entry's time between projects: a bar whose dividers are dragged, and a row per part where its project,
 * share, time and billable flag can be set, and its own description when the parts are described one by one. Removing
 * all but one part ends the split.
 */
export function SplitEditor({
  workspaceId,
  catalog,
  parts,
  seconds,
  running,
  errors,
  onChange,
  adding,
  onAddingChange,
  onDescribeAllAsOne,
}: SplitEditorProps) {
  const perPart = describesParts(parts)
  const percents = parts.map((part) => part.percent)
  // Whole minutes, divided so that the parts shown add up to the duration shown.
  const partSeconds = allocate(Math.round(seconds / 60), percents).map((minutes) => minutes * 60)
  const taken = (except: number) => (projectId: string | null, taskId: string | null) =>
    parts.some((part, i) => i !== except && part.projectId === projectId && part.taskId === taskId)

  function withPercents(next: number[]) {
    onChange(parts.map((part, i) => ({ ...part, percent: next[i] })))
  }

  function change(index: number, values: Partial<SplitPart>) {
    onChange(parts.map((part, i) => (i === index ? { ...part, ...values } : part)))
  }

  function add(projectId: string | null, taskId: string | null) {
    const next = addPercent(percents)
    onChange([
      ...parts.map((part, i) => ({ ...part, percent: next[i] })),
      {
        projectId,
        taskId,
        isBillable: catalog.project(projectId)?.isBillable ?? false,
        percent: next[parts.length],
        ...(perPart && { description: '' }),
      },
    ])
  }

  function remove(index: number) {
    const next = removePercent(percents, index)
    onChange(parts.filter((_, i) => i !== index).map((part, i) => ({ ...part, percent: next[i] })))
  }

  const isEqual = equalPercents(parts.length).every((value, i) => value === percents[i])

  return (
    <div className="grid gap-3" role="group" aria-label="Time split between projects">
      <SplitBar catalog={catalog} parts={parts} onPercents={withPercents} />

      <ul className="grid gap-1">
        {parts.map((part, i) => {
          const project = catalog.project(part.projectId)
          const error = errors.parts[i]
          return (
            <li key={i} className="grid gap-1">
              <div className="grid grid-cols-[minmax(0,1fr)_auto_auto_auto_auto] items-center gap-1">
                <ProjectPicker
                  workspaceId={workspaceId}
                  projectId={part.projectId}
                  taskId={part.taskId}
                  taken={taken(i)}
                  onChange={(projectId, taskId) =>
                    change(i, { projectId, taskId, isBillable: catalog.project(projectId)?.isBillable ?? part.isBillable })
                  }
                >
                  <Button
                    type="button"
                    variant="ghost"
                    className="min-w-0 justify-start px-2"
                    aria-label={`Part ${i + 1}: ${partName(catalog, part)}`}
                    aria-invalid={error ? true : undefined}
                  >
                    {project ? (
                      <ProjectLabel project={project} task={catalog.task(part.taskId)} />
                    ) : (
                      <span className="text-muted-foreground flex items-center gap-1.5 text-sm">
                        <span className="border-muted-foreground size-2.5 shrink-0 rounded-full border border-dashed" />
                        No project
                      </span>
                    )}
                  </Button>
                </ProjectPicker>
                <PercentField
                  label={`Share of ${partName(catalog, part)}`}
                  value={part.percent}
                  onCommit={(value) => withPercents(setPercent(percents, i, value))}
                />
                <PartDuration
                  label={`Time on ${partName(catalog, part)}`}
                  seconds={partSeconds[i]}
                  disabled={running}
                  onCommit={(value) => withPercents(setPercent(percents, i, percentOfDuration(value, seconds)))}
                />
                <Tooltip>
                  <TooltipTrigger asChild>
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      aria-label={`Billable: ${partName(catalog, part)}`}
                      aria-pressed={part.isBillable}
                      onClick={() => change(i, { isBillable: !part.isBillable })}
                      className={cn(part.isBillable && 'bg-primary/10 hover:bg-primary/15')}
                    >
                      <DollarSign className={cn(part.isBillable ? 'text-primary-text' : 'text-muted-foreground')} />
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent>{part.isBillable ? 'Billable' : 'Non-billable'}</TooltipContent>
                </Tooltip>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  aria-label={`Remove ${partName(catalog, part)} from the split`}
                  onClick={() => remove(i)}
                  className="text-muted-foreground"
                >
                  <X />
                </Button>
              </div>
              {part.description !== undefined && (
                <PartDescription
                  workspaceId={workspaceId}
                  id={`entry-part-description-${i}`}
                  label={`Description for ${partName(catalog, part)}`}
                  projectId={part.projectId}
                  value={part.description}
                  placeholder={`What did you do on ${catalog.project(part.projectId)?.name ?? 'this part'}?`}
                  onChange={(description) => change(i, { description })}
                />
              )}
              {error && <p className="text-destructive px-2 text-sm">{error}</p>}
            </li>
          )
        })}
      </ul>

      <div className="flex flex-wrap items-center gap-2">
        <ProjectPicker
          workspaceId={workspaceId}
          projectId={undefined}
          taskId={null}
          taken={taken(-1)}
          open={adding}
          onOpenChange={onAddingChange}
          placeholder="Add which project to the split?"
          onChange={add}
        >
          <Button type="button" variant="outline" size="sm" disabled={parts.length >= MAX_PARTS}>
            <Plus />
            Add project
          </Button>
        </ProjectPicker>
        {perPart && (
          <Button type="button" variant="ghost" size="sm" className="ml-auto" onClick={onDescribeAllAsOne}>
            <TextQuote />
            One description for all
          </Button>
        )}
        <Button type="button" variant="ghost" size="sm" className={cn(!perPart && 'ml-auto')} disabled={isEqual} onClick={() => withPercents(equalPercents(parts.length))}>
          <Equal />
          Split equally
        </Button>
      </div>
      {errors.split && <p className="text-destructive text-sm">{errors.split}</p>}
    </div>
  )
}

/**
 * The parts as segments of one bar, each as wide as its share. A divider between two parts is dragged to move time
 * between them; pressing anywhere on the bar moves the nearest divider there. Dividers are sliders for the keyboard.
 */
function SplitBar({ catalog, parts, onPercents }: { catalog: Catalog; parts: SplitPart[]; onPercents: (percents: number[]) => void }) {
  const bar = useRef<HTMLDivElement>(null)
  const [dragging, setDragging] = useState<number | null>(null)
  const percents = parts.map((part) => part.percent)
  const bounds = percents.slice(0, -1).map((_, i) => percents.slice(0, i + 1).reduce((a, b) => a + b, 0))

  function positionAt(clientX: number) {
    const rect = bar.current!.getBoundingClientRect()
    return ((clientX - rect.left) / rect.width) * 100
  }

  function drag(event: PointerEvent, index: number) {
    onPercents(moveBoundary(percents, index, positionAt(event.clientX), event.altKey ? 1 : DRAG_STEP))
  }

  function startDrag(event: PointerEvent<HTMLDivElement>, index: number) {
    if (event.button !== 0) {
      return
    }
    event.preventDefault()
    bar.current!.setPointerCapture(event.pointerId)
    setDragging(index)
    drag(event, index)
  }

  /** Pressing the bar takes hold of the divider nearest to where it was pressed. */
  function pressBar(event: PointerEvent<HTMLDivElement>) {
    const at = positionAt(event.clientX)
    const nearest = bounds.reduce((best, bound, i) => (Math.abs(bound - at) < Math.abs(bounds[best] - at) ? i : best), 0)
    startDrag(event, nearest)
    ;(event.currentTarget.querySelectorAll('[role="slider"]')[nearest] as HTMLElement | undefined)?.focus()
  }

  function nudge(event: KeyboardEvent, index: number) {
    const step = event.shiftKey ? 1 : DRAG_STEP
    const delta = { ArrowLeft: -step, ArrowDown: -step, ArrowRight: step, ArrowUp: step }[event.key]
    const to = { Home: 0, End: 100 }[event.key]
    if (delta === undefined && to === undefined) {
      return
    }
    event.preventDefault()
    onPercents(moveBoundary(percents, index, to ?? bounds[index] + delta!))
  }

  /** Double-clicking a divider evens out the two parts beside it. */
  function evenOut(index: number) {
    onPercents(moveBoundary(percents, index, bounds[index] - percents[index] + Math.ceil((percents[index] + percents[index + 1]) / 2)))
  }

  return (
    <div
      ref={bar}
      className={cn('relative flex h-11 touch-none rounded-lg select-none', dragging === null ? 'cursor-pointer' : 'cursor-col-resize')}
      onPointerDown={pressBar}
      onPointerMove={(event) => dragging !== null && drag(event, dragging)}
      onPointerUp={() => setDragging(null)}
      onPointerCancel={() => setDragging(null)}
    >
      {parts.map((part, i) => {
        const color = partColor(catalog, part)
        return (
          <div
            key={i}
            className={cn(
              'relative flex min-w-0 flex-col justify-center overflow-hidden px-2.5 transition-[width] duration-75',
              i === 0 && 'rounded-l-lg',
              i === parts.length - 1 && 'rounded-r-lg',
            )}
            style={{ width: `${part.percent}%`, backgroundColor: `color-mix(in oklab, ${color} 18%, var(--card))` }}
          >
            <span className="absolute inset-x-0 bottom-0 h-1" style={{ backgroundColor: color }} />
            {part.percent >= 8 && (
              <span className="flex min-w-0 items-baseline gap-1.5 text-xs leading-tight">
                {part.percent >= 18 && <span className="truncate font-medium">{catalog.project(part.projectId)?.name ?? 'No project'}</span>}
                <span className="shrink-0 font-semibold tabular-nums">{part.percent}%</span>
              </span>
            )}
          </div>
        )
      })}
      {bounds.map((bound, i) => (
        <div
          key={i}
          role="slider"
          tabIndex={0}
          aria-label={`Divider between ${partName(catalog, parts[i])} and ${partName(catalog, parts[i + 1])}`}
          aria-valuemin={1}
          aria-valuemax={99}
          aria-valuenow={bound}
          aria-valuetext={`${partName(catalog, parts[i])} ${percents[i]}%, ${partName(catalog, parts[i + 1])} ${percents[i + 1]}%`}
          className="group/divider absolute inset-y-0 z-10 flex w-5 -translate-x-1/2 cursor-col-resize items-center justify-center outline-none"
          style={{ left: `${bound}%` }}
          onPointerDown={(event) => {
            event.stopPropagation()
            event.currentTarget.focus()
            startDrag(event, i)
          }}
          onKeyDown={(event) => nudge(event, i)}
          onDoubleClick={() => evenOut(i)}
        >
          <span className="bg-background absolute inset-y-0 w-0.5" />
          <span
            className={cn(
              'bg-foreground ring-background relative h-6 w-1.5 rounded-full shadow-sm ring-2 transition-transform',
              'group-hover/divider:scale-110 group-focus-visible/divider:ring-ring/50 group-focus-visible/divider:ring-[3px]',
              dragging === i && 'scale-110',
            )}
          />
          {dragging === i && (
            <span className="bg-foreground text-background pointer-events-none absolute -top-8 rounded-md px-1.5 py-0.5 text-xs font-medium whitespace-nowrap tabular-nums shadow">
              {percents[i]} · {percents[i + 1]}
            </span>
          )}
        </div>
      ))}
    </div>
  )
}

/** A part's share in whole percent, applied when the field is left or Enter is pressed. */
function PercentField({ label, value, onCommit }: { label: string; value: number; onCommit: (value: number) => void }) {
  const [text, setText] = useState<string | null>(null)

  function commit() {
    const typed = text === null ? NaN : Number.parseInt(text, 10)
    if (Number.isFinite(typed) && typed !== value) {
      onCommit(typed)
    }
    setText(null)
  }

  return (
    <div className="relative w-16">
      <Input
        aria-label={label}
        inputMode="numeric"
        value={text ?? String(value)}
        onFocus={(event) => {
          setText(String(value))
          event.currentTarget.select()
        }}
        onChange={(event) => setText(event.target.value.replace(/[^\d]/g, '').slice(0, 3))}
        onBlur={commit}
        onKeyDown={(event) => {
          if (event.key === 'Enter') {
            event.preventDefault()
            commit()
          } else if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
            event.preventDefault()
            onCommit(value + (event.key === 'ArrowUp' ? 1 : -1) * (event.shiftKey ? 10 : 1))
            setText(null)
          }
        }}
        className="h-8 pr-6 text-right tabular-nums"
      />
      <span className="text-muted-foreground pointer-events-none absolute top-1/2 right-2 -translate-y-1/2 text-sm">%</span>
    </div>
  )
}

/** A part's time; typing a duration sets the share that comes closest to it. */
function PartDuration({ label, seconds, disabled, onCommit }: { label: string; seconds: number; disabled: boolean; onCommit: (seconds: number) => void }) {
  const [text, setText] = useState<string | null>(null)

  function commit() {
    const typed = text === null ? null : parseDuration(text)
    if (typed !== null) {
      onCommit(typed)
    }
    setText(null)
  }

  return (
    <Input
      aria-label={label}
      value={text ?? formatHoursMinutes(seconds)}
      disabled={disabled}
      title={disabled ? 'The timer is running; set the share in percent.' : 'Type a duration, e.g. 1:15'}
      onFocus={(event) => {
        setText(formatHoursMinutes(seconds))
        event.currentTarget.select()
      }}
      onChange={(event) => setText(event.target.value)}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === 'Enter') {
          event.preventDefault()
          commit()
        }
      }}
      className="text-muted-foreground focus-visible:text-foreground h-8 w-20 border-transparent bg-transparent text-right font-mono text-xs tabular-nums shadow-none hover:border-input disabled:opacity-100"
    />
  )
}

interface PartDescriptionProps {
  workspaceId: string
  id: string
  label: string
  projectId: string | null
  value: string
  placeholder: string
  onChange: (description: string) => void
}

/** A part's own description, offering what was written before on its project first. */
function PartDescription({ workspaceId, id, label, projectId, value, placeholder, onChange }: PartDescriptionProps) {
  const completion = useDescriptionCompletion({ workspaceId, value, projectId, onPick: onChange })
  return (
    <div className="relative -mt-1">
      <Input
        {...completion.inputProps}
        id={id}
        aria-label={label}
        aria-controls={completion.visible ? completion.listId : undefined}
        autoComplete="off"
        value={value}
        placeholder={placeholder}
        onChange={(event) => {
          onChange(event.target.value)
          completion.onTyped()
        }}
        onKeyDown={completion.onKeyDown}
        onBlur={completion.onBlur}
        className="h-7 border-transparent bg-transparent pl-[1.625rem] text-sm shadow-none hover:border-input focus-visible:border-input dark:bg-transparent"
      />
      {completion.list}
    </div>
  )
}
