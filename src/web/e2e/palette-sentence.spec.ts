import { expect, test, type Page } from '@playwright/test'
import { addMember, call, createUser, localDay, signIn, updateSettings } from './api.ts'
import { entryRow } from './support.ts'

const palette = (page: Page) => page.getByRole('dialog', { name: 'Command palette' })
const search = (page: Page) => palette(page).getByRole('combobox')
const firstRow = (page: Page) => palette(page).getByRole('option').first()

async function openPalette(page: Page) {
  await page.keyboard.press('ControlOrMeta+k')
  await expect(search(page)).toBeFocused()
}

/** A clock time `minutes` ago, as typed (`9:05`) and as shown (`09:05`). */
function minutesAgo(minutes: number) {
  const date = new Date(Date.now() - minutes * 60_000)
  const mm = String(date.getMinutes()).padStart(2, '0')
  return { typed: `${date.getHours()}:${mm}`, shown: `${String(date.getHours()).padStart(2, '0')}:${mm}` }
}

interface Entry {
  description: string
  start: string
  stop: string | null
  tagIds: string[]
}

test('say what you work on in one line: a project, a tag and when it started, then undo', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await call(user, 'POST', '/api/workspaces/{ws}/projects', { name: 'Acme website', color: '#6366f1' })
  await call(user, 'POST', '/api/workspaces/{ws}/tags', { name: 'review' })
  const since = minutesAgo(30)
  await signIn(page, user)

  await openPalette(page)
  await search(page).pressSequentially('Review PR @acme')
  await expect(firstRow(page)).toContainText('Acme website')
  await page.keyboard.press('Tab')
  await expect(palette(page).getByRole('list', { name: 'Picked for the entry' })).toContainText('Acme website')
  await search(page).pressSequentially('#rev')
  await expect(firstRow(page)).toContainText('review')
  await page.keyboard.press('Tab')
  await expect(search(page)).toHaveValue('Review PR ')
  await search(page).pressSequentially(`since ${since.typed}`)

  // What Enter will save, before it is saved.
  await expect(firstRow(page)).toContainText('Start “Review PR”')
  await expect(firstRow(page)).toContainText('Acme website')
  await expect(firstRow(page)).toContainText('#review')
  await expect(firstRow(page)).toContainText(`since ${since.shown} (0:30)`)
  await page.keyboard.press('Enter')

  const row = entryRow(page, 'Review PR')
  await expect(row).toContainText('Acme website')
  await expect(row).toContainText('review')
  await expect(row).toContainText(`${since.shown} – now`)

  await page.locator('[data-sonner-toast]').filter({ hasText: 'Timer started' }).getByRole('button', { name: 'Undo' }).click()
  await expect(row).toBeHidden()
  await expect(page.getByRole('button', { name: 'Start timer' })).toBeVisible()
})

test('a # typed without picking a tag stays text, and Backspace turns a chip back into text', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await call(user, 'POST', '/api/workspaces/{ws}/projects', { name: 'Acme website', color: '#6366f1' })
  await signIn(page, user)

  await openPalette(page)
  await search(page).pressSequentially('@acme')
  await page.keyboard.press('Enter')
  await expect(search(page)).toHaveValue('')
  await page.keyboard.press('Backspace')
  await expect(search(page)).toHaveValue('@Acme website')
  await expect(firstRow(page)).toContainText('Acme website')
  await search(page).fill('')

  await search(page).pressSequentially('Fix #482')
  await expect(firstRow(page)).toContainText('Start “Fix #482”')
  await page.keyboard.press('Enter')
  await expect(entryRow(page, 'Fix #482')).toContainText('now')
  expect(await call<unknown[]>(user, 'GET', '/api/workspaces/{ws}/tags')).toHaveLength(0)
})

test('add a finished entry for another day in one line', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await signIn(page, user)

  await openPalette(page)
  await search(page).pressSequentially('Standup yesterday 9:00-9:15')
  await expect(firstRow(page)).toContainText('Add “Standup”')
  await expect(firstRow(page)).toContainText('Yesterday 09:00–09:15 (0:15)')
  await page.keyboard.press('Enter')
  await expect(page.locator('[data-sonner-toast]').filter({ hasText: 'Added “Standup”' })).toBeVisible()

  const day = localDay(-1)
  const entries = await call<Entry[]>(user, 'GET', `/api/workspaces/{ws}/time-entries?from=${day}T00:00:00Z&to=${localDay(1)}T00:00:00Z`)
  const standup = entries.find((entry) => entry.description === 'Standup')!
  expect(standup.stop).not.toBeNull()
  expect((Date.parse(standup.stop!) - Date.parse(standup.start)) / 60_000).toBe(15)
})

test('a locked day and a required project are handled before anything is saved', async ({ page }) => {
  const admin = await createUser('Ana Anić')
  await call(admin, 'POST', '/api/workspaces/{ws}/projects', { name: 'Acme website', color: '#6366f1' })
  await updateSettings(admin, { lockedBefore: localDay(0), requireProject: true })
  const member = await addMember(admin, 'Ivo Ivić')
  await signIn(page, member)

  await openPalette(page)
  await search(page).pressSequentially('Standup yesterday 9:00-9:15')
  await expect(firstRow(page)).toContainText('is locked')
  await page.keyboard.press('Enter')
  await expect(palette(page)).toBeVisible()

  const from = minutesAgo(45)
  const to = minutesAgo(30)
  await search(page).fill('')
  await search(page).pressSequentially(`Standup ${from.typed}-${to.typed}`)
  await expect(firstRow(page)).toContainText('Needs a project')
  await page.keyboard.press('Enter')
  await search(page).pressSequentially('acme')
  await page.keyboard.press('Enter')
  // Back on the line, which now names the project; only Enter saves it.
  await expect(palette(page).getByRole('list', { name: 'Picked for the entry' })).toContainText('Acme website')
  await expect(firstRow(page)).toContainText('Add “Standup”')
  await expect(firstRow(page)).not.toContainText('Needs a project')
  await page.keyboard.press('Enter')
  await expect(page.locator('[data-sonner-toast]').filter({ hasText: 'Added “Standup”' })).toBeVisible()
})

test('move the running timer’s start with +15, and jump to a day', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await signIn(page, user)
  await call(user, 'POST', '/api/workspaces/{ws}/time-entries', { description: 'Deep work', start: new Date(Date.now() - 10 * 60_000).toISOString() })
  await page.reload()
  await expect(page.getByRole('button', { name: 'Stop timer' })).toBeVisible()

  await openPalette(page)
  await search(page).fill('+15')
  await expect(firstRow(page)).toContainText('Started 15 min earlier')
  await expect(firstRow(page)).toContainText(/0:10:\d\d → 0:25:\d\d/)
  await page.keyboard.press('Enter')
  await expect(page.locator('[data-sonner-toast]').filter({ hasText: 'Started at' })).toBeVisible()
  await expect(entryRow(page, 'Deep work')).toContainText(/0:25:\d\d/)

  await openPalette(page)
  await search(page).fill('yesterday')
  await expect(firstRow(page)).toContainText('Yesterday in the calendar')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(`/calendar\\?date=${localDay(-1)}&view=day`))
})
