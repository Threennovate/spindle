import { mkdirSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { expect, test, type Locator, type Page } from '@playwright/test'
import { call, createUser, type ApiUser } from '../e2e/api.ts'
import { latestEmailTo, PASSWORD } from '../e2e/support.ts'

/**
 * The pictures in the repository's README (.github/assets/readme-*), each in light and dark: Home, the day's review
 * with a split suggested from agent activity, and the calendar's week.
 *
 * One person, Emma, in a studio: Acme API and Acme Web for the client Acme, Northwind by the hour, and Internal. The
 * week up to yesterday has full days with a few split entries. Today has a morning entry on Acme API while her agents
 * also worked in the Acme Web repository (synced agent activity), so the review suggests a split, an hour of agent
 * work in Northwind that no timer covered, and a timer running since after lunch. Run it on a weekday after Monday,
 * so the week has days in it.
 */

const OUT = resolve(process.env.README_SCREENSHOTS ?? join(import.meta.dirname, '../../../artifacts/readme-screenshots'))

interface Id { id: string }

const at = (date: Date, hours: number, minutes = 0) => {
  const d = new Date(date)
  d.setHours(hours, minutes, 0, 0)
  return d
}

/** The days of this week (from Monday) before today. */
function earlierDays(today: Date): Date[] {
  const days: Date[] = []
  const back = (today.getDay() + 6) % 7
  for (let i = back; i > 0; i--) {
    const d = new Date(today)
    d.setDate(today.getDate() - i)
    days.push(d)
  }
  return days
}

async function arrange() {
  const emma = await createUser('Emma K.')
  await call(emma, 'PUT', '/api/workspaces/{ws}/settings', {
    name: 'Studio', currency: 'EUR', defaultHourlyRate: 90, onlyAdminsCreateProjects: false, onlyAdminsCreateTags: false,
    lockedBefore: null, requireDescription: false, requireProject: false, requireTask: false, requireTag: false,
  })
  const acme = await call<Id>(emma, 'POST', '/api/workspaces/{ws}/clients', { name: 'Acme' })
  const northwindClient = await call<Id>(emma, 'POST', '/api/workspaces/{ws}/clients', { name: 'Northwind' })
  const project = (name: string, color: string, body: object = {}) =>
    call<Id>(emma, 'POST', '/api/workspaces/{ws}/projects', { name, color, ...body })
  const api = await project('Acme API', '#2D6BE0', { clientId: acme.id, isBillable: true })
  const web = await project('Acme Web', '#D0477F', { clientId: acme.id, isBillable: true })
  const northwind = await project('Northwind', '#E36A00', { clientId: northwindClient.id, isBillable: true, hourlyRate: 95 })
  const internal = await project('Internal', '#13987F')

  const entry = (description: string, projectId: string, start: Date, stop: Date | null, shares?: { projectId: string; percent: number }[]) =>
    call(emma, 'POST', '/api/workspaces/{ws}/time-entries', {
      description, projectId, isBillable: projectId !== internal.id,
      start: start.toISOString(), stop: stop?.toISOString() ?? null,
      shares: shares?.map((s) => ({ ...s, isBillable: s.projectId !== internal.id })) ?? null,
    })

  const today = new Date()
  today.setHours(0, 0, 0, 0)

  // Full days earlier in the week: the same rhythm with different work, a split entry most days.
  const plans: [string, string, number, number, number, number, { projectId: string; percent: number }[]?][][] = [
    [
      ['Weekly planning', internal.id, 9, 0, 9, 45],
      ['Cursor pagination', api.id, 9, 50, 12, 15, [{ projectId: api.id, percent: 60 }, { projectId: web.id, percent: 40 }]],
      ['Inventory sync', northwind.id, 13, 0, 15, 30],
      ['Pricing page', web.id, 15, 40, 17, 30],
    ],
    [
      ['Rate limiter', api.id, 9, 0, 11, 40],
      ['Order export', northwind.id, 11, 50, 12, 50],
      ['Checkout form and order webhooks', web.id, 13, 40, 16, 40, [{ projectId: web.id, percent: 50 }, { projectId: api.id, percent: 30 }, { projectId: northwind.id, percent: 20 }]],
      ['Team meeting', internal.id, 16, 45, 17, 30],
    ],
    [
      ['Webhook retries', api.id, 9, 10, 12, 0, [{ projectId: api.id, percent: 70 }, { projectId: internal.id, percent: 30 }]],
      ['Weekly call', northwind.id, 12, 10, 12, 50],
      ['Navigation and footer', web.id, 13, 30, 16, 0],
      ['Reporting fixes', northwind.id, 16, 10, 17, 40],
    ],
    [
      ['API docs', api.id, 9, 0, 10, 50],
      ['Design review', web.id, 11, 0, 12, 20, [{ projectId: web.id, percent: 65 }, { projectId: api.id, percent: 35 }]],
      ['Inventory sync', northwind.id, 13, 20, 16, 30],
      ['Hiring call', internal.id, 16, 40, 17, 20],
    ],
  ]
  const earlier = earlierDays(today)
  for (const [i, day] of earlier.entries()) {
    for (const [description, projectId, h1, m1, h2, m2, shares] of plans[i % plans.length]) {
      await entry(description, projectId, at(day, h1, m1), at(day, h2, m2), shares)
    }
  }

  // Today: a morning on Acme API while agents also worked on Acme Web; agent work on Northwind with no timer; a
  // timer running since 13:10.
  await entry('Standup and planning', internal.id, at(today, 9, 0), at(today, 9, 40))
  await entry('Checkout and webhooks', api.id, at(today, 9, 52), at(today, 11, 12))
  await entry('Lunch call with Northwind', northwind.id, at(today, 12, 30), at(today, 13, 0))
  await entry('Order webhooks', api.id, at(today, 13, 10), null)

  await call(emma, 'PUT', '/api/me/agent-activity/settings', { sync: true, titles: true })
  const repos = {
    api: { identity: 'origin:github.com/acme/api', name: 'acme-api' },
    web: { identity: 'origin:github.com/acme/web', name: 'acme-web' },
    northwind: { identity: 'origin:github.com/northwind/inventory', name: 'northwind-inventory' },
  }
  for (const [repository, projectId] of [[repos.api, api.id], [repos.web, web.id], [repos.northwind, northwind.id]] as const) {
    await call(emma, 'PUT', '/api/workspaces/{ws}/agent-activity/links', { repository, projectId, noProject: false, branchRules: null })
  }
  // Prompts every few minutes; each repository's attention runs to the next prompt anywhere.
  const prompts = (from: Date, minutes: number[]) => minutes.map((m) => from.getTime() + m * 60_000)
  const session = (id: string, repository: { identity: string; name: string }, title: string, branch: string, touches: number[]) => ({
    source: 'claude', sessionId: id, parentSessionId: null, repository, title, branches: [branch], touches,
    runs: [{ start: touches[0], end: touches.at(-1)! + 4 * 60_000 }], parserVersion: 1,
  })
  const morning = at(today, 9, 52)
  await call(emma, 'PUT', '/api/me/agent-activity/sessions', {
    deviceId: crypto.randomUUID(),
    sessions: [
      session('readme-api', repos.api, 'Order webhooks with retries', 'feat/order-webhooks', prompts(morning, [1, 6, 12, 18, 41, 47, 53, 70, 76])),
      session('readme-web', repos.web, 'Checkout form validation', 'feat/checkout', prompts(morning, [24, 29, 34, 58, 63])),
      session('readme-northwind', repos.northwind, 'Stock levels import', 'fix/stock-import', prompts(at(today, 11, 25), [0, 9, 17, 26, 38, 47, 55])),
    ],
  })
  return { emma }
}

/** The whole window, or only `target`. */
async function shoot(page: Page, name: string, scheme: 'light' | 'dark', target?: Locator) {
  mkdirSync(OUT, { recursive: true })
  await (target ?? page).screenshot({ path: join(OUT, `readme-${name}-${scheme}.png`), animations: 'disabled' })
}

// e2e's signIn waits for the timer's description field, which a running timer doesn't show.
async function signIn(page: Page, user: ApiUser) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(user.email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page).toHaveURL(/\/$/)
}

