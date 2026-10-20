import type { PlanPrice, Subscription } from '@/api/types'
import { describeDaysLeft, daysLeft } from './trial'

export interface PlanDescription {
  badge: string
  tone: 'good' | 'neutral' | 'bad'
  title: string
  text: string
  /** What paying would do now: start a plan, or manage the one there is. Comped organizations have nothing to do. */
  action: 'choose' | 'manage' | null
}

/**
 * What the plan page says about a subscription. Days are formatted by the caller, in the person's time zone and date
 * format, and the end of a trial or paid period is shown as its last day.
 */
export function describePlan(subscription: Subscription, organizationName: string, formatLastDay: (end: string) => string, now = new Date()): PlanDescription {
  const { status, access, trialEndsAt, paidUntil, hasPlan, nextChargeAt, cancelsAtPeriodEnd } = subscription
  const readOnly = `${organizationName} is read-only: everything is still here and can be read, reported and exported, but no time can be added or changed until you choose a plan.`

  switch (status) {
    case 'trialing':
      if (hasPlan) {
        const firstCharge = nextChargeAt ?? trialEndsAt
        return {
          badge: 'Plan chosen',
          tone: 'good',
          title: firstCharge ? `Starts after ${formatLastDay(firstCharge)}` : 'Plan chosen',
          text: 'The trial goes on until then, with nothing charged. The first charge comes when it ends, for the seats there are then.',
          action: 'manage',
        }
      }
      if (access === 'readOnly' || !trialEndsAt) {
        return {
          badge: 'Trial ended',
          tone: 'bad',
          title: 'The trial has ended',
          text: trialEndsAt ? `Its last day was ${formatLastDay(trialEndsAt)}. ${readOnly}` : readOnly,
          action: 'choose',
        }
      }
      return {
        badge: 'Free trial',
        tone: 'neutral',
        title: describeDaysLeft(daysLeft(trialEndsAt, now)),
        text: `The trial lasts until the end of ${formatLastDay(trialEndsAt)}, with everything working. If you choose a plan before then, you're only charged once the trial is over.`,
        action: 'choose',
      }
    case 'active':
      if (cancelsAtPeriodEnd && paidUntil) {
        return {
          badge: 'Canceled',
          tone: 'neutral',
          title: `Works until ${formatLastDay(paidUntil)}`,
          text: 'The plan won’t renew. You can resume it under Manage subscription until then.',
          action: 'manage',
        }
      }
      return {
        badge: 'Paid',
        tone: 'good',
        title: paidUntil ? `Paid until ${formatLastDay(paidUntil)}` : 'Paid',
        text: 'The plan renews by itself, and its seats follow the people in the organization.',
        action: 'manage',
      }
    case 'pastDue':
      return {
        badge: 'Payment failed',
        tone: 'bad',
        title: 'A payment didn’t go through',
        text: 'It is being tried again, and everything keeps working meanwhile. Check the payment method under Manage subscription.',
        action: 'manage',
      }
    case 'canceled':
      if (access === 'readOnly' || !paidUntil) {
        return { badge: 'Ended', tone: 'bad', title: 'The plan has ended', text: readOnly, action: 'choose' }
      }
      return {
        badge: 'Canceled',
        tone: 'neutral',
        title: `Works until ${formatLastDay(paidUntil)}`,
        text: 'The plan won’t renew. Choose one again to keep tracking time after that.',
        action: 'choose',
      }
    case 'comped':
      return {
        badge: 'Free',
        tone: 'good',
        title: 'Free, on us',
        text: `${organizationName} gets everything in Spindle at no charge.`,
        action: null,
      }
  }
}

/** "€5 per person a month", from a price per seat. */
export function describePrice(price: PlanPrice, locale?: string) {
  const amount = new Intl.NumberFormat(locale, {
    style: 'currency',
    currency: price.currency,
    minimumFractionDigits: Number.isInteger(price.unitAmount) ? 0 : 2,
  }).format(price.unitAmount)
  return `${amount} per person a ${price.interval === 'year' ? 'year' : 'month'}`
}
