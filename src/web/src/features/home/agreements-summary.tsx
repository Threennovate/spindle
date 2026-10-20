import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Skeleton } from '@/components/ui/skeleton'
import { formatHoursMinutes } from '@/lib/time/duration'
import { dayKey } from '@/lib/time/zone'
import { Bar } from '../agreements/agreements-card'
import { agreementStatus, formatPeriod } from '../agreements/format'
import { profitabilityQuery, type AgreementProfitability, type ProjectProfitability } from '../agreements/queries'
import { ProjectDot } from '../catalog/project-label'
import { useWorkspace } from '../workspace'

/** Agreements listed before the rest are left to Profitability. */
const SHOWN = 4

/**
 * The agreements running now, for admins: a retainer's hours this period against those included, a fixed price's
 * hours against those quoted. Hours only; what they earn is on Profitability. Not shown in a workspace without any.
 */
export function AgreementsSummary() {
  const { workspaceId, timeZone, isAdmin } = useWorkspace()
  const query = useQuery({ ...profitabilityQuery(workspaceId), enabled: isAdmin })
  const today = dayKey(new Date(), timeZone)

  if (!isAdmin || query.isError || (query.isSuccess && query.data.projects.every((project) => project.agreements.length === 0))) {
    return null
  }

  const running = (query.data?.projects ?? []).flatMap((project) =>
    project.agreements.filter((item) => agreementStatus(item.agreement, today).tone === 'active').map((item) => ({ project, item })),
  )

  return (
    <section aria-labelledby="agreements-title" className="bg-card ring-foreground/10 flex w-full shrink-0 flex-col gap-3 rounded-xl p-4 ring-1 lg:w-88">
      <div className="flex items-baseline justify-between gap-2">
        <h3 id="agreements-title" className="text-base font-medium">
          Agreements
        </h3>
        <Link to="/profitability" className="text-primary-text text-xs font-medium underline-offset-4 hover:underline">
          Profitability
        </Link>
      </div>
      {query.isPending ? (
        <div className="grid gap-3" aria-busy="true">
          <Skeleton className="h-10" />
          <Skeleton className="h-10" />
        </div>
      ) : running.length === 0 ? (
        <p className="text-muted-foreground text-sm">No agreement is running now. Fixed prices in progress and current retainers show here.</p>
      ) : (
        <ul className="grid gap-4">
          {running.slice(0, SHOWN).map(({ project, item }) => (
            <AgreementLine key={item.agreement.id} project={project} item={item} today={today} />
          ))}
          {running.length > SHOWN && <li className="text-muted-foreground text-xs">{running.length - SHOWN} more on Profitability</li>}
        </ul>
      )}
    </section>
  )
}

function AgreementLine({ project, item, today }: { project: ProjectProfitability; item: AgreementProfitability; today: string }) {
  const { agreement } = item
  const period = agreement.kind === 'retainer' ? item.periods.find((each) => each.start <= today && today <= each.end) : undefined
  const used = agreement.kind === 'retainer' ? (period?.seconds ?? 0) : item.trackedSeconds
  const limit = agreement.kind === 'retainer' ? (period?.includedSeconds ?? null) : agreement.hours != null ? Math.round(agreement.hours * 3600) : null
  const over = limit !== null && used > limit
  const note =
    agreement.kind === 'retainer'
      ? `${period ? formatPeriod(period, today) : 'This period'} · ${limit !== null ? 'included hours' : 'no hour limit'}`
      : limit !== null
        ? 'Fixed price · hours quoted'
        : 'Fixed price'

  return (
    <li className="grid gap-1.5">
      <div className="flex items-baseline justify-between gap-3 text-sm">
        <span className="flex min-w-0 items-center gap-1.5">
          <ProjectDot color={project.color} />
          <Link to="/projects/$projectId" params={{ projectId: project.projectId }} className="truncate font-medium underline-offset-4 hover:underline">
            {agreement.name}
          </Link>
        </span>
        <span className={over ? 'shrink-0 text-xs text-orange-700 tabular-nums dark:text-orange-400' : 'text-muted-foreground shrink-0 text-xs tabular-nums'}>
          {formatHoursMinutes(used)}
          {limit !== null && ` of ${formatHoursMinutes(limit)}`}
        </span>
      </div>
      {limit !== null && limit > 0 && (
        <Bar
          label={`${agreement.name}: hours used`}
          parts={[
            { ratio: Math.min(used, limit) / Math.max(used, limit), className: 'bg-primary' },
            { ratio: Math.max(0, used - limit) / Math.max(used, limit), className: 'bg-orange-500' },
          ]}
        />
      )}
      <p className="text-muted-foreground truncate text-xs">
        {project.name} · {note}
      </p>
    </li>
  )
}
