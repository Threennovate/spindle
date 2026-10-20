import { Link } from '@tanstack/react-router'
import { Skeleton } from '@/components/ui/skeleton'
import { ProjectDot } from '../catalog/project-label'
import type { Catalog } from '../catalog/use-catalog'
import { percent } from '../reports/charts'
import { useDurationFormat } from '../workspace'
import type { WeekSummary } from './week'

/** Rows shown before the rest are left to the report. */
const SHOWN_ROWS = 8

/**
 * Where the week went, by project: its share of the week and how much of it came from split entries, each part counted
 * on its own project.
 */
export function ProjectsCard({ summary, catalog, reportSearch }: { summary: WeekSummary | null; catalog: Catalog; reportSearch: { from: string; to: string } }) {
  const duration = useDurationFormat()
  const rows = summary?.projects ?? []
  const anySplit = rows.some((row) => row.splitSeconds > 0)

  return (
    <section aria-labelledby="week-projects-title" className="bg-card ring-foreground/10 grid min-w-0 flex-1 content-start gap-2 rounded-xl p-4 ring-1">
      <h3 id="week-projects-title" className="text-base font-medium">
        Projects
      </h3>
      {!summary ? (
        <div className="grid gap-2" aria-busy="true">
          <Skeleton className="h-8" />
          <Skeleton className="h-8" />
          <Skeleton className="h-8" />
        </div>
      ) : rows.length === 0 ? (
        <p className="text-muted-foreground text-sm">Each project’s share of the week shows here, with split entries counted on each of their projects.</p>
      ) : (
        <div>
          <table className="w-full table-fixed text-sm">
            <thead>
              <tr className="text-muted-foreground text-left text-xs">
                <th scope="col" className="py-1.5 pr-3 font-medium sm:w-[40%]">
                  Project
                </th>
                <th scope="col" className="hidden py-1.5 pr-3 font-medium sm:table-cell">
                  <span className="sr-only">Share of the week</span>
                </th>
                {anySplit && (
                  <th scope="col" className="w-24 py-1.5 pr-3 text-right font-medium" title="Time from entries split between projects">
                    From splits
                  </th>
                )}
                <th scope="col" className="w-24 py-1.5 text-right font-medium">
                  Time
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.slice(0, SHOWN_ROWS).map((row) => {
                const project = catalog.project(row.projectId)
                const client = catalog.client(project?.clientId)
                const color = project?.color ?? 'var(--muted-foreground)'
                return (
                  <tr key={row.projectId ?? 'none'} className="border-t">
                    <td className="py-2 pr-3">
                      <span className="flex min-w-0 items-center gap-1.5">
                        <ProjectDot color={color} />
                        {project ? (
                          <Link to="/projects/$projectId" params={{ projectId: project.id }} className="min-w-0 truncate font-medium underline-offset-4 hover:underline">
                            {project.name}
                          </Link>
                        ) : (
                          <span className="text-muted-foreground truncate">No project</span>
                        )}
                        {client && <span className="text-muted-foreground hidden min-w-0 shrink-[3] truncate sm:inline">· {client.name}</span>}
                      </span>
                    </td>
                    <td className="hidden py-2 pr-3 sm:table-cell">
                      <span className="bg-muted flex h-1.5 overflow-hidden rounded-full" title={`${percent(row.seconds, summary.seconds)} of the week`}>
                        <span className="h-full rounded-full" style={{ width: `${(row.seconds / summary.seconds) * 100}%`, background: color }} />
                      </span>
                    </td>
                    {anySplit && <td className="text-muted-foreground py-2 pr-3 text-right tabular-nums">{row.splitSeconds > 0 ? duration(row.splitSeconds) : '–'}</td>}
                    <td className="py-2 text-right font-medium tabular-nums">{duration(row.seconds)}</td>
                  </tr>
                )
              })}
            </tbody>
          </table>
          {rows.length > SHOWN_ROWS && (
            <Link to="/reports" search={{ period: 'custom', ...reportSearch }} className="text-primary-text mt-2 inline-block text-xs font-medium underline-offset-4 hover:underline">
              {rows.length - SHOWN_ROWS} more in the report
            </Link>
          )}
        </div>
      )}
    </section>
  )
}