/** Confirms the account's email and puts the first steps away, so neither shows in the pictures. */
async function settle(page: Page, user: ApiUser) {
  const email = await latestEmailTo(user.email, 'Confirm your email for Spindle')
  await page.goto(/http:\/\/localhost:5173\/confirm-email\?\S+/.exec(email.text)![0])
  await expect(page.getByText('Your email is confirmed')).toBeVisible()
  await call(user, 'PUT', '/api/me/first-steps', { hidden: true })
}

let emma: ApiUser

test.beforeAll(async () => {
  test.setTimeout(180_000)
  ;({ emma } = await arrange())
})

for (const scheme of ['light', 'dark'] as const) {
  test(`readme pictures, ${scheme}`, async ({ page }) => {
    // Tall enough for Home's week, Needs you and its projects.
    await page.setViewportSize({ width: 1280, height: 1080 })
    await page.emulateMedia({ colorScheme: scheme })
    await signIn(page, emma)
    if (scheme === 'light') await settle(page, emma)

    await page.goto('/')
    await expect(page.getByRole('heading', { name: 'Needs you' })).toBeVisible()
    await expect(page.getByText('to review').first()).toBeVisible()
    await shoot(page, 'home', scheme)

    await page.getByRole('button', { name: 'Review…' }).first().click()
    const review = page.getByRole('dialog')
    await expect(review).toBeVisible()
    await page.waitForTimeout(500)
    await shoot(page, 'review', scheme, review)
    await page.keyboard.press('Escape')

    await page.goto('/calendar')
    await expect(page.getByText('Checkout and webhooks').first()).toBeVisible()
    // The working day, from just before nine to the evening.
    await page.setViewportSize({ width: 1280, height: 860 })
    await page.locator('main .overflow-y-auto').first().evaluate((grid) => { grid.scrollTop = (grid.scrollHeight * 8.6) / 24 })
    await page.waitForTimeout(500)
    await shoot(page, 'calendar', scheme)
  })
}
