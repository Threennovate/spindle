import { readFileSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'
import { addMember, call, createUser, signIn, updateSettings, type ApiUser } from './api.ts'

// A past week: Monday 3 to Sunday 9 August 2026, in the browser's time zone like the users'.
const WEEK = '/reports?period=custom&from=2026-08-03&to=2026-08-09'
const at = (day: number, hours: number, minutes = 0) => new Date(2026, 7, day, hours, minutes).toISOString()

/** Ana (admin) and Ivo track time on Website (Acme, 100 an hour) and Internal. */
async function arrange() {
  const admin = await createUser('Ana Anić')
  const member = await addMember(admin, 'Ivo Ivić')
  await updateSettings(admin, { defaultHourlyRate: 50 })
  const acme = await call<{ id: string }>(admin, 'POST', '/api/workspaces/{ws}/clients', { name: 'Acme' })
  const website = await call<{ id: string }>(admin, 'POST', '/api/workspaces/{ws}/projects', { name: 'Website', color: '#0B83D9', clientId: acme.id, isBillable: true, hourlyRate: 100 })
  const internal = await call<{ id: string }>(admin, 'POST', '/api/workspaces/{ws}/projects', { name: 'Internal', color: '#2DA608' })
  await entry(admin, { description: 'Homepage', projectId: website.id, isBillable: true, start: at(3, 9), stop: at(3, 11) })
  await entry(admin, { description: 'Planning', projectId: internal.id, isBillable: false, start: at(3, 13), stop: at(3, 14) })
  await entry(member, { description: 'Homepage', projectId: website.id, isBillable: true, start: at(4, 9), stop: at(4, 10, 30) })
  return { admin, member }
}

function entry(user: ApiUser, body: object) {
  return call(user, 'POST', '/api/workspaces/{ws}/time-entries', body)
}

/** The value on one of the report's headline cards, e.g. "Total". */
function figure(page: Page, label: string) {
  return page
    .locator('[data-slot=card]')
    .filter({ has: page.locator('[data-slot=card-description]', { hasText: new RegExp(`^${label}$`) }) })
    .locator('[data-slot=card-title]')
}

function breakdownRow(page: Page, name: string) {
  return page.getByRole('table', { name: /^Time by/ }).getByRole('row').filter({ hasText: name })
}

test('an admin reads, narrows, saves and exports a summary', async ({ page }) => {
  const { admin } = await arrange()
  await signIn(page, admin)
  await page.goto(WEEK)

  await expect(figure(page, 'Total')).toHaveText('4:30:00')
  await expect(figure(page, 'Billable')).toHaveText('3:30:00')
  await expect(figure(page, 'Amount')).toHaveText('€350.00')
  await expect(breakdownRow(page, 'Website')).toContainText('3:30:00')
  await expect(breakdownRow(page, 'Website')).toContainText('€350.00')
  await expect(breakdownRow(page, 'Internal')).toContainText('1:00:00')

  // Grouped by member instead.
  await page.getByRole('combobox', { name: 'Group by' }).click()
  await page.getByRole('option', { name: 'Member' }).click()
  await expect(breakdownRow(page, 'Ana Anić')).toContainText('3:00:00')
  await expect(breakdownRow(page, 'Ivo Ivić')).toContainText('1:30:00')
  await page.getByRole('button', { name: 'Show the breakdown of Ana Anić' }).click()
  await expect(breakdownRow(page, 'Planning')).toContainText('1:00:00')

  // Narrowed to one project, then back.
  await page.getByRole('button', { name: 'Filter', exact: true }).click()
  await page.getByRole('option', { name: 'Project' }).click()
  await page.getByRole('option', { name: /Website/ }).click()
  await page.keyboard.press('Escape')
  await expect(figure(page, 'Total')).toHaveText('3:30:00')
  await expect(page).toHaveURL(/projects=/)
  await expect(page.getByRole('button', { name: 'Project · Website' })).toBeVisible()
  await page.getByRole('button', { name: 'Clear the project filter' }).click()
  await expect(figure(page, 'Total')).toHaveText('4:30:00')

  // Saved as a view, then opened again from anywhere.
  await page.getByRole('button', { name: 'Save as a view…' }).click()
  await page.getByRole('dialog').getByLabel('Name').fill('August week by member')
  await page.getByRole('dialog').getByRole('button', { name: 'Save' }).click()
  await expect(page.getByRole('heading', { name: 'August week by member', level: 1 })).toBeVisible()
  const views = page.getByRole('navigation', { name: 'Report views' })

  // Reports opens the view opened last.
  await page.getByRole('link', { name: 'Entries' }).click()
  await page.getByRole('link', { name: 'Reports' }).click()
  await expect(page.getByRole('heading', { name: 'August week by member', level: 1 })).toBeVisible()
  await views.getByRole('button', { name: /^My week/ }).click()
  await expect(page.getByRole('button', { name: /^Period: This week/ })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Whose time: Me' })).toBeVisible()
  await views.getByRole('button', { name: 'August week by member' }).click()
  await expect(figure(page, 'Total')).toHaveText('4:30:00')
  await expect(breakdownRow(page, 'Ivo Ivić')).toBeVisible()

  // A change shows until it is saved to the view.
  await page.getByRole('combobox', { name: 'Group by' }).click()
  await page.getByRole('option', { name: 'Client' }).click()
  await expect(page.getByText('Edited', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText('Edited', { exact: true })).toHaveCount(0)

  // Exported as a spreadsheet file.
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Export' }).click()
  await page.getByRole('menuitem', { name: 'CSV' }).click()
  const file = await download
  expect(file.suggestedFilename()).toBe('spindle-summary-2026-08-03-to-2026-08-09.csv')
  const csv = readFileSync((await file.path())!, 'utf8').replace(/^﻿/, '').split('\r\n')
  expect(csv[0]).toBe('Client,Description,Duration,Hours,Amount (EUR)')
  expect(csv[1]).toBe('Acme,,3:30:00,3.50,350.00')
})

test('the detailed and weekly reports, and editing an entry from a report', async ({ page }) => {
  const { admin } = await arrange()
  await signIn(page, admin)
  await page.goto(`${WEEK}&tab=detailed`)

  const table = page.getByRole('table', { name: /^Time entries/ })
  await expect(table.getByRole('row')).toHaveCount(4)
  await table.getByRole('button', { name: 'Duration' }).click()
  await expect(table.getByRole('row').nth(1)).toContainText('2:00:00')
  await table.getByRole('button', { name: 'Duration' }).click()
  await expect(table.getByRole('row').nth(1)).toContainText('1:00:00')
  await expect(table.getByRole('columnheader', { name: 'Duration' })).toHaveAttribute('aria-sort', 'ascending')

  // Your own entries open for editing; the report follows.
  await table.getByRole('button', { name: 'Planning' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('heading', { name: 'Edit time entry' })).toBeVisible()
  await dialog.getByLabel('Description').fill('Team planning')
  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(table.getByRole('row').nth(1)).toContainText('Team planning')
  // Someone else's entry is only shown.
  await expect(table.getByRole('row').filter({ hasText: 'Ivo Ivić' }).getByRole('button')).toHaveCount(0)

  await page.getByRole('tab', { name: 'Weekly' }).click()
  const week = page.getByRole('table', { name: /^Time per day/ })
  const website = week.getByRole('row').filter({ hasText: 'Website' })
  await expect(website.getByRole('cell')).toHaveText(['2:00:00', '1:30:00', '–', '–', '–', '–', '–', '3:30:00', '€350.00'])
  await page.getByRole('button', { name: 'Next week' }).click()
  await expect(week.getByText('No time tracked this week.')).toBeVisible()
})

test('members report on their own time, without money', async ({ page }) => {
  const { admin, member } = await arrange()
  await updateSettings(admin, { defaultHourlyRate: 50 })
  await signIn(page, member)
  await page.goto(WEEK)

  await expect(figure(page, 'Total')).toHaveText('1:30:00')
  await expect(page.getByText('Amount', { exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: /^Whose time/ })).toHaveCount(0)
  await expect(breakdownRow(page, 'Website')).toContainText('1:30:00')

  // Their own views, without the team's.
  const views = page.getByRole('navigation', { name: 'Report views' })
  await expect(views.getByRole('button', { name: /^My month/ })).toBeVisible()
  await expect(views.getByRole('button', { name: 'Team this week' })).toHaveCount(0)
})
