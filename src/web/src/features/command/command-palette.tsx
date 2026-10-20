import { useQuery } from '@tanstack/react-query'
import { Command as CommandPrimitive } from 'cmdk'
import { cn } from 'cn'
import { ChevronLeft, Search } from 'lucide-react'
import { Dialog as DialogPrimitive } from 'radix-ui'
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import type { Project, TimeEntry } from '@/api/types'
import { Kbd } from '@/components/kbd'
import { LogoMark } from '@/components/logo'
import { formatDuration } from '@/lib/time/duration'
import { useNow } from '@/lib/use-now'
import { ProjectDot } from '../catalog/project-label'
import { SplitDots } from '../catalog/split-label'
import { useCatalog } from '../catalog/use-catalog'
import { ProjectDialog } from '../projects/project-dialog'
import { InviteDialog } from '../team/invite-dialog'
import { currentEntryQuery } from '../time-entries/queries'
import { isSplit, partsOf } from '../time-entries/split'
import { useWorkspace } from '../workspace'
import { OPEN_PALETTE_EVENT } from './events'
import { actionsPage } from './palette-pages'
import { rank } from './rank'
import { PaletteContext, usePalette, type PaletteDialogs, type PaletteSentence } from './palette-context'
import { PaletteGroup, PaletteRow } from './palette-row'
import { useRootItems } from './root-items'
import { mentionAt, withoutMention, type Mention, type SentenceChip } from './sentence'
import { SentenceChips } from './sentence-chips'
import { chipText, useMentionItems, useSentenceItems } from './sentence-items'
import { SECTIONS, type PaletteAction, type PaletteControls, type PaletteItem, type PalettePage, type SectionId } from './types'
import { mostUsed, usageScore, useUsage, type Usage } from './usage'

interface Frame {
  page: PalettePage
  query: string
}

const ROOT: PalettePage = {
  title: '',
  placeholder: '',
  render: (query) => <RootPage query={query} />,
}

/**
 * The command palette: ⌘K (Ctrl+K) from anywhere. Everything the app does, and everything in the workspace, a few
 * keystrokes away. Actions finish inside the palette where they can, and say what they did with a way back.
 */
