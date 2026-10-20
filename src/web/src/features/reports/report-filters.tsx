import { useQuery } from '@tanstack/react-query'
import { Building2, ChevronRight, CircleDashed, Coins, FolderKanban, Plus, Search, SlidersHorizontal, Tag, Users } from 'lucide-react'
import { useState, type FormEvent, type ReactNode } from 'react'
import { cn } from 'cn'
import { Button } from '@/components/ui/button'
import { Command, CommandGroup, CommandInput, CommandItem, CommandList, CommandSeparator } from '@/components/ui/command'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { matchKeywords, useCatalog } from '../catalog/use-catalog'
import { groupsQuery, membersQuery } from '../team/queries'
import { useWorkspace } from '../workspace'
import { FilterChip, FilterOptions } from './filter-select'
import { pickedNames, type FilterSection } from './filters'
import { NONE_ID, ROUNDING_MINUTES, ROUNDINGS, type ReportSearch, type RoundingMode } from './search'
import { whoseTime } from './views'

const ROUNDING_LABELS: Record<RoundingMode, string> = { none: 'No rounding', up: 'Round up', nearest: 'Round to nearest', down: 'Round down' }
const BILLABLE_LABELS = { yes: 'Billable only', no: 'Non-billable only' } as const

type ListFilter = 'projects' | 'clients' | 'tags'
const LIST_FILTERS: { key: ListFilter; label: string; icon: ReactNode; placeholder: string }[] = [
  { key: 'projects', label: 'Project', icon: <FolderKanban />, placeholder: 'Search projects' },
  { key: 'clients', label: 'Client', icon: <Building2 />, placeholder: 'Search clients' },
  { key: 'tags', label: 'Tag', icon: <Tag />, placeholder: 'Search tags' },
]

interface ReportFiltersProps {
  search: ReportSearch
  onChange: (changes: Partial<ReportSearch>) => void
}

/** What the report counts: whose time first, for admins, then a chip for each filter in use and "+ Filter" for more. */
export function ReportFilters({ search, onChange }: ReportFiltersProps) {
  const { isAdmin } = useWorkspace()
  const sections = useFilterSections()

  return (
    <div role="group" aria-label="Filters" className="flex min-w-0 flex-wrap items-center gap-1.5">
      {isAdmin && <WhoseTime search={search} onChange={onChange} />}
      {LIST_FILTERS.map(({ key, label, placeholder }) => {
        const selected = search[key]
        if (!selected) {
          return null
        }
        return (
          <FilterChip key={key} label={`${label} · ${pickedNames(sections[key], selected)}`} name={label.toLowerCase()} onClear={() => onChange({ [key]: undefined })}>
            {() => <FilterOptions sections={sections[key]} selected={selected} searchPlaceholder={placeholder} onChange={(ids) => onChange({ [key]: nonEmpty(ids) })} />}
          </FilterChip>
        )
      })}
      {search.billable && (
        <FilterChip label={BILLABLE_LABELS[search.billable]} name="billable" onClear={() => onChange({ billable: undefined })}>
          {(close) => (
            <BillableOptions
              value={search.billable}
              onChange={(billable) => {
                onChange({ billable })
                close()
              }}
            />
          )}
        </FilterChip>
      )}
      {search.q && (
        <FilterChip label={`Description · “${search.q}”`} name="description" onClear={() => onChange({ q: undefined })} contentClassName="p-3">
          {(close) => (
            <DescriptionForm
              value={search.q ?? ''}
              onApply={(q) => {
                onChange({ q })
                close()
              }}
            />
          )}
        </FilterChip>
      )}
      <AddFilter search={search} sections={sections} onChange={onChange} />
    </div>
  )
}

function useFilterSections(): Record<ListFilter, FilterSection[]> {
  const { workspaceId } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  return {
    projects: [
      { options: [{ id: NONE_ID, label: 'No project' }] },
      {
        heading: 'Projects',
        options: catalog.projects.map((project) => ({
          id: project.id,
          label: project.name,
          detail: [catalog.client(project.clientId)?.name, project.archivedAt ? 'archived' : undefined].filter(Boolean).join(' · ') || undefined,
          color: project.color,
        })),
      },
    ],
    clients: [
      { options: [{ id: NONE_ID, label: 'No client' }] },
      { heading: 'Clients', options: catalog.clients.map((client) => ({ id: client.id, label: client.name, detail: client.archivedAt ? 'archived' : undefined })) },
    ],
    tags: [
      { options: [{ id: NONE_ID, label: 'No tag' }] },
      { heading: 'Tags', options: catalog.tags.map((tag) => ({ id: tag.id, label: tag.name })) },
    ],
  }
}

