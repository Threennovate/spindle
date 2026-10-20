import { expect, test } from '@playwright/test'
import { addMember, createUser, signIn } from './api.ts'

test('an admin imports a Toggl Track export, assigning someone who is not a member', async ({ page }) => {
  const admin = await createUser('Ana Anić')
  const member = await addMember(admin, 'Ivo Ivić')
  const csv = [
    'User,Email,Client,Project,Task,Description,Billable,Start date,Start time,End date,End time,Duration,Tags,Amount (EUR)',
    `Ana Anić,${admin.email},Acme,Website,,Homepage,Yes,2026-08-03,09:00:00,2026-08-03,11:00:00,02:00:00,design,`,
    `Ivo Ivić,${member.email},Acme,Website,,Review,Yes,2026-08-04,13:00:00,2026-08-04,14:30:00,01:30:00,,`,
    'Marko Marić,marko@elsewhere.example,,Internal,,Planning,No,2026-08-05,09:00:00,2026-08-05,10:00:00,01:00:00,,',
    'Marko Marić,marko@elsewhere.example,,Internal,,Broken,No,someday,09:00:00,,10:00:00,01:00:00,,',
  ].join('\n')
  await signIn(page, admin)

  await page.getByRole('link', { name: 'Settings' }).click()
  await page.getByRole('link', { name: 'Import from a CSV file' }).click()
  await expect(page.getByRole('heading', { name: 'Import time' })).toBeVisible()
  await page.getByLabel('CSV file to import').setInputFiles({ name: 'toggl.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) })

  await expect(page.getByText('Toggl Track file · 4 entries')).toBeVisible()
  const people = page.getByRole('list', { name: 'People in the file' })
  await expect(people.getByRole('listitem')).toHaveCount(3)
  await expect(page.getByRole('combobox', { name: "Whose time Marko Marić's entries become" })).toHaveText('Leave out')
  await expect(page.getByRole('button', { name: 'Import 2 entries' })).toBeVisible()
  await expect(page.getByRole('list', { name: 'Problems' })).toContainText('Line 5')

  await page.getByRole('combobox', { name: "Whose time Marko Marić's entries become" }).click()
  await page.getByRole('option', { name: 'Ana Anić' }).click()
  await page.getByRole('button', { name: 'Import 3 entries' }).click()

  await expect(page.getByText('Imported 3 entries')).toBeVisible()
  await expect(page.getByText('Created 2 projects, 1 client, 1 tag. 1 left out.')).toBeVisible()
  await page.getByRole('link', { name: 'See it in reports' }).click()
  await expect(page).toHaveURL(/\/reports\?.*from=2026-08-03/)
  await expect(page.getByRole('table', { name: /^Time by/ }).getByRole('row').filter({ hasText: 'Website' })).toContainText('3:30:00')
})