export function CommandPalette() {
  const { workspaceId, workspace } = useWorkspace()
  const [open, setOpen] = useState(false)
  const [frames, setFrames] = useState<Frame[]>([{ page: ROOT, query: '' }])
  const [selected, setSelected] = useState('')
  const [projectDialog, setProjectDialog] = useState<Parameters<PaletteDialogs['project']>[0] | null>(null)
  const [inviting, setInviting] = useState(false)
  const { usage, record } = useUsage(workspaceId)
  // Rows register themselves here as they render; a plain map, as it only tells the footer about the selected row.
  const [registry] = useState(() => new Map<string, PaletteItem>())
  const [, setSelectedActions] = useState('')
  const inputRef = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  // Until the person moves the selection, it stays on the first row, even when rows arrive or reorder: Enter does what the top row says.
  const followTop = useRef(true)
  // cmdk selects the first row whenever the search changes; a page that changes its query itself can keep its row.
  const keepSelected = useRef<string | null>(null)
  const [nudge, setNudge] = useState(false)
  // Whether the action running now opened a page, which keeps the palette open.
  const pushed = useRef(false)
  const catalog = useCatalog(workspaceId)
  // The first page's line: chips picked from the lists, and where the caret is, to complete `@project` and `#tag`.
  const [chips, setChips] = useState<SentenceChip[]>([])
  const [caret, setCaret] = useState(0)
  // A chip turned back into text, e.g. "@Booking engine", is still completed as one, spaces and all.
  const [reverted, setReverted] = useState<{ start: number; end: number } | null>(null)

  const frame = frames[frames.length - 1]
  const depth = frames.length - 1
  const line = frames[0].query
  const mention = depth === 0 ? (revertedMention(line, caret, reverted) ?? mentionAt(line, caret)) : null
  const latest = useRef({ line, mention })

  const { data: running } = useQuery(currentEntryQuery)
  const openRef = useRef(open)
  const depthRef = useRef(0)
  useEffect(() => {
    openRef.current = open
    depthRef.current = frames.length - 1
    latest.current = { line, mention }
  })

  const startOver = useCallback(() => {
    followTop.current = true
    setFrames([{ page: ROOT, query: '' }])
    setSelected('')
    setChips([])
    setCaret(0)
    setReverted(null)
  }, [])

  const show = useCallback(
    (next: boolean) => {
      setOpen(next)
      if (next && !openRef.current) {
        // Opened again while it was still closing: the dialog stays mounted, so it neither starts over nor focuses the field.
        startOver()
        requestAnimationFrame(() => inputRef.current?.focus())
      } else if (!next) {
        // Every opening starts fresh, on the first page.
        // After the closing animation, unless it was opened again meanwhile.
        window.setTimeout(() => {
          if (!openRef.current) startOver()
        }, 150)
      }
    },
    [startOver],
  )

  useEffect(() => {
    function onKeyDown(event: globalThis.KeyboardEvent) {
      if ((event.metaKey || event.ctrlKey) && !event.altKey && !event.shiftKey && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        show(!openRef.current)
      }
    }
    const openFromEvent = () => show(true)
    window.addEventListener('keydown', onKeyDown)
    window.addEventListener(OPEN_PALETTE_EVENT, openFromEvent)
    return () => {
      window.removeEventListener('keydown', onKeyDown)
      window.removeEventListener(OPEN_PALETTE_EVENT, openFromEvent)
    }
  }, [show])

  useEffect(() => {
    const list = listRef.current
    if (!open || !list) return
    const selectTop = () => {
      const top = list.querySelector<HTMLElement>('[cmdk-item]:not([aria-disabled="true"])')?.dataset.value
      if (followTop.current && top) {
        setSelected((current) => (current === top ? current : top))
      }
    }
    selectTop()
    const observer = new MutationObserver(selectTop)
    observer.observe(list, { childList: true, subtree: true })
    return () => observer.disconnect()
  }, [open])

  const controls = useMemo<PaletteControls>(
    () => ({
      close: () => show(false),
      push: (page) => {
        pushed.current = true
        followTop.current = true
        setFrames((current) => [...current, { page, query: '' }])
        setSelected('')
      },
      back: () => {
        followTop.current = true
        setFrames((current) => (current.length > 1 ? current.slice(0, -1) : current))
        setSelected('')
      },
      setQuery: (query, options) => {
        if (options?.keepSelected) {
          keepSelected.current = options.keepSelected
          requestAnimationFrame(() => (keepSelected.current = null))
        }
        setFrames((current) => [...current.slice(0, -1), { ...current[current.length - 1], query }])
      },
      select: (id) => {
        followTop.current = false
        setSelected(id)
      },
    }),
    [show],
  )

  const dialogs = useMemo<PaletteDialogs>(
    () => ({
      project: (options) => {
        show(false)
        setProjectDialog(options)
      },
      invite: () => {
        show(false)
        setInviting(true)
      },
    }),
    [show],
  )

  const sentence = useMemo<PaletteSentence>(() => {
    const setLine = (text: string, at: number) => {
      followTop.current = true
      setFrames((current) => [{ ...current[0], query: text }, ...current.slice(1)])
      setCaret(at)
      requestAnimationFrame(() => {
        inputRef.current?.focus()
        inputRef.current?.setSelectionRange(at, at)
      })
    }
    return {
      chips,
      mention,
      addChip: (chip) => {
        const { line: text, mention: typing } = latest.current
        const next = typing ? withoutMention(text, typing) : { text, caret: text.length }
        setChips((current) => {
          if (chip.kind === 'project' || chip.kind === 'client') {
            return [...current.filter((c) => c.kind !== 'project' && c.kind !== 'client'), chip]
          }
          const known = current.some((c) => (c.kind === 'tag' && chip.kind === 'tag' ? c.tagId === chip.tagId : c.kind === chip.kind))
          return known ? current : [...current, chip]
        })
        setReverted(null)
        setLine(next.text, next.caret)
      },
      removeChip: (index) => {
        setChips((current) => current.filter((_, i) => i !== index))
        inputRef.current?.focus()
      },
      append: (text) => {
        const trimmed = latest.current.line.replace(/\s+$/, '')
        const next = trimmed ? `${trimmed} ${text}` : text
        setLine(next, next.length)
      },
      focus: () => inputRef.current?.focus(),
    }
  }, [chips, mention])

  const run = useCallback(
    (item: PaletteItem, action: PaletteAction) => {
      // Only the first page teaches the palette what is used; the pages inside are steps of one command.
      if (depthRef.current === 0) {
        record(item.id)
      }
      pushed.current = false
      void action.run(controls)
      if (!pushed.current && !action.keepOpen) {
        show(false)
      }
    },
    [controls, record, show],
  )

  const context = useMemo(
    () => ({ controls, dialogs, usage, record, run, register: (item: PaletteItem, actions?: string) => {
        registry.set(item.id, item)
        if (actions !== undefined) setSelectedActions(`${item.id}|${actions}`)
      },
      sentence,
    }),
    [controls, dialogs, usage, record, run, registry, sentence],
  )

  const current = registry.get(selected)

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.nativeEvent.isComposing) return
    if (frame.page.onKeyDown?.(event)) {
      event.preventDefault()
      return
    }
    if (['ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'].includes(event.key)) {
      followTop.current = false
    }
    const input = inputRef.current
    if (event.key === 'Backspace' && !frame.query && depth > 0) {
      event.preventDefault()
      controls.back()
    } else if (event.key === 'Backspace' && depth === 0 && chips.length > 0 && input?.selectionStart === 0 && input.selectionEnd === 0) {
      // The last chip turns back into text, to change what was picked.
      event.preventDefault()
      const token = chipText(chips[chips.length - 1], catalog)
      setChips(chips.slice(0, -1))
      const text = frame.query ? `${token} ${frame.query}` : token
      followTop.current = true
      controls.setQuery(text)
      setCaret(token.length)
      setReverted(token.startsWith('@') || token.startsWith('#') ? { start: 0, end: token.length } : null)
      requestAnimationFrame(() => input.setSelectionRange(token.length, token.length))
    } else if (event.key === 'Tab' && !event.shiftKey) {
      event.preventDefault()
      if (mention && current?.id.startsWith('mention:')) {
        run(current, current.primary)
      } else if (current && (current.secondary || current.more?.length)) {
        controls.push(actionsPage(current))
      }
    } else if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
      event.preventDefault()
      if (current?.secondary) {
        run(current, current.secondary)
      }
    }
  }

  return (
    <PaletteContext.Provider value={context}>
      <DialogPrimitive.Root open={open} onOpenChange={show}>
        <DialogPrimitive.Portal>
          <DialogPrimitive.Overlay className="data-open:animate-in data-open:fade-in-0 data-closed:animate-out data-closed:fade-out-0 fixed inset-0 z-50 bg-black/15 duration-150 supports-backdrop-filter:backdrop-blur-[2px] dark:bg-black/40" />
          <DialogPrimitive.Content
            aria-label="Command palette"
            aria-describedby={undefined}
            onOpenAutoFocus={(event) => {
              event.preventDefault()
              inputRef.current?.focus()
            }}
            onEscapeKeyDown={(event) => {
              // Escape steps back a page before it closes the palette.
              if (depth > 0) {
                event.preventDefault()
                controls.back()
              }
            }}
            className={cn(
              'bg-popover text-popover-foreground fixed top-3 left-1/2 z-50 w-[calc(100%-1.5rem)] max-w-[640px] -translate-x-1/2 overflow-hidden rounded-2xl shadow-2xl ring-1 shadow-black/20 ring-black/5 outline-none sm:top-[12vh] dark:ring-white/10',
              'data-open:animate-in data-open:fade-in-0 data-open:zoom-in-[0.98] data-open:slide-in-from-top-1 data-closed:animate-out data-closed:fade-out-0 data-closed:zoom-out-[0.98] duration-150 ease-out motion-reduce:animate-none',
            )}
          >
            <DialogPrimitive.Title className="sr-only">Command palette</DialogPrimitive.Title>
            <CommandPrimitive
              label="Command palette"
              shouldFilter={false}
              loop
              // cmdk trims the value, so the nudge's space changes only the prop: enough for cmdk to take the kept row.
              value={nudge ? `${selected} ` : selected}
              onValueChange={(value) => {
                const keep = keepSelected.current
                if (keep && value !== keep) {
                  // cmdk has already moved to the first row inside; the kept row is handed back to it.
                  setSelected(keep)
                  setNudge((current) => !current)
                } else {
                  setSelected(value)
                }
              }}
              onKeyDown={onKeyDown}
              className="flex flex-col"
            >
              <div className="flex h-14 items-center gap-2 border-b px-4">
                {depth > 0 ? (
                  <button
                    type="button"
                    onClick={controls.back}
                    aria-label="Back"
                    className="text-muted-foreground hover:bg-accent hover:text-foreground -ml-1.5 flex size-7 shrink-0 items-center justify-center rounded-md transition-colors"
                  >
                    <ChevronLeft className="size-4" />
                  </button>
                ) : (
                  <Search className="text-muted-foreground size-4 shrink-0" aria-hidden="true" />
                )}
                {depth > 0 && (
                  <span className="bg-muted text-foreground max-w-[45%] shrink-0 truncate rounded-md px-2 py-1 text-xs font-medium" key={depth}>
                    {frame.page.title}
                  </span>
                )}
                {depth === 0 && chips.length > 0 && <SentenceChips chips={chips} catalog={catalog} onRemove={sentence.removeChip} />}
                <CommandPrimitive.Input
                  ref={inputRef}
                  value={frame.query}
                  onValueChange={(value) => {
                    if (depth === 0) {
                      const at = inputRef.current?.selectionStart ?? value.length
                      setCaret(at)
                      // The reverted chip's text grows and shrinks with what is typed in it.
                      setReverted((range) => {
                        if (!range) return null
                        const end = range.end + value.length - frame.query.length
                        return at > range.start && at <= end ? { start: range.start, end } : null
                      })
                    }
                    followTop.current = true
                    controls.setQuery(value)
                  }}
                  onSelect={(event) => {
                    const at = event.currentTarget.selectionStart ?? 0
                    setCaret(at)
                    setReverted((range) => (range && (at <= range.start || at > range.end) ? null : range))
                  }}
                  aria-describedby={depth === 0 ? 'palette-line-help' : undefined}
                  placeholder={
                    depth > 0
                      ? frame.page.placeholder
                      : chips.length > 0
                        ? 'Describe it, add a time…'
                        : running
                          ? 'Search, or type what you’re doing next…'
                          : 'What are you working on? Or search…'
                  }
                  className="placeholder:text-muted-foreground h-full min-w-0 flex-1 bg-transparent text-[15px] outline-none"
                />
                <span id="palette-line-help" className="sr-only">
                  Type @ for a project, # for a tag, $ for billable, and a time at the end, such as since 9:40 or 9:00-10:30.
                </span>
                <Kbd className="hidden sm:inline-flex">esc</Kbd>
              </div>

              <CommandPrimitive.List
                ref={listRef}
                onPointerMove={() => (followTop.current = false)}
                className="h-(--cmdk-list-height) max-h-[min(460px,62dvh)] scroll-py-2 overflow-y-auto overscroll-contain transition-[height] duration-150 ease-out [scrollbar-width:thin] motion-reduce:transition-none">
                {/* Inside the measured part, so the list's height includes the padding. */}
                <div className="py-1.5">{frame.page.render(frame.query)}</div>
              </CommandPrimitive.List>

              <Footer item={current} depth={depth} workspaceName={workspace.name} />
            </CommandPrimitive>
          </DialogPrimitive.Content>
        </DialogPrimitive.Portal>
      </DialogPrimitive.Root>

      <ProjectDialog
        workspaceId={workspaceId}
        open={projectDialog !== null}
        onOpenChange={(isOpen) => !isOpen && setProjectDialog(null)}
        project={projectDialog?.project}
        initialName={projectDialog?.initialName}
        onSaved={(project: Project) => {
          const options = projectDialog
          setProjectDialog(null)
          options?.onSaved?.(project)
        }}
      />
      <InviteDialog workspaceId={workspaceId} workspaceName={workspace.name} open={inviting} onOpenChange={setInviting} />
    </PaletteContext.Provider>
  )
}

