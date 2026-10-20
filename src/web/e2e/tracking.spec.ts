import { expect, test } from '@playwright/test'
import { entryRow, registerNewUser } from './support.ts'

test('track time with a project and tag created on the spot', async ({ page }) => {
  await registerNewUser(page)

  await page.getByRole('combobox', { name: 'Description' }).fill('Writing specs')

  await page.getByRole('button', { name: 'Choose project' }).click()
  await page.getByPlaceholder('Search projects, tasks or clients').fill('Website')
  await page.getByRole('option', { name: 'Create project “Website”' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByLabel('Name')).toHaveValue('Website')
  await dialog.getByRole('button', { name: 'Create project' }).click()
  await expect(page.getByRole('button', { name: 'Project: Website' })).toBeVisible()

  await page.getByRole('button', { name: 'Choose tags' }).click()
  await page.getByPlaceholder('Search or create tags').fill('focus')
  await page.getByRole('option', { name: 'Create tag “focus”' }).click()
  await page.keyboard.press('Escape')
  await expect(page.getByRole('button', { name: 'Tags: 1 selected' })).toBeVisible()

  await page.getByRole('button', { name: 'Start timer' }).click()

  const row = entryRow(page, 'Writing specs')
  await expect(row).toContainText('now')
  await expect(row).toContainText('Website')
  await expect(row).toContainText('focus')
  await expect(page).toHaveTitle(/^0:00:\d\d · Writing specs · Entries · Spindle$/)

  await page.getByRole('button', { name: 'Stop timer' }).click()

  await expect(page.getByRole('button', { name: 'Start timer' })).toBeVisible()
  await expect(row).not.toContainText('now')
  await expect(page).toHaveTitle('Entries · Spindle')
})

test('correct when the running timer started', async ({ page }) => {
  await registerNewUser(page)
  await page.getByRole('combobox', { name: 'Description' }).fill('Forgot to start')
  await page.getByRole('button', { name: 'Start timer' }).click()
  const duration = page.getByRole('button', { name: 'Change start time' })

  // The field opens with its time selected, so typing replaces it.
  await duration.click()
  const started = page.getByLabel('Started at')
  await expect(started).toBeFocused()
  expect(await started.evaluate((input: HTMLInputElement) => input.selectionEnd! - input.selectionStart!)).toBe(5)

  // "-15" is 15 minutes ago; the day and duration it gives show before it is saved.
  await started.press('-')
  await started.press('1')
  await started.press('5')
  await expect(page.locator('#running-start-result')).toContainText(/0:1[45]:\d\d/)
  await started.press('Enter')
  await expect(duration).toHaveText(/^0:1[45]:\d\d$/)

  // Half a day or more is asked about once more before it is saved.
  await duration.click()
  await started.fill('-13h')
  await started.press('Enter')
  await expect(page.getByText('Save anyway to keep it.')).toBeVisible()
  await page.getByRole('button', { name: 'Save anyway' }).click()
  await expect(duration).toHaveText(/^13:0\d:\d\d$/)
})

test('add time manually, then correct it', async ({ page }) => {
  await registerNewUser(page)

  await page.getByRole('radio', { name: 'Manual mode' }).click()
  await page.getByRole('combobox', { name: 'Description' }).fill('Client call')
  await page.getByLabel('Start time').fill('9:00')
  await page.getByLabel('End time').fill('10:30')
  await page.getByRole('button', { name: 'Add', exact: true }).click()

  const row = entryRow(page, 'Client call')
  await expect(row).toContainText('09:00 – 10:30')
  await expect(row).toContainText('1:30:00')

  await page.getByRole('button', { name: 'Edit “Client call”' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Duration').fill('45m')
  await dialog.getByLabel('Duration').blur()
  await expect(dialog.getByLabel('End')).toHaveValue('09:45')
  await dialog.getByLabel('Description').fill('Client call with Acme')
  await dialog.getByRole('button', { name: 'Save' }).click()

  const edited = entryRow(page, 'Client call with Acme')
  await expect(edited).toContainText('09:00 – 09:45')
  await expect(edited).toContainText('0:45:00')
})

test('continue an entry, pick one from suggestions, and delete one', async ({ page }) => {
  await registerNewUser(page)
  await page.getByRole('radio', { name: 'Manual mode' }).click()
  // Times can be typed without a colon; leaving the field writes them out.
  for (const [description, start, end, endShown] of [
    ['Standup', '900', '915', '09:15'],
    ['Design review', '10', '11:00', '11:00'],
  ]) {
    await page.getByRole('combobox', { name: 'Description' }).fill(description)
    await page.getByLabel('Start time').fill(start)
    await page.getByLabel('End time').fill(end)
    await page.getByRole('button', { name: 'Add', exact: true }).click()
    await expect(entryRow(page, description)).toBeVisible()
    // The next entry starts where this one ended.
    await expect(page.getByLabel('Start time')).toHaveValue(endShown)
  }
  await page.getByRole('radio', { name: 'Timer mode' }).click()

  // Continue starts a new timer with the same details.
  await page.getByRole('button', { name: 'Continue “Design review”' }).click()
  await expect(page.getByRole('button', { name: 'Stop timer' })).toBeVisible()
  await expect(entryRow(page, 'Design review').filter({ hasText: 'now' })).toHaveCount(1)
  await page.getByRole('button', { name: 'Stop timer' }).click()

  // Typing offers matching previous entries; arrow down and Enter start one.
  const description = page.getByRole('combobox', { name: 'Description' })
  await description.fill('stand')
  await expect(page.getByRole('option')).toHaveText(['Standup'])
  await description.press('ArrowDown')
  await description.press('Enter')
  await expect(page.getByRole('textbox', { name: 'Description' })).toHaveValue('Standup')
  await page.getByRole('button', { name: 'Stop timer' }).click()
  await expect(entryRow(page, 'Standup')).toHaveCount(2)

  await page.getByRole('button', { name: 'More actions for “Design review”' }).first().click()
  await page.getByRole('menuitem', { name: 'Delete' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click()
  await expect(entryRow(page, 'Design review')).toHaveCount(1)
})
