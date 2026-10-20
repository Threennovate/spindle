import { formatMoney, formatRate } from '@/lib/money'
import { PROJECT_COLORS } from '../catalog/colors'

/** Round hour marks for a chart's axis that reach at least `maxHours`: about four steps of a friendly size. */
export function hourTicks(maxHours: number): number[] {
  const steps = [0.25, 0.5, 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 60, 80, 100, 150, 200, 250, 300, 400, 500, 750, 1000, 1500, 2000, 2500, 5000]
  const step = steps.find((candidate) => candidate * 4 >= maxHours) ?? Math.ceil(maxHours / 4 / 1000) * 1000
  const count = Math.max(1, Math.ceil(maxHours / step))
  return Array.from({ length: count + 1 }, (_, index) => index * step)
}

/** An axis label for a number of hours: 30m, 1h 30m, 8h. */
export function formatHourTick(hours: number): string {
  if (hours === 0) {
    return '0'
  }
  const whole = Math.floor(hours)
  const minutes = Math.round((hours - whole) * 60)
  return whole === 0 ? `${minutes}m` : minutes ? `${whole}h ${minutes}m` : `${whole}h`
}

/** Groups keep their project's color; the rest get one from the palette by position, and "without" is grey. */
export function groupColor(group: { key: string; color?: string | null }, index: number): string {
  if (group.color) {
    return group.color
  }
  return group.key === 'none' ? 'var(--muted-foreground)' : PROJECT_COLORS[index % PROJECT_COLORS.length]
}

/** The share of a whole as a rounded percentage, e.g. 33.3%. */
export function percent(part: number, whole: number): string {
  if (whole <= 0) {
    return '0%'
  }
  const value = (part / whole) * 100
  return `${value >= 10 || value === 0 ? Math.round(value) : value.toFixed(1)}%`
}

/** An amount in the workspace currency, or a dash where there is none to show. */
export function money(amount: number | null | undefined, currency: string): string {
  return amount === null || amount === undefined ? '—' : formatMoney(amount, currency)
}

/**
 * What each tracked hour earned: the amount over all the time tracked, billable or not. With billed time entered
 * separately it shows how well the work pays, e.g. calls billed but not tracked raise it.
 */
export function perHour(amount: number | null | undefined, seconds: number, currency: string): string {
  return amount == null || seconds <= 0 ? '—' : formatRate(amount / (seconds / 3600), currency)
}

/** Whether a report's billed time differs from its billable time: some was entered separately. Admins only. */
export function hasBilledTime(totals: { billableSeconds: number; billedSeconds?: number | null }): boolean {
  return totals.billedSeconds != null && totals.billedSeconds !== totals.billableSeconds
}