function nonEmpty(ids: string[]): string[] | undefined {
  return ids.length ? ids : undefined
}

type AddPage = 'menu' | ListFilter | 'billable' | 'description'

/** One button for every filter: pick what to filter by, then how. */
function AddFilter({ search, sections, onChange }: ReportFiltersProps & { sections: Record<ListFilter, FilterSection[]> }) {
  const [open, setOpen] = useState(false)
  const [page, setPage] = useState<AddPage>('menu')
  const list = LIST_FILTERS.find((filter) => filter.key === page)

  function openChange(isOpen: boolean) {
    setOpen(isOpen)
    if (!isOpen) {
      setPage('menu')
    }
  }

  function apply(changes: Partial<ReportSearch>) {
    onChange(changes)
    openChange(false)
  }

  return (
    <Popover open={open} onOpenChange={openChange}>
      <PopoverTrigger asChild>
        <Button variant="ghost" className="text-muted-foreground h-8">
          <Plus />
          Filter
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className={cn('w-72 p-0', page === 'description' && 'p-3')}>
        {page === 'menu' && (
          <Command>
            <CommandList>
              <CommandGroup>
                {LIST_FILTERS.map(({ key, label, icon }) => (
                  <CommandItem key={key} value={label} onSelect={() => setPage(key)}>
                    <span className="text-muted-foreground flex">{icon}</span>
                    {label}
                    <FilterNote>{search[key] && `${search[key].length} picked`}</FilterNote>
                  </CommandItem>
                ))}
                <CommandItem value="Billable" onSelect={() => setPage('billable')}>
                  <Coins className="text-muted-foreground" />
                  Billable
                  <FilterNote>{search.billable && BILLABLE_LABELS[search.billable]}</FilterNote>
                </CommandItem>
                <CommandItem value="Description contains" onSelect={() => setPage('description')}>
                  <Search className="text-muted-foreground" />
                  Description contains…
                </CommandItem>
              </CommandGroup>
              <CommandSeparator />
              <CommandGroup heading="Time to sort out">
                <CommandItem value="Without a project" onSelect={() => apply({ projects: [NONE_ID] })}>
                  <CircleDashed className="text-muted-foreground" />
                  Without a project
                </CommandItem>
              </CommandGroup>
            </CommandList>
          </Command>
        )}
        {list && <FilterOptions sections={sections[list.key]} selected={search[list.key] ?? []} searchPlaceholder={list.placeholder} onChange={(ids) => onChange({ [list.key]: nonEmpty(ids) })} />}
        {page === 'billable' && <BillableOptions value={search.billable} onChange={(billable) => apply({ billable })} />}
        {page === 'description' && <DescriptionForm value={search.q ?? ''} onApply={(q) => apply({ q })} />}
      </PopoverContent>
    </Popover>
  )
}

/** What a filter is set to, or an arrow to set it. */
function FilterNote({ children }: { children: ReactNode }) {
  return <span className="text-muted-foreground ml-auto text-xs">{children || <ChevronRight className="size-3.5" />}</span>
}

function BillableOptions({ value, onChange }: { value: ReportSearch['billable']; onChange: (value: 'yes' | 'no') => void }) {
  return (
    <Command>
      <CommandList>
        <CommandGroup>
          {(['yes', 'no'] as const).map((option) => (
            <CommandItem key={option} value={option} data-checked={value === option} onSelect={() => onChange(option)}>
              {BILLABLE_LABELS[option]}
            </CommandItem>
          ))}
        </CommandGroup>
      </CommandList>
    </Command>
  )
}

/** Narrows the report to descriptions containing some text. */
function DescriptionForm({ value, onApply }: { value: string; onApply: (value: string | undefined) => void }) {
  const [text, setText] = useState(value)

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    onApply(text.trim() ? text.trim().slice(0, 200) : undefined)
  }

  return (
    <form onSubmit={handleSubmit} className="grid gap-2">
      <Label htmlFor="report-description">Description contains</Label>
      <Input id="report-description" type="search" value={text} onChange={(event) => setText(event.target.value)} placeholder="e.g. review" autoFocus />
      <Button type="submit" size="sm" className="justify-self-end">
        Apply
      </Button>
    </form>
  )
}