/** The mention a chip turned back into, while the caret is in it. */
function revertedMention(text: string, caret: number, range: { start: number; end: number } | null): Mention | null {
  const sign = range && text[range.start]
  if (!range || (sign !== '@' && sign !== '#') || caret <= range.start || caret > range.end) return null
  return { kind: sign, query: text.slice(range.start + 1, range.end), start: range.start, end: range.end }
}

/** What Enter, ⌘ Enter and Tab do for the selected row, so nothing happens by surprise. */
function Footer({ item, depth, workspaceName }: { item: PaletteItem | undefined; depth: number; workspaceName: string }) {
  const hasActions = !!item && (!!item.secondary || !!item.more?.length)
  return (
    <div className="bg-muted/40 text-muted-foreground flex h-10 items-center gap-3 border-t px-3 text-xs">
      <span className="flex min-w-0 items-center gap-1.5">
        {depth > 0 ? (
          <>
            <Kbd>backspace</Kbd>
            <span>Back</span>
          </>
        ) : (
          <>
            <LogoMark compact className="size-4" />
            <span className="truncate">{workspaceName}</span>
          </>
        )}
      </span>
      {item && (
        <span className="ml-auto flex shrink-0 items-center gap-3">
          <FooterHint label={item.primary.label} keys={['enter']} strong />
          {item.secondary && <FooterHint label={item.secondary.label} keys={['mod', 'enter']} className="hidden sm:flex" />}
          {hasActions && <FooterHint label="Actions" keys={['tab']} className="hidden sm:flex" />}
        </span>
      )}
    </div>
  )
}

