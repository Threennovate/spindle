import { expect, test } from '@playwright/test'
import { addMember, call, createUser, localTime, signIn } from './api.ts'
import { entryRow, openEntries } from './support.ts'

test('a timer started on one device shows on the others, and stops there too', async ({ browser }) => {
  const user = await createUser('Ana Anić')
  const laptop = await (await browser.newContext()).newPage()
  const desktop = await (await browser.newContext()).newPage()
  await signIn(laptop, user)
  await signIn(desktop, user)
  const laptopTimer = laptop.getByRole('region', { name: 'Timer' })
  const desktopTimer = desktop.getByRole('region', { name: 'Timer' })
  await expect(desktopTimer.getByRole('button', { name: 'Start timer' })).toBeVisible()

  await laptop.getByRole('combobox', { name: 'Description' }).fill('Writing the proposal')
  await laptopTimer.getByRole('button', { name: 'Start timer' }).click()

  await expect(desktopTimer.getByRole('button', { name: 'Stop timer' })).toBeVisible()
  await expect(desktopTimer.getByRole('textbox', { name: 'Description' })).toHaveValue('Writing the proposal')

  await desktopTimer.getByRole('button', { name: 'Stop timer' }).click()
  await expect(laptopTimer.getByRole('button', { name: 'Start timer' })).toBeVisible()
  await expect(laptop.getByRole('button', { name: 'Edit “Writing the proposal”' })).toBeVisible()
})

test('what teammates add shows up without reloading', async ({ page }) => {
  const admin = await createUser('Ana Anić')
  const member = await addMember(admin, 'Ivo Ivić')
  await signIn(page, admin)
  await page.getByRole('link', { name: 'Projects' }).click()
  await expect(page.getByText('No projects yet.', { exact: false })).toBeVisible()

  await call(member, 'POST', '/api/workspaces/{ws}/projects', { name: 'Mobile app', color: '#9E5BD9' })

  await expect(page.getByRole('link', { name: 'Mobile app' })).toBeVisible()
})

test('saving over a change made on another device asks which version to keep', async ({ page }) => {
  const user = await createUser('Ana Anić')
  const entry = await call<{ id: string; start: string; stop: string }>(user, 'POST', '/api/workspaces/{ws}/time-entries', {
    description: 'Client call',
    start: localTime(0, 9),
    stop: localTime(0, 10),
  })
  await signIn(page, user)
  await openEntries(page)

  await page.getByRole('button', { name: 'Edit “Client call”' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Description').fill('Client call with Acme')
  // Meanwhile, on the phone.
  await call(user, 'PUT', `/api/workspaces/{ws}/time-entries/${entry.id}`, { description: 'Renamed on the phone', start: entry.start, stop: entry.stop })
  await dialog.getByRole('button', { name: 'Save' }).click()

  const conflict = page.getByRole('alertdialog', { name: 'This entry changed on another device' })
  await expect(conflict).toContainText('“Renamed on the phone”')
  await conflict.getByRole('button', { name: 'Keep mine' }).click()
  await expect(entryRow(page, 'Client call with Acme')).toBeVisible()

  // Deleting what changed since the editor opened asks too, and keeping it leaves it.
  await page.getByRole('button', { name: 'Edit “Client call with Acme”' }).click()
  await call(user, 'PUT', `/api/workspaces/{ws}/time-entries/${entry.id}`, { description: 'Renamed again', start: entry.start, stop: entry.stop })
  await page.getByRole('dialog').getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('alertdialog', { name: 'Delete this time entry?' }).getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('alertdialog', { name: 'This entry changed on another device' }).getByRole('button', { name: 'Keep it' }).click()
  await expect(entryRow(page, 'Renamed again')).toBeVisible()
})
