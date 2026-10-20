import { describe, expect, it } from 'vitest'
import type { Subscription } from '@/api/types'
import { describePlan, describePrice } from './plan'
import { daysLeft, describeDaysLeft, lastDayKey, trialLabel } from './trial'

const now = new Date('2026-10-09T12:00:00Z')
const day = (end: string) => end.slice(0, 10)

function subscription(change: Partial<Subscription>): Subscription {
  return {
    status: 'trialing',
    access: 'full',
    trialEndsAt: null,
    paidUntil: null,
    seats: 3,
    checkoutAvailable: false,
    hasPlan: false,
    interval: null,
    nextChargeAt: null,
    cancelsAtPeriodEnd: false,
    prices: [],
    ...change,
  }
}

describe('describePlan', () => {
  it('counts the days left in a trial and offers a plan', () => {
    const plan = describePlan(subscription({ trialEndsAt: '2026-10-19T12:00:00Z' }), 'Studio', day, now)

    expect(plan).toMatchObject({ badge: 'Free trial', title: '10 days left', action: 'choose' })
    expect(plan.text).toContain('2026-10-19')
  })

  it('says an ended trial left the organization read-only', () => {
    const plan = describePlan(subscription({ access: 'readOnly', trialEndsAt: '2026-10-01T12:00:00Z' }), 'Studio', day, now)

    expect(plan).toMatchObject({ badge: 'Trial ended', tone: 'bad', action: 'choose' })
    expect(plan.text).toContain('Studio is read-only')
  })

  it('lets a paying organization manage its subscription', () => {
    expect(describePlan(subscription({ status: 'active', paidUntil: '2026-11-09T00:00:00Z' }), 'Studio', day, now)).toMatchObject({
      title: 'Paid until 2026-11-09',
      action: 'manage',
    })
    expect(describePlan(subscription({ status: 'pastDue' }), 'Studio', day, now).action).toBe('manage')
  })

  it('keeps a canceled plan working to the end of what was paid, then read-only', () => {
    expect(describePlan(subscription({ status: 'canceled', paidUntil: '2026-11-09T00:00:00Z' }), 'Studio', day, now).title).toBe('Works until 2026-11-09')
    expect(describePlan(subscription({ status: 'canceled', access: 'readOnly', paidUntil: '2026-10-01T00:00:00Z' }), 'Studio', day, now).badge).toBe('Ended')
  })

  it('has nothing to pay for a comped organization', () => {
    expect(describePlan(subscription({ status: 'comped' }), 'Studio', day, now).action).toBeNull()
  })
})

describe('trial days', () => {
  it('counts part of a day as a day, and nothing once it has passed', () => {
    expect(daysLeft('2026-10-12T11:00:00Z', now)).toBe(3)
    expect(daysLeft('2026-10-09T13:00:00Z', now)).toBe(1)
    expect(daysLeft('2026-10-01T00:00:00Z', now)).toBe(0)
    expect(describeDaysLeft(1)).toBe('Last day')
  })

  it('shows an end at midnight as the day before', () => {
    expect(lastDayKey('2026-10-20T00:00:00Z', 'UTC')).toBe('2026-10-19')
  })

  it('says a plan chosen during the trial starts when the trial ends', () => {
    const plan = describePlan(
      subscription({ hasPlan: true, trialEndsAt: '2026-12-01T09:30:00Z', nextChargeAt: '2026-12-01T09:30:00Z' }),
      'Studio',
      day,
      now,
    )

    expect(plan).toMatchObject({ badge: 'Plan chosen', title: 'Starts after 2026-12-01', action: 'manage' })
  })

  it('says a canceled plan runs to the end of what was paid', () => {
    const plan = describePlan(subscription({ status: 'active', hasPlan: true, cancelsAtPeriodEnd: true, paidUntil: '2026-11-09T00:00:00Z' }), 'Studio', day, now)

    expect(plan).toMatchObject({ badge: 'Canceled', title: 'Works until 2026-11-09', action: 'manage' })
  })
})

describe('describePrice', () => {
  it('shows a price per person and period', () => {
    expect(describePrice({ interval: 'month', unitAmount: 5, currency: 'EUR' }, 'en-US')).toBe('€5 per person a month')
    expect(describePrice({ interval: 'year', unitAmount: 49.5, currency: 'EUR' }, 'en-US')).toBe('€49.50 per person a year')
  })
})

describe('trialLabel', () => {
  const now = new Date('2026-10-11T12:00:00Z')

  it('shows the days left, and marks the last two weeks', () => {
    expect(trialLabel('2027-01-06T12:00:00Z', now)).toEqual({ text: '87 days left', soon: false })
    expect(trialLabel('2026-10-25T12:00:00Z', now)).toEqual({ text: '14 days left', soon: true })
    expect(trialLabel('2026-10-12T00:00:00Z', now)).toEqual({ text: 'Last day', soon: true })
  })

  it('shows nothing without a trial', () => {
    expect(trialLabel(null, now)).toBeNull()
    expect(trialLabel(undefined, now)).toBeNull()
  })
})