function FooterHint({ label, keys, strong = false, className }: { label: string; keys: string[]; strong?: boolean; className?: string }) {
  return (
    <span className={cn('flex items-center gap-1.5', className)}>
      <span className={cn('max-w-48 truncate', strong && 'text-foreground font-medium')}>{label}</span>
      <span className="flex items-center gap-0.5">
        {keys.map((key) => (
          <Kbd key={key}>{key}</Kbd>
        ))}
      </span>
    </span>
  )
}

// ——— The first page ———

const SECTION_ORDER = Object.keys(SECTIONS) as SectionId[]
const SECTION_LIMITS: Partial<Record<SectionId, number>> = { projects: 5, tasks: 4, clients: 3, tags: 3, members: 3, entries: 4, reports: 4, go: 4, create: 3, account: 2 }

function RootPage({ query }: { query: string }) {
  const root = useRootItems(query)
  const { usage, sentence: line } = usePalette()
  const said = useSentenceItems(query)
  const mentioned = useMentionItems()
  const [now] = useState(() => Date.now())
  const typed = query.trim()

  if (!typed && line.chips.length === 0) {
    return <NowPage root={root} usage={usage} now={now} />
  }

  const preview = (heading?: string) =>
    said.preview && (
      <PaletteGroup key="preview" heading={heading}>
        <PaletteRow item={said.preview} />
      </PaletteGroup>
    )

  // Completing `@project` or `#tag`: what fits comes first, and Tab or Enter makes it a chip. When nothing fits, the
  // line stays as typed, so Enter still saves it, e.g. "Fix #482".
  if (mentioned && line.mention) {
    const rows = (items: PaletteItem[]) => items.map((item) => <PaletteRow key={item.id} item={item} query={line.mention!.query} />)
    return (
      <>
        {mentioned.matches.length > 0 && <PaletteGroup heading={line.mention.kind === '@' ? 'Project' : 'Tag'}>{rows(mentioned.matches)}</PaletteGroup>}
        {preview(mentioned.matches.length > 0 ? SECTIONS.fallback : undefined)}
        {mentioned.create && <PaletteGroup>{rows([mentioned.create])}</PaletteGroup>}
      </>
    )
  }

  // With chips, the text describes the entry; searching it would only get in the way.
  // Earlier entries come from the server a moment later; until then the last answer is narrowed to what is typed now.
  const scored = typed && line.chips.length === 0 ? rank([...root.items, ...root.entries], typed, (id) => usageScore(usage[id], now)) : []

  const sections = new Map<SectionId, { item: PaletteItem; score: number }[]>()
  for (const result of scored) {
    const list = sections.get(result.item.section) ?? []
    if (list.length < (SECTION_LIMITS[result.item.section] ?? 6)) {
      list.push(result)
      sections.set(result.item.section, list)
    }
  }
  const ordered = [...sections.entries()].sort(([a, x], [b, y]) => y[0].score - x[0].score || SECTION_ORDER.indexOf(a) - SECTION_ORDER.indexOf(b))
  // What was typed reads as a description when nothing fits it well, or when it names a project, a tag or a time;
  // then saving it comes first.
  const best = ordered[0]?.[1][0]?.score ?? 0
  const previewFirst = said.explicit || best < 60

  return (
    <>
      {said.commands.length > 0 && (
        <PaletteGroup>
          {said.commands.map((item) => (
            <PaletteRow key={item.id} item={item} />
          ))}
        </PaletteGroup>
      )}
      {previewFirst && preview(said.commands.length > 0 ? SECTIONS.fallback : undefined)}
      {ordered.map(([section, results]) => (
        <PaletteGroup key={section} heading={SECTIONS[section]}>
          {results.map(({ item }) => (
            <PaletteRow key={item.id} item={item} query={typed} />
          ))}
        </PaletteGroup>
      ))}
      {!previewFirst && preview(SECTIONS.fallback)}
      {said.asWritten && (
        <PaletteGroup>
          <PaletteRow item={said.asWritten} />
        </PaletteGroup>
      )}
    </>
  )
}

