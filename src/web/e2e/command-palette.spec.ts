import { expect, test, type Page } from '@playwright/test'
import { call, createUser, signIn } from './api.ts'
import { entryRow } from './support.ts'

const palette = (page: Page) => page.getByRole('dialog', { name: 'Command palette' })
const search = (page: Page) => palette(page).getByRole('combobox')

async function openPalette(page: Page) {
  await page.keyboard.press('ControlOrMeta+k')
  await expect(search(page)).toBeFocused()
}

test('start a timer by typing what you work on, then stop it and undo the stop', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await call(user, 'POST', '/api/workspaces/{ws}/projects', { name: 'Booking engine', color: '#10b981' })
  await signIn(page, user)

  await openPalette(page)
  await search(page).fill('Payment retries')
  // Nothing fits the words, so starting a timer with them comes first; ⌘ Enter picks a project for it.
  await expect(palette(page).getByRole('option').first()).toHaveText(/Start “Payment retries”/)
  await page.keyboard.press('ControlOrMeta+Enter')
  await search(page).fill('book')
  await palette(page).getByRole('option', { name: /Booking engine/ }).click()

  const row = entryRow(page, 'Payment retries')
  await expect(row).toContainText('now')
  await expect(row).toContainText('Booking engine')

  await openPalette(page)
  await expect(palette(page).getByText('Payment retries')).toBeVisible()
  await palette(page).getByRole('option', { name: /Stop timer/ }).click()
  await expect(page.getByRole('button', { name: 'Start timer' })).toBeVisible()

  await page.locator('[data-sonner-toast]').filter({ hasText: 'Stopped' }).getByRole('button', { name: 'Undo' }).click()
  await expect(page.getByRole('button', { name: 'Stop timer' })).toBeVisible()
  await expect(row).toContainText('now')
})

test('change when the running timer started, and step back with Escape', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await signIn(page, user)
  await call(user, 'POST', '/api/workspaces/{ws}/time-entries', { description: 'Forgot to start', start: new Date().toISOString() })
  await page.reload()
  await expect(page.getByRole('button', { name: 'Stop timer' })).toBeVisible()

  await openPalette(page)
  await search(page).fill('start time')
  await page.keyboard.press('Enter')
  await expect(palette(page).getByText('Start time', { exact: true })).toBeVisible()
  await search(page).fill('-20')
  await expect(palette(page).getByRole('option', { name: /Started at/ })).toBeVisible()
  await page.keyboard.press('Enter')

  await expect(palette(page)).toBeHidden()
  await expect(page.getByRole('button', { name: 'Change start time' })).toHaveText(/^0:2\d:\d\d$/)

  // Escape goes back a page, and only then closes the palette.
  await openPalette(page)
  await search(page).fill('tags')
  await page.keyboard.press('Enter')
  await expect(search(page)).toHaveAttribute('placeholder', 'Search or create tags')
  await page.keyboard.press('Escape')
  await expect(search(page)).toHaveValue('tags')
  await page.keyboard.press('Escape')
  await expect(palette(page)).toBeHidden()
})

test('act on a project: start on Enter, open with ⌘ Enter, more with Tab', async ({ page }) => {
  const user = await createUser('Ana Anić')
  const project = await call<{ id: string }>(user, 'POST', '/api/workspaces/{ws}/projects', { name: 'Website redesign', color: '#6366f1' })
  await signIn(page, user)

  await page.getByRole('button', { name: 'Search or run a command…' }).click()
  await search(page).fill('web')
  await expect(palette(page).getByRole('option').first()).toHaveText(/Website redesign/)
  await page.keyboard.press('Tab')
  await expect(palette(page).getByRole('option', { name: /Report this month/ })).toBeVisible()
  await page.keyboard.press('Backspace')
  await page.keyboard.press('ControlOrMeta+Enter')
  await expect(page).toHaveURL(new RegExp(`/projects/${project.id}$`))
})

test('switch the appearance', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await signIn(page, user)

  await openPalette(page)
  await search(page).fill('dark')
  await palette(page).getByRole('option', { name: /Dark appearance/ }).click()
  await expect(page.locator('html')).toHaveClass(/\bdark\b/)

  await openPalette(page)
  await search(page).fill('light')
  await palette(page).getByRole('option', { name: /Light appearance/ }).click()
  await expect(page.locator('html')).not.toHaveClass(/\bdark\b/)
})
