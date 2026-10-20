import { mkdirSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { expect, test, type Locator } from '@playwright/test'
import { addMember, call, createUser, signIn, type ApiUser } from '../e2e/api.ts'

/**
 * The pictures for "Rates, fixed prices and retainers" (spindletrack.com/docs/money): the Profitability page, a
 * project's agreements, and the agreement dialog of a retainer.
 *
 * The data is the website's profitability example, with everyone's labor cost at €40 an hour:
 * - Acme Web: a fixed price of €12,000 for 120 hours quoted, 96 hours tracked on its task.
 * - Acme API: a retainer of €4,000 a month with 40 hours included, from the 1st of two months ago: 38, 40 and 46
 *   hours in its three months, so only this month goes over.
 * - Northwind: by the hour at €95, 62 hours.
 * - Internal: not billed, 30 hours.
 * Time is spread over Emma (admin), Noah and Sam, on weekdays in two-hour blocks, so the totals come out as on the site.
 * Run it after the first week of a month, so this month has room for the retainer's 46 hours.
 */

const OUT = resolve(process.env.DOCS_SCREENSHOTS ?? join(import.meta.dirname, '../../../artifacts/docs-screenshots'))

interface Id { id: string }
interface Agreement extends Id { taskIds: string[] }
interface Member { userId: string; role: string }

const day = (date: Date) =>
  `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`

/** Weekdays from `from` to `to`, both included. */
function weekdays(from: Date, to: Date): Date[] {
  const days: Date[] = []
  for (const d = new Date(from); d <= to; d.setDate(d.getDate() + 1)) {
    if (d.getDay() !== 0 && d.getDay() !== 6) days.push(new Date(d))
  }
  return days
}

/** A small deterministic generator, so each run spreads the time the same way. */
function random(seed: number) {
  return () => {
    seed = (seed * 1664525 + 1013904223) % 2 ** 32
    return seed / 2 ** 32
  }
}

interface Slot { person: number; start: Date; end: Date }

/** Two-hour blocks on each person's weekdays, shuffled. */
function slots(days: Date[], people: number, next: () => number): Slot[] {
  const all: Slot[] = []
  for (const d of days) {
    for (let person = 0; person < people; person++) {
      for (const hour of [9, 11, 14, 16]) {
        const start = new Date(d)
        start.setHours(hour, 0, 0, 0)
        all.push({ person, start, end: new Date(start.getTime() + 2 * 3600_000) })
      }
    }
  }
  for (let i = all.length - 1; i > 0; i--) {
    const j = Math.floor(next() * (i + 1))
    ;[all[i], all[j]] = [all[j], all[i]]
  }
  return all
}

async function arrange() {
  const emma = await createUser('Emma K.')
  const noah = await addMember(emma, 'Noah P.')
  const sam = await addMember(emma, 'Sam R.')
  const people = [emma, noah, sam]

  await call(emma, 'PUT', '/api/workspaces/{ws}/settings', {
    name: 'Studio', currency: 'EUR', defaultHourlyRate: null, onlyAdminsCreateProjects: false, onlyAdminsCreateTags: false,
    lockedBefore: null, requireDescription: false, requireProject: false, requireTask: false, requireTag: false,
  })
  for (const member of await call<Member[]>(emma, 'GET', '/api/workspaces/{ws}/members')) {
    await call(emma, 'PUT', `/api/workspaces/{ws}/members/${member.userId}`, { role: member.role, laborCost: 40 })
  }

  const acme = await call<Id>(emma, 'POST', '/api/workspaces/{ws}/clients', { name: 'Acme' })
  const northwindClient = await call<Id>(emma, 'POST', '/api/workspaces/{ws}/clients', { name: 'Northwind' })
  const project = (name: string, color: string, body: object = {}) =>
    call<Id>(emma, 'POST', '/api/workspaces/{ws}/projects', { name, color, ...body })
  const web = await project('Acme Web', '#D0477F', { clientId: acme.id, isBillable: true })
  const api = await project('Acme API', '#2D6BE0', { clientId: acme.id, isBillable: true })
  const northwind = await project('Northwind', '#E36A00', { clientId: northwindClient.id, isBillable: true, hourlyRate: 95 })
  const internal = await project('Internal', '#13987F')

  const today = new Date()
  today.setHours(0, 0, 0, 0)
  const monthStart = new Date(today.getFullYear(), today.getMonth(), 1)
  const retainerStart = new Date(today.getFullYear(), today.getMonth() - 2, 1)
  const yesterday = new Date(today)
  yesterday.setDate(today.getDate() - 1)
  const lastMonthEnd = new Date(monthStart)
  lastMonthEnd.setDate(0)
  const secondMonth = new Date(today.getFullYear(), today.getMonth() - 1, 1)
  const firstMonthEnd = new Date(secondMonth)
  firstMonthEnd.setDate(0)

  const build = await call<Agreement>(emma, 'POST', `/api/workspaces/{ws}/projects/${web.id}/agreements`, {
    name: 'Website build', kind: 'fixedPrice', amount: 12000, hours: 120, startsOn: day(retainerStart),
  })
  const maintenance = await call<Agreement>(emma, 'POST', `/api/workspaces/{ws}/projects/${api.id}/agreements`, {
    name: 'Maintenance', kind: 'retainer', amount: 4000, hours: 40, period: 'month', startsOn: day(retainerStart),
  })

  const pools = {
    all: slots(weekdays(retainerStart, lastMonthEnd), people.length, random(14)),
    first: slots(weekdays(retainerStart, firstMonthEnd), people.length, random(15)),
    second: slots(weekdays(secondMonth, lastMonthEnd), people.length, random(16)),
    current: slots(weekdays(monthStart, yesterday), people.length, random(17)),
  }
  const apiNotes = ['Cursor pagination', 'Rate limiter', 'Retry-After handling in the rate limiter', 'Webhook retries', 'Dependency updates']
  const work: { pool: keyof typeof pools; project: string; task: string | null; billable: boolean; hours: number; notes: string[] }[] = [
    { pool: 'all', project: web.id, task: build.taskIds[0], billable: true, hours: 96,
      notes: ['Pricing page', 'Pricing toggle and mobile layout fixes', 'Checkout form', 'Navigation and footer', 'Design review'] },
    { pool: 'first', project: api.id, task: maintenance.taskIds[0], billable: true, hours: 38, notes: apiNotes },
    { pool: 'second', project: api.id, task: maintenance.taskIds[0], billable: true, hours: 40, notes: apiNotes },
    { pool: 'current', project: api.id, task: maintenance.taskIds[0], billable: true, hours: 46,
      notes: ['Cursor pagination tests', 'Rate limiter', 'Error budget alerts', 'API docs'] },
    { pool: 'all', project: northwind.id, task: null, billable: true, hours: 62,
      notes: ['Inventory sync', 'Order export', 'Weekly call', 'Reporting fixes'] },
    { pool: 'all', project: internal.id, task: null, billable: false, hours: 30,
      notes: ["Next week's plan from the open issues", 'Hiring call', 'Team meeting'] },
  ]

  // A block taken from one pool is taken from the others too, so nobody has two entries at once.
  const taken = new Set<string>()
  const key = (slot: Slot) => `${slot.person}@${slot.start.getTime()}`
  for (const w of work) {
    const pool = pools[w.pool]
    for (let left = w.hours, n = 0; left > 0; left -= 2, n++) {
      let slot = pool.shift()
      while (slot && taken.has(key(slot))) slot = pool.shift()
      if (!slot) throw new Error('Not enough weekdays for the hours; run it after the first week of a month.')
      taken.add(key(slot))
      const stop = left >= 2 ? slot.end : new Date(slot.start.getTime() + left * 3600_000)
      await call(people[slot.person], 'POST', '/api/workspaces/{ws}/time-entries', {
        description: w.notes[n % w.notes.length], projectId: w.project, taskId: w.task, isBillable: w.billable,
        start: slot.start.toISOString(), stop: stop.toISOString(),
      })
    }
  }
  return { emma, web, api }
}

async function shoot(target: Locator, name: string) {
  mkdirSync(OUT, { recursive: true })
  await target.screenshot({ path: join(OUT, `${name}.png`), animations: 'disabled' })
}

let data: { emma: ApiUser; web: Id; api: Id }

test.beforeAll(async () => {
  test.setTimeout(180_000)
  data = await arrange()
})

test('money: profitability, agreements and the retainer dialog', async ({ page }) => {
  await signIn(page, data.emma)

  await page.goto('/profitability')
  const apiRow = page.getByRole('row').filter({ hasText: 'Acme API' }).first()
  await expect(apiRow).toBeVisible()
  await page.getByRole('button', { name: 'Show the agreements of Acme API' }).click()
  await expect(page.getByText('Maintenance').first()).toBeVisible()
  // The page down to its note, without the empty space below it.
  const main = (await page.locator('main').boundingBox())!
  const note = (await page.locator('main').getByText('Revenue counts each fixed price').boundingBox())!
  mkdirSync(OUT, { recursive: true })
  await page.screenshot({
    path: join(OUT, 'money-profitability.png'), animations: 'disabled',
    clip: { x: main.x, y: main.y, width: main.width, height: note.y + note.height + 32 - main.y },
  })

  await page.goto(`/projects/${data.api.id}`)
  const agreements = page.locator('[data-slot=card]').filter({ has: page.getByText('Agreements', { exact: true }) })
  await expect(agreements).toContainText('Maintenance')
  await shoot(agreements, 'money-agreements')

  await agreements.getByRole('button', { name: 'Actions for Maintenance' }).click()
  await page.getByRole('menuitem', { name: 'Edit' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Edit agreement')
  await shoot(dialog, 'money-retainer-dialog')
})
