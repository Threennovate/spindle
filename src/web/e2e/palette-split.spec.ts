import { expect, test, type Page } from '@playwright/test'
import { call, createUser, localDay, signIn } from './api.ts'

const palette = (page: Page) => page.getByRole('dialog', { name: 'Command palette' })
const search = (page: Page) => palette(page).getByRole('combobox')
const part = (page: Page, name: string) => palette(page).getByRole('option').filter({ hasText: name })

async function openPalette(page: Page) {
  await page.keyboard.press('ControlOrMeta+k')
  await expect(search(page)).toBeFocused()
}

interface Entry {
  projectId: string | null
  shares: { projectId: string | null; percent: number }[]
}

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString()

/** Starts a timer from elsewhere, e.g. another device, and shows it. */
async function startElsewhere(page: Page, user: Awaited<ReturnType<typeof createUser>>, entry: object) {
  await call(user, 'POST', '/api/workspaces/{ws}/time-entries', entry)
  await page.reload()
  await expect(page.getByRole('button', { name: 'Stop timer' })).toBeVisible()
}

/** Picks a project on the palette's project page. */
async function pickProject(page: Page, name: string) {
  await search(page).fill(name.toLowerCase())
  await expect(palette(page).getByRole('option').first()).toContainText(name)
  await page.keyboard.press('Enter')
}

test('split the running timer between three projects with the keyboard alone, then undo', async ({ page }) => {
  const user = await createUser('Ana Anić')
  const project = (name: string, color: string) => call<{ id: string }>(user, 'POST', '/api/workspaces/{ws}/projects', { name, color })
  const atlas = await project('Atlas', '#6366f1')
  const beacon = await project('Beacon', '#10b981')
  const cobalt = await project('Cobalt', '#f59e0b')
  await signIn(page, user)
  await startElsewhere(page, user, { description: 'Agents', projectId: atlas.id, start: minutesAgo(60) })

  await openPalette(page)
  await search(page).fill('split')
  await expect(palette(page).getByRole('option').first()).toContainText('Split with…')
  await page.keyboard.press('Enter')
  await pickProject(page, 'Beacon')

  // Half and half to start with; → makes the selected part bigger by 5.
  await expect(part(page, 'Atlas')).toContainText('50%')
  await expect(part(page, 'Beacon')).toContainText('50%')
  await page.keyboard.press('ArrowRight')
  await page.keyboard.press('ArrowRight')
  await expect(part(page, 'Atlas')).toContainText('60%')
  await expect(part(page, 'Beacon')).toContainText('40%')

  // A number is the selected part's share.
  await page.keyboard.type('70')
  await expect(part(page, 'Atlas')).toContainText('70%')
  await expect(part(page, 'Beacon')).toContainText('30%')
  await page.keyboard.press('ArrowDown')
  await page.keyboard.press('Shift+ArrowLeft')
  await expect(part(page, 'Atlas')).toContainText('71%')
  await expect(part(page, 'Beacon')).toContainText('29%')

  await page.keyboard.press('+')
  await pickProject(page, 'Cobalt')
  await expect(part(page, 'Cobalt')).toBeVisible()
  await expect(palette(page).getByRole('option', { name: /More parts in the full editor/ })).toBeVisible()
  await page.keyboard.press('Enter')

  await expect(page.locator('[data-sonner-toast]').filter({ hasText: 'Split: Atlas' })).toBeVisible()
  let current = await call<Entry>(user, 'GET', '/api/me/time-entries/current')
  expect(current.shares.map((share) => share.projectId)).toEqual([atlas.id, beacon.id, cobalt.id])
  expect(current.shares.reduce((sum, share) => sum + share.percent, 0)).toBe(100)

  await page.locator('[data-sonner-toast]').filter({ hasText: 'Split: Atlas' }).getByRole('button', { name: 'Undo' }).click()
  await expect(async () => {
    current = await call<Entry>(user, 'GET', '/api/me/time-entries/current')
    expect(current.shares).toHaveLength(0)
    expect(current.projectId).toBe(atlas.id)
  }).toPass()
})

test('split the entry that just ended, and reports count each part', async ({ page }) => {
  const user = await createUser('Ana Anić')
  const atlas = await call<{ id: string }>(user, 'POST', '/api/workspaces/{ws}/projects', { name: 'Atlas', color: '#6366f1' })
  const beacon = await call<{ id: string }>(user, 'POST', '/api/workspaces/{ws}/projects', { name: 'Beacon', color: '#10b981' })
  await call(user, 'POST', '/api/workspaces/{ws}/time-entries', { description: 'Research', projectId: atlas.id, start: minutesAgo(90), stop: minutesAgo(30) })
  await signIn(page, user)

  await openPalette(page)
  await search(page).fill('split research')
  await expect(palette(page).getByRole('option').first()).toContainText('Split “Research” (ended')
  await page.keyboard.press('Enter')
  await pickProject(page, 'Beacon')
  await page.keyboard.type('25')
  await expect(part(page, 'Atlas')).toContainText('0:15')
  await expect(part(page, 'Beacon')).toContainText('0:45')
  await page.keyboard.press('Enter')
  await expect(page.locator('[data-sonner-toast]').filter({ hasText: 'Split: Atlas 25% · Beacon 75%' })).toBeVisible()

  const report = await call<{ groups: { id: string; seconds: number }[] }>(user, 'POST', '/api/workspaces/{ws}/reports/summary', {
    from: localDay(0),
    to: localDay(0),
    groupBy: 'project',
  })
  const seconds = Object.fromEntries(report.groups.map((group) => [group.id, group.seconds]))
  expect(seconds[atlas.id]).toBe(15 * 60)
  expect(seconds[beacon.id]).toBe(45 * 60)
})

test('split the running timer in one line', async ({ page }) => {
  const user = await createUser('Ana Anić')
  const atlas = await call<{ id: string }>(user, 'POST', '/api/workspaces/{ws}/projects', { name: 'Atlas', color: '#6366f1' })
  const beacon = await call<{ id: string }>(user, 'POST', '/api/workspaces/{ws}/projects', { name: 'Beacon', color: '#10b981' })
  await signIn(page, user)
  await startElsewhere(page, user, { description: 'Agents', projectId: atlas.id, start: minutesAgo(20) })

  await openPalette(page)
  await search(page).pressSequentially('split @bea')
  await page.keyboard.press('Tab')
  await search(page).pressSequentially('40')
  const preview = palette(page).getByRole('option').first()
  await expect(preview).toContainText('Split with Beacon')
  await expect(preview).toContainText('Atlas 60%')
  await expect(preview).toContainText('Beacon 40%')
  await page.keyboard.press('Enter')

  await expect(async () => {
    const current = await call<Entry>(user, 'GET', '/api/me/time-entries/current')
    expect(current.shares.map((share) => [share.projectId, share.percent])).toEqual([
      [atlas.id, 60],
      [beacon.id, 40],
    ])
  }).toPass()
})
