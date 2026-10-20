import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { cn } from 'cn'
import { ArrowDown, ChevronRight } from 'lucide-react'
import { Fragment, useState } from 'react'
import { PageHeader } from '@/components/page-header'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { errorMessage } from '@/lib/error-message'
import { formatMoney } from '@/lib/money'
import { formatHoursMinutes } from '@/lib/time/duration'
import { dayKey } from '@/lib/time/zone'
import { usePageTitle } from '@/lib/title'
import { usePersistentState } from '@/lib/use-persistent-state'
import { ProjectDot } from '../catalog/project-label'
import { useCatalog } from '../catalog/use-catalog'
import { usePermissions } from '../settings/queries'
import { useWorkspace } from '../workspace'
import { agreementStatus, margin } from './format'
import { profitabilityQuery, type ProjectProfitability } from './queries'

const SORTS = ['revenue', 'profit', 'margin', 'laborCost', 'tracked'] as const
type Sort = (typeof SORTS)[number]

const STATUSES = ['active', 'all'] as const

const marginOf = (item: { profit: number; revenue: number }) => (item.revenue > 0 ? item.profit / item.revenue : Number.NEGATIVE_INFINITY)

const SORT_VALUE: Record<Sort, (project: ProjectProfitability) => number> = {
  revenue: (p) => p.revenue,
  profit: (p) => p.profit,
  margin: marginOf,
  laborCost: (p) => p.laborCost,
  tracked: (p) => p.trackedSeconds,
}

/** How profitable each project is, over all its time: what its client pays against the labor cost of the time. */
export function ProfitabilityPage() {
  usePageTitle('Profitability')
  const { workspaceId } = useWorkspace()
  const { isAdmin } = usePermissions()
  const query = useQuery({ ...profitabilityQuery(workspaceId), enabled: isAdmin })
  const [sort, setSort] = usePersistentState<Sort>('spindle:profitability-sort', 'revenue', SORTS)
  const [status, setStatus] = usePersistentState('spindle:profitability-status', 'active', STATUSES)

  return (
    <div className="mx-auto grid max-w-6xl gap-6">
      <PageHeader
        title="Profitability"
        description="What each project earns against the labor cost of the time spent on it, over all its time until today."
        actions={
          <Tabs value={status} onValueChange={(value) => setStatus(value as (typeof STATUSES)[number])}>
            <TabsList>
              <TabsTrigger value="active">Active</TabsTrigger>
              <TabsTrigger value="all">With archived</TabsTrigger>
            </TabsList>
          </Tabs>
        }
      />

      {!isAdmin ? (
        <p className="text-muted-foreground text-sm">Only admins can see how profitable projects are.</p>
      ) : query.isPending ? (
        <div className="grid gap-4">
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {[0, 1, 2, 3].map((i) => (
              <Skeleton key={i} className="h-24" />
            ))}
          </div>
          <Skeleton className="h-72" />
        </div>
      ) : query.isError ? (
        <p className="text-muted-foreground text-sm">{errorMessage(query.error, "Couldn't load profitability.")}</p>
      ) : (
        <Overview
          projects={query.data.projects.filter((p) => status === 'all' || !p.isArchived)}
          currency={query.data.currency}
          sort={sort}
          onSort={setSort}
        />
      )}
    </div>
  )
}