/** Before anything is typed: the timer, what to pick up again, what this person uses most, and where to go. */
function NowPage({ root, usage, now }: { root: ReturnType<typeof useRootItems>; usage: Usage; now: number }) {
  const shown = new Set([...root.now.timer, ...root.now.continue].map((item) => item.id))
  const byId = new Map(root.items.map((item) => [item.id, item]))
  const recent = mostUsed(usage, now, 8)
    .filter((id) => !shown.has(id) && byId.has(id))
    .slice(0, 4)
    .map((id) => byId.get(id)!)
  const recentIds = new Set(recent.map((item) => item.id))
  const pages = root.items.filter((item) => item.section === 'go' && !recentIds.has(item.id))

  return (
    <>
      {root.running && <RunningCard entry={root.running} />}
      <PaletteGroup heading={root.running ? undefined : SECTIONS.timer}>
        {root.now.timer.map((item) => (
          <PaletteRow key={item.id} item={item} />
        ))}
      </PaletteGroup>
      {root.now.continue.length > 0 && (
        <PaletteGroup heading={SECTIONS.continue}>
          {root.now.continue.map((item) => (
            <PaletteRow key={item.id} item={item} />
          ))}
        </PaletteGroup>
      )}
      {recent.length > 0 && (
        <PaletteGroup heading={SECTIONS.recent}>
          {recent.map((item) => (
            <PaletteRow key={item.id} item={item} />
          ))}
        </PaletteGroup>
      )}
      <PaletteGroup heading={SECTIONS.go}>
        {pages.map((item) => (
          <PaletteRow key={item.id} item={item} />
        ))}
      </PaletteGroup>
      <p className="text-muted-foreground px-5 pt-2 pb-3 text-xs text-pretty">
        Search projects, clients, tags, reports and earlier entries. Or type what you’re working on and press <Kbd>enter</Kbd> to start: add{' '}
        <b className="text-foreground/80 font-medium">@project</b>, <b className="text-foreground/80 font-medium">#tag</b>,{' '}
        <b className="text-foreground/80 font-medium">$</b> or a time such as <b className="text-foreground/80 font-medium">since 9:40</b> or{' '}
        <b className="text-foreground/80 font-medium">yesterday 9-10:30</b>.
      </p>
    </>
  )
}

