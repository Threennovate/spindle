import { dayKey } from '@/lib/time/zone'

const DAY = 24 * 60 * 60 * 1000

/** How early owners and admins start hearing that the trial ends, as the reminder emails do. */
export const TRIAL_ENDING_DAYS = 14

/** The last day before an end, which is often midnight at the start of the next one. */
export function lastDayKey(end: string, timeZone: string): string {
  return dayKey(new Date(new Date(end).getTime() - 1), timeZone)
}

/** Whole days until an end, counting a part of a day as one; 0 once it has passed. */
export function daysLeft(end: string, now: Date = new Date()): number {
  return Math.max(0, Math.ceil((new Date(end).getTime() - now.getTime()) / DAY))
}

/** "12 days left", or "Last day" when less than a day is left. */
export function describeDaysLeft(days: number): string {
  return days <= 1 ? 'Last day' : `${days} days left`
}

/** The quiet label next to Plan while the trial runs: "87 days left", and whether it ends soon. */
export function trialLabel(trialEndsAt: string | null | undefined, now: Date = new Date()): { text: string; soon: boolean } | null {
  if (!trialEndsAt) return null
  const days = daysLeft(trialEndsAt, now)
  return { text: describeDaysLeft(days), soon: days <= TRIAL_ENDING_DAYS }
}