function Overview({ projects, currency, sort, onSort }: { projects: ProjectProfitability[]; currency: string; sort: Sort; onSort: (sort: Sort) => void }) {
  const revenue = projects.reduce((sum, p) => sum + p.revenue, 0)
  const laborCost = projects.reduce((sum, p) => sum + p.laborCost, 0)
  const profit = revenue - laborCost
  const tracked = projects.reduce((sum, p) => sum + p.trackedSeconds, 0)
  const sorted = [...projects].sort((a, b) => SORT_VALUE[sort](b) - SORT_VALUE[sort](a) || a.name.localeCompare(b.name))

  return (
    <>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <Figure label="Revenue" value={formatMoney(revenue, currency)} note={`${projects.length} ${projects.length === 1 ? 'project' : 'projects'}`} />
        <Figure label="Labor cost" value={formatMoney(laborCost, currency)} note={`${formatHoursMinutes(tracked)} tracked`} />
        <Figure label="Profit" value={formatMoney(profit, currency)} note={`${margin(profit, revenue)} margin`} negative={profit < 0} />
        <Figure
          label="Earned per hour"
          value={tracked > 0 ? formatMoney(revenue / (tracked / 3600), currency) : '—'}
          note={tracked > 0 ? `${formatMoney(laborCost / (tracked / 3600), currency)} labor cost per hour` : 'No time tracked'}
        />
      </div>

      {projects.length === 0 ? (
        <p className="text-muted-foreground rounded-lg border border-dashed p-6 text-center text-sm">
          No projects with time or agreements yet. Add the fixed prices and retainers agreed with clients on each project’s page.
        </p>
      ) : (
        <div className="overflow-x-auto rounded-lg border">
          <Table className="min-w-[44rem]">
            <TableHeader>
              <TableRow>
                <TableHead className="pl-4">Project</TableHead>
                <SortHead sort="tracked" current={sort} onSort={onSort}>
                  Tracked
                </SortHead>
                <SortHead sort="revenue" current={sort} onSort={onSort}>
                  Revenue
                </SortHead>
                <SortHead sort="laborCost" current={sort} onSort={onSort}>
                  Labor cost
                </SortHead>
                <SortHead sort="profit" current={sort} onSort={onSort}>
                  Profit
                </SortHead>
                <SortHead sort="margin" current={sort} onSort={onSort} className="pr-4">
                  Margin
                </SortHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {sorted.map((project) => (
                <ProjectRows key={project.projectId} project={project} currency={currency} />
              ))}
            </TableBody>
            <TableFooter>
              <TableRow>
                <TableCell className="pl-4 font-medium">Total</TableCell>
                <TableCell className="text-right tabular-nums">{formatHoursMinutes(tracked)}</TableCell>
                <TableCell className="text-right tabular-nums">{formatMoney(revenue, currency)}</TableCell>
                <TableCell className="text-right tabular-nums">{formatMoney(laborCost, currency)}</TableCell>
                <TableCell className={cn('text-right font-medium tabular-nums', profit < 0 && 'text-destructive')}>{formatMoney(profit, currency)}</TableCell>
                <TableCell className="pr-4 text-right tabular-nums">{margin(profit, revenue)}</TableCell>
              </TableRow>
            </TableFooter>
          </Table>
        </div>
      )}
      <p className="text-muted-foreground text-xs">
        Revenue counts each fixed price in full, done or not, retainers’ fees so far with any hours over the limit that are billed, and time outside
        agreements at its hourly rates. Labor cost is the time tracked at each member’s labor cost.
      </p>
    </>
  )
}