/** Whose time the report counts: the admin's own, everyone's, or some groups' and members'. */
function WhoseTime({ search, onChange }: ReportFiltersProps) {
  const { workspaceId, me } = useWorkspace()
  const { data: members = [] } = useQuery(membersQuery(workspaceId))
  const { data: groups = [] } = useQuery(groupsQuery(workspaceId))
  const whose = whoseTime(search, me.id)
  const selected = [...(search.groups ?? []), ...(search.users ?? [])]
  const groupIds = new Set(groups.map((group) => group.id))
  const sections: FilterSection[] = [
    { heading: 'Groups', options: groups.map((group) => ({ id: group.id, label: group.name, detail: `${group.memberIds.length} ${group.memberIds.length === 1 ? 'member' : 'members'}` })) },
    {
      heading: 'Members',
      options: members.map((member) =>
        member.userId === me.id
          ? { id: member.userId, label: 'Me', detail: member.fullName, keywords: [member.email] }
          : { id: member.userId, label: member.fullName, keywords: [member.email] },
      ),
    },
  ]
  const label = whose === 'me' ? 'Me' : whose === 'everyone' ? 'Everyone' : pickedNames(sections, selected)

  function choose(ids: string[]) {
    onChange({ groups: nonEmpty(ids.filter((id) => groupIds.has(id))), users: nonEmpty(ids.filter((id) => !groupIds.has(id))) })
  }

  return (
    <FilterChip label={label} name="whose time" aria-label={`Whose time: ${label}`} active={whose !== 'everyone'} icon={<Users />} contentClassName="w-80">
      {(close) => (
        <Command filter={matchKeywords}>
          <CommandInput placeholder="Search members and groups" />
          <CommandList>
            <CommandGroup>
              <CommandItem
                value="me"
                keywords={['me', me.fullName]}
                data-checked={whose === 'me'}
                onSelect={() => {
                  choose([me.id])
                  close()
                }}
              >
                Me
                <span className="text-muted-foreground truncate text-xs">{me.fullName}</span>
              </CommandItem>
              <CommandItem
                value="everyone"
                keywords={['everyone', 'all']}
                data-checked={whose === 'everyone'}
                onSelect={() => {
                  choose([])
                  close()
                }}
              >
                Everyone
                <span className="text-muted-foreground text-xs">
                  {members.length} {members.length === 1 ? 'person' : 'people'}
                </span>
              </CommandItem>
            </CommandGroup>
            {sections.map((section) =>
              section.options.length === 0 ? null : (
                <CommandGroup key={section.heading} heading={section.heading}>
                  {section.options.map((option) => (
                    <CommandItem
                      key={option.id}
                      value={option.id}
                      keywords={[option.label, option.detail ?? '', ...(option.keywords ?? [])]}
                      data-checked={selected.includes(option.id)}
                      onSelect={() => choose(selected.includes(option.id) ? selected.filter((id) => id !== option.id) : [...selected, option.id])}
                    >
                      <span className="truncate">{option.label}</span>
                      {option.detail && <span className="text-muted-foreground truncate text-xs">{option.detail}</span>}
                    </CommandItem>
                  ))}
                </CommandGroup>
              ),
            )}
          </CommandList>
        </Command>
      )}
    </FilterChip>
  )
}

/** How each entry's duration is rounded; it stays apart from the filters, since it changes the time, not which. */
export function RoundingFilter({ mode, minutes, onChange }: { mode: RoundingMode; minutes: number; onChange: (mode: RoundingMode, minutes: number) => void }) {
  const active = mode !== 'none'
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button variant="ghost" className={cn('h-8', active ? 'bg-primary/15 text-primary-text hover:text-primary-text' : 'text-muted-foreground')}>
          <SlidersHorizontal />
          {active ? `${ROUNDING_LABELS[mode]} · ${minutes} min` : 'Rounding'}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="grid w-64 gap-3">
        <p className="text-muted-foreground text-xs">Each entry's duration is rounded before it is added up and billed.</p>
        <div className="grid gap-1.5">
          <Label htmlFor="rounding-mode">Rounding</Label>
          <Select value={mode} onValueChange={(value) => onChange(value as RoundingMode, minutes)}>
            <SelectTrigger id="rounding-mode" className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {ROUNDINGS.map((rounding) => (
                <SelectItem key={rounding} value={rounding}>
                  {ROUNDING_LABELS[rounding]}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="rounding-minutes">To</Label>
          <Select value={String(minutes)} onValueChange={(value) => onChange(mode, Number(value))} disabled={!active}>
            <SelectTrigger id="rounding-minutes" className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {ROUNDING_MINUTES.map((value) => (
                <SelectItem key={value} value={String(value)}>
                  {value === 60 ? '1 hour' : `${value} minutes`}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </PopoverContent>
    </Popover>
  )
}
