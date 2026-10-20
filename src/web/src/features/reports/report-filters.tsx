import { useQuery } from '@tanstack/react-query'
import { Search, SlidersHorizontal } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { cn } from 'cn'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useCatalog } from '../catalog/use-catalog'
import { groupsQuery, membersQuery } from '../team/queries'
import { useWorkspace } from '../workspace'
import { FilterSelect, type FilterSection } from './filter-select'
import { hasFilters, NONE_ID, ROUNDING_MINUTES, ROUNDINGS, type ReportSearch, type RoundingMode } from './search'

const ROUNDING_LABELS: Record<RoundingMode, string> = { none: 'No rounding', up: 'Round up', nearest: 'Round to nearest', down: 'Round down' }

interface ReportFiltersProps {
  search: ReportSearch
  onChange: (changes: Partial<ReportSearch>) => void
  onClear: () => void
}

/** What the report counts: whose time, on what, billable or not, and how it is rounded. */
export function ReportFilters({ search, onChange, onClear }: ReportFiltersProps) {
  const { workspaceId, isAdmin } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const { data: members = [] } = useQuery({ ...membersQuery(workspaceId), enabled: isAdmin })
  const { data: groups = [] } = useQuery({ ...groupsQuery(workspaceId), enabled: isAdmin })
  const groupIds = new Set(groups.map((group) => group.id))

  const projectSections: FilterSection[] = [
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
  ]
  const clientSections: FilterSection[] = [
    { options: [{ id: NONE_ID, label: 'No client' }] },
    { heading: 'Clients', options: catalog.clients.map((client) => ({ id: client.id, label: client.name, detail: client.archivedAt ? 'archived' : undefined })) },
  ]
  const tagSections: FilterSection[] = [
    { options: [{ id: NONE_ID, label: 'No tag' }] },
    { heading: 'Tags', options: catalog.tags.map((tag) => ({ id: tag.id, label: tag.name })) },
  ]
  const memberSections: FilterSection[] = [
    { heading: 'Groups', options: groups.map((group) => ({ id: group.id, label: group.name, detail: `${group.memberIds.length} ${group.memberIds.length === 1 ? 'member' : 'members'}` })) },
    { heading: 'Members', options: members.map((member) => ({ id: member.userId, label: member.fullName, keywords: [member.email] })) },
  ]

  return (
    <div role="group" aria-label="Filters" className="flex flex-wrap items-center gap-1">
      {isAdmin && (
        <FilterSelect
          label="Member"
          searchPlaceholder="Search members and groups"
          sections={memberSections}
          selected={[...(search.groups ?? []), ...(search.users ?? [])]}
          onChange={(ids) =>
            onChange({ groups: nonEmpty(ids.filter((id) => groupIds.has(id))), users: nonEmpty(ids.filter((id) => !groupIds.has(id))) })
          }
        />
      )}
      <FilterSelect label="Project" searchPlaceholder="Search projects" sections={projectSections} selected={search.projects ?? []} onChange={(ids) => onChange({ projects: nonEmpty(ids) })} />
      <FilterSelect label="Client" searchPlaceholder="Search clients" sections={clientSections} selected={search.clients ?? []} onChange={(ids) => onChange({ clients: nonEmpty(ids) })} />
      <FilterSelect label="Tag" searchPlaceholder="Search tags" sections={tagSections} selected={search.tags ?? []} onChange={(ids) => onChange({ tags: nonEmpty(ids) })} />

      <Select value={search.billable ?? 'all'} onValueChange={(value) => onChange({ billable: value === 'all' ? undefined : (value as 'yes' | 'no') })}>
        <SelectTrigger size="sm" aria-label="Billable" className={cn('border-transparent shadow-none', search.billable && 'bg-primary/10 text-primary-text')}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="all">Billable and not</SelectItem>
          <SelectItem value="yes">Billable only</SelectItem>
          <SelectItem value="no">Non-billable only</SelectItem>
        </SelectContent>
      </Select>

      <DescriptionFilter value={search.q ?? ''} onChange={(q) => onChange({ q: q || undefined })} />
      <RoundingFilter mode={search.rounding ?? 'none'} minutes={search.roundTo ?? 15} onChange={(rounding, roundTo) => onChange({ rounding: rounding === 'none' ? undefined : rounding, roundTo: rounding === 'none' ? undefined : roundTo })} />

      {hasFilters(search) && (
        <Button variant="link" size="sm" onClick={onClear}>
          Clear filters
        </Button>
      )}
    </div>
  )
}

function nonEmpty(ids: string[]): string[] | undefined {
  return ids.length ? ids : undefined
}

/** Narrows the report to descriptions containing some text, once the person stops typing. */
function DescriptionFilter({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const [text, setText] = useState(value)
  const [shown, setShown] = useState(value)
  const timer = useRef<number | undefined>(undefined)

  // Follow the address bar when it changes elsewhere, e.g. "Clear filters" or a saved report.
  if (value !== shown) {
    setShown(value)
    if (value !== text.trim()) {
      setText(value)
    }
  }

  useEffect(() => () => window.clearTimeout(timer.current), [])

  function handleChange(next: string) {
    setText(next)
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => onChange(next.trim()), 350)
  }

  return (
    <div className="relative">
      <Search aria-hidden="true" className="text-muted-foreground pointer-events-none absolute top-1/2 left-2 size-3.5 -translate-y-1/2" />
      <Input
        type="search"
        aria-label="Description contains"
        placeholder="Description"
        value={text}
        onChange={(event) => handleChange(event.target.value)}
        className={cn('h-8 w-40 pl-7 text-sm', value && 'border-primary/40')}
      />
    </div>
  )
}

function RoundingFilter({ mode, minutes, onChange }: { mode: RoundingMode; minutes: number; onChange: (mode: RoundingMode, minutes: number) => void }) {
  const active = mode !== 'none'
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="sm" className={cn(active && 'bg-primary/10 text-primary-text hover:text-primary-text')}>
          <SlidersHorizontal />
          {active ? `${ROUNDING_LABELS[mode]} · ${minutes} min` : 'Rounding'}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="grid w-64 gap-3">
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