function ProjectRows({ project, currency }: { project: ProjectProfitability; currency: string }) {
  const { workspaceId, timeZone } = useWorkspace()
  const catalog = useCatalog(workspaceId)
  const [open, setOpen] = useState(false)
  const today = dayKey(new Date(), timeZone)
  const client = catalog.client(project.clientId)
  const hasOutside = project.outside.trackedSeconds > 0 || project.outside.amount !== 0
  const expandable = project.agreements.length > 0

  return (
    <Fragment>
      <TableRow className={cn(open && 'border-b-0')}>
        <TableCell className="pl-2">
          <div className="flex min-w-0 items-center gap-1">
            <Button
              variant="ghost"
              size="icon-xs"
              className={cn('text-muted-foreground shrink-0', !expandable && 'invisible')}
              aria-label={open ? `Hide the agreements of ${project.name}` : `Show the agreements of ${project.name}`}
              aria-expanded={open}
              onClick={() => setOpen(!open)}
            >
              <ChevronRight className={cn('transition-transform', open && 'rotate-90')} />
            </Button>
            <ProjectDot color={project.color} />
            <Link to="/projects/$projectId" params={{ projectId: project.projectId }} className="truncate font-medium hover:underline">
              {project.name}
            </Link>
            {client && <span className="text-muted-foreground truncate text-xs">{client.name}</span>}
            {project.isArchived && (
              <Badge variant="outline" className="shrink-0">
                Archived
              </Badge>
            )}
            {expandable && !open && (
              <span className="text-muted-foreground shrink-0 text-xs">
                · {project.agreements.length} {project.agreements.length === 1 ? 'agreement' : 'agreements'}
              </span>
            )}
          </div>
        </TableCell>
        <TableCell className="text-muted-foreground text-right tabular-nums">{formatHoursMinutes(project.trackedSeconds)}</TableCell>
        <TableCell className="text-right tabular-nums">{formatMoney(project.revenue, currency)}</TableCell>
        <TableCell className="text-right tabular-nums">{formatMoney(project.laborCost, currency)}</TableCell>
        <TableCell className={cn('text-right font-medium tabular-nums', project.profit < 0 && 'text-destructive')}>{formatMoney(project.profit, currency)}</TableCell>
        <TableCell className="pr-4 text-right tabular-nums">{margin(project.profit, project.revenue)}</TableCell>
      </TableRow>
      {open &&
        project.agreements.map((item) => {
          const status = agreementStatus(item.agreement, today)
          return (
            <TableRow key={item.agreement.id} className="bg-muted/30 border-b-0 text-sm">
              <TableCell className="pl-14">
                <div className="flex min-w-0 items-center gap-2">
                  <span className="truncate">{item.agreement.name}</span>
                  <span className="text-muted-foreground shrink-0 text-xs">
                    {item.agreement.kind === 'fixedPrice' ? 'Fixed price' : 'Retainer'} · {status.label}
                  </span>
                </div>
              </TableCell>
              <TableCell className="text-muted-foreground text-right tabular-nums">{formatHoursMinutes(item.trackedSeconds)}</TableCell>
              <TableCell className="text-right tabular-nums">{formatMoney(item.revenue, currency)}</TableCell>
              <TableCell className="text-right tabular-nums">{formatMoney(item.laborCost, currency)}</TableCell>
              <TableCell className={cn('text-right tabular-nums', item.profit < 0 && 'text-destructive')}>{formatMoney(item.profit, currency)}</TableCell>
              <TableCell className="pr-4 text-right tabular-nums">{margin(item.profit, item.revenue)}</TableCell>
            </TableRow>
          )
        })}
      {open && hasOutside && (
        <TableRow className="bg-muted/30 text-sm">
          <TableCell className="text-muted-foreground pl-14">Outside agreements, by the hour</TableCell>
          <TableCell className="text-muted-foreground text-right tabular-nums">{formatHoursMinutes(project.outside.trackedSeconds)}</TableCell>
          <TableCell className="text-right tabular-nums">{formatMoney(project.outside.amount, currency)}</TableCell>
          <TableCell className="text-right tabular-nums">{formatMoney(project.outside.laborCost, currency)}</TableCell>
          <TableCell className="text-right tabular-nums">{formatMoney(project.outside.amount - project.outside.laborCost, currency)}</TableCell>
          <TableCell className="pr-4 text-right tabular-nums">{margin(project.outside.amount - project.outside.laborCost, project.outside.amount)}</TableCell>
        </TableRow>
      )}
    </Fragment>
  )
}

function SortHead({ sort, current, onSort, className, children }: { sort: Sort; current: Sort; onSort: (sort: Sort) => void; className?: string; children: string }) {
  const active = sort === current
  return (
    <TableHead className={cn('text-right', className)} aria-sort={active ? 'descending' : undefined}>
      <button
        type="button"
        onClick={() => onSort(sort)}
        className={cn('hover:text-foreground inline-flex items-center gap-1', active ? 'text-foreground' : 'text-muted-foreground')}
      >
        {children}
        <ArrowDown className={cn('size-3', !active && 'invisible')} />
      </button>
    </TableHead>
  )
}

function Figure({ label, value, note, negative }: { label: string; value: string; note: string; negative?: boolean }) {
  return (
    <Card>
      <CardHeader>
        <CardDescription>{label}</CardDescription>
        <CardTitle className={cn('text-2xl tabular-nums', negative && 'text-destructive')}>{value}</CardTitle>
        <p className="text-muted-foreground text-xs">{note}</p>
      </CardHeader>
    </Card>
  )
}