/** The running timer at the top of the palette, ticking. */
function RunningCard({ entry }: { entry: TimeEntry }) {
  const catalog = useCatalog(entry.workspaceId)
  const now = useNow()
  const project = catalog.project(entry.projectId)
  const task = catalog.task(entry.taskId)
  let where: ReactNode = <span className="text-muted-foreground">No project</span>
  if (isSplit(entry)) {
    const parts = partsOf(entry)
    where = (
      <>
        <SplitDots catalog={catalog} parts={parts} />
        <span className="truncate">{parts.map((part) => `${catalog.project(part.projectId)?.name ?? 'No project'} ${part.percent}%`).join(' · ')}</span>
      </>
    )
  } else if (project) {
    where = (
      <>
        <ProjectDot color={project.color} />
        <span className="truncate">
          {project.name}
          {task && <span className="text-muted-foreground"> · {task.name}</span>}
        </span>
      </>
    )
  }

  return (
    <div className="mx-3 mt-1.5 mb-1 flex items-center gap-3 rounded-xl border bg-[color-mix(in_srgb,var(--primary)_8%,var(--popover))] px-3.5 py-2.5">
      <span className="relative flex size-2.5 shrink-0" aria-hidden="true">
        <span className="bg-primary absolute inline-flex size-full animate-ping rounded-full opacity-50 motion-reduce:animate-none" />
        <span className="bg-primary relative inline-flex size-2.5 rounded-full" />
      </span>
      <span className="grid min-w-0 flex-1 gap-0.5">
        <span className="truncate text-sm font-medium">{entry.description || 'No description'}</span>
        <span className="flex min-w-0 items-center gap-1.5 text-xs">{where}</span>
      </span>
      <span className="font-mono text-lg font-semibold tabular-nums" aria-label="Running for">
        {formatDuration((now - Date.parse(entry.start)) / 1000)}
      </span>
    </div>
  )
}
