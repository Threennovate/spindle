import { expect, test, type Page } from '@playwright/test'
import { entryRow, registerNewUser } from './support.ts'

const PX_PER_MINUTE = 48 / 60

/** Screen coordinates of a time of day in the single-day calendar grid. */
async function slot(page: Page, hours: number, minutes = 0) {
  const box = (await page.getByRole('grid', { name: 'Time entries by time of day' }).boundingBox())!
  return { x: box.x + 56 + (box.width - 56) / 2, y: box.y + (hours * 60 + minutes) * PX_PER_MINUTE }
}

async function drag(page: Page, from: { x: number; y: number }, to: { x: number; y: number }) {
  await page.mouse.move(from.x, from.y)
  await page.mouse.down()
  await page.mouse.move(to.x, to.y, { steps: 8 })
  await page.mouse.up()
}

test('draw, move and resize an entry on the calendar', async ({ page }) => {
  await registerNewUser(page)
  await page.getByRole('link', { name: 'Calendar' }).click()
  await page.getByRole('radio', { name: 'Day' }).click()
  await expect(page.getByRole('grid', { name: 'Time entries by time of day' })).toHaveAttribute('aria-busy', 'false')

  // Pressing anywhere in the 9:00–9:15 slot starts at 9:00.
  await drag(page, await slot(page, 9, 5), await slot(page, 10, 30))
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('heading', { name: 'New time entry' })).toBeVisible()
  await expect(dialog.getByLabel('Start')).toHaveValue('09:00')
  await expect(dialog.getByLabel('End')).toHaveValue('10:30')
  await dialog.getByLabel('Description').fill('Planning')
  await dialog.getByRole('button', { name: 'Add entry' }).click()

  const block = page.getByRole('button', { name: /^Planning, no project, 09:00 – 10:30$/ })
  await expect(block).toBeVisible()

  // Drag the block an hour later; it keeps its length.
  const middle = await slot(page, 9, 45)
  await drag(page, middle, { x: middle.x, y: middle.y + 60 * PX_PER_MINUTE })
  await expect(page.getByRole('button', { name: /^Planning, no project, 10:00 – 11:30$/ })).toBeVisible()

  // Drag its bottom edge to 12:00.
  const end = await slot(page, 11, 30)
  await drag(page, { x: end.x, y: end.y - 2 }, await slot(page, 12))
  const resized = page.getByRole('button', { name: /^Planning, no project, 10:00 – 12:00$/ })
  await expect(resized).toBeVisible()

  // A click without dragging opens the entry.
  await resized.click()
  await expect(page.getByRole('dialog').getByRole('heading', { name: 'Edit time entry' })).toBeVisible()
})

test('fill in a week on the timesheet', async ({ page }) => {
  await registerNewUser(page)
  await page.getByRole('link', { name: 'Timesheet' }).click()
  await expect(page.getByText('Nothing tracked this week')).toBeVisible()

  await page.getByRole('button', { name: 'Add row' }).click()
  await page.getByPlaceholder('Search projects, tasks or clients').fill('Research')
  await page.getByRole('option', { name: 'Create project “Research”' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Create project' }).click()

  const today = new Intl.DateTimeFormat('en-GB', { weekday: 'short', day: 'numeric' }).format(new Date())
  const cell = page.getByRole('textbox', { name: `Research on ${today}` })
  await cell.fill('2:00')
  await cell.press('Enter')
  await expect(cell).toHaveValue('2:00')

  await cell.fill('30m')
  await cell.press('Enter')
  await expect(cell).toHaveValue('0:30')
  await expect(page.getByRole('row', { name: /^Total/ })).toContainText('0:30')

  await page.getByRole('link', { name: 'Entries', exact: true }).click()
  await expect(entryRow(page, 'No description')).toContainText('Research')
  await expect(entryRow(page, 'No description')).toContainText('0:30:00')
})

test('change and delete several entries at once', async ({ page }) => {
  await registerNewUser(page)
  await page.getByRole('radio', { name: 'Manual mode' }).click()
  for (const [description, start, end] of [
    ['Emails', '08:00', '08:30'],
    ['Reading', '08:30', '09:00'],
  ]) {
    await page.getByRole('combobox', { name: 'Description' }).fill(description)
    await page.getByLabel('Start time').fill(start)
    await page.getByLabel('End time').fill(end)
    await page.getByRole('button', { name: 'Add', exact: true }).click()
    await expect(entryRow(page, description)).toBeVisible()
    await expect(page.getByLabel('Start time')).toHaveValue(end)
  }

  await page.getByRole('button', { name: 'Select', exact: true }).click()
  await page.getByRole('checkbox', { name: /^Select all entries on/ }).click()
  const toolbar = page.getByRole('toolbar', { name: 'Selected entries' })
  await expect(toolbar).toContainText('2 selected')

  await toolbar.getByRole('button', { name: 'Project' }).click()
  await page.getByPlaceholder('Search projects, tasks or clients').fill('Admin')
  await page.getByRole('option', { name: 'Create project “Admin”' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Create project' }).click()
  // While selecting, each row is controlled by its checkbox.
  const selectedRow = (description: string) =>
    page.getByRole('listitem').filter({ has: page.getByRole('checkbox', { name: `Select “${description}”` }) })
  await expect(selectedRow('Emails')).toContainText('Admin')
  await expect(selectedRow('Reading')).toContainText('Admin')

  await toolbar.getByRole('button', { name: 'Billable' }).click()
  await page.getByRole('menuitem', { name: 'Mark billable' }).click()
  await expect(selectedRow('Emails').getByLabel('Billable')).toBeVisible()

  await toolbar.getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete 2 entries' }).click()
  await expect(page.getByText('Nothing tracked yet this week')).toBeVisible()
})

test('keyboard shortcuts start and stop the timer and move between pages', async ({ page }) => {
  await registerNewUser(page)
  await page.locator('main').click({ position: { x: 5, y: 5 } })

  await page.keyboard.press('s')
  await expect(page.getByRole('button', { name: 'Stop timer' })).toBeVisible()
  await page.keyboard.press('s')
  await expect(page.getByRole('button', { name: 'Start timer' })).toBeVisible()

  await page.keyboard.press('g')
  await page.keyboard.press('p')
  await expect(page).toHaveURL(/\/projects$/)

  await page.keyboard.press('n')
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByRole('combobox', { name: 'Description' })).toBeFocused()

  await page.getByRole('combobox', { name: 'Description' }).blur()
  await page.keyboard.press('Shift+?')
  await expect(page.getByRole('dialog').getByRole('heading', { name: 'Keyboard shortcuts' })).toBeVisible()
})
