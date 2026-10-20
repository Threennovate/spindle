import { expect, test, type Page } from '@playwright/test'
import { addMember, call, createUser, localDay, localTime, signIn, updateSettings } from './api.ts'
import { entryRow } from './support.ts'

const PX_PER_MINUTE = 48 / 60

/** How the timesheet names a day's column, e.g. "Sun 27". */
function dayColumn(offset: number) {
  const date = new Date()
  date.setDate(date.getDate() + offset)
  return new Intl.DateTimeFormat('en-GB', { weekday: 'short', day: 'numeric' }).format(date)
}

async function openSettings(page: Page) {
  await page.getByRole('link', { name: 'Settings' }).click()
  await expect(page.getByRole('heading', { name: 'Workspace settings' })).toBeVisible()
}

test('an admin sets the workspace’s money, rules and groups', async ({ page }) => {
  const admin = await createUser('Ana Anić')
  await addMember(admin, 'Ivo Ivić')
  await signIn(page, admin)

  await openSettings(page)
  await page.getByLabel('Currency').click()
  await page.getByRole('option', { name: 'USD' }).click()
  await page.getByLabel('Default hourly rate (USD)').fill('80')
  await page.getByLabel('Only admins create projects and clients').click()
  await page.getByLabel('Lock entries before').fill(localDay(-7))
  await page.getByLabel('Project', { exact: true }).click()
  await page.getByRole('button', { name: 'Save settings' }).click()
  await expect(page.getByText('Settings saved')).toBeVisible()

  await page.reload()
  await expect(page.getByLabel('Currency')).toHaveText('USD')
  await expect(page.getByLabel('Default hourly rate (USD)')).toHaveValue('80')
  await expect(page.getByLabel('Only admins create projects and clients')).toBeChecked()
  await expect(page.getByLabel('Only admins create tags')).not.toBeChecked()
  await expect(page.getByLabel('Lock entries before')).toHaveValue(localDay(-7))
  await expect(page.getByLabel('Project', { exact: true })).toBeChecked()

  // Rates per member, in the workspace's currency.
  await page.getByRole('link', { name: 'Team' }).click()
  const ivo = page.getByRole('row').filter({ hasText: 'Ivo Ivić' })
  await expect(ivo.getByRole('cell', { name: 'Default' })).toBeVisible()
  await ivo.getByRole('button', { name: 'Actions for Ivo Ivić' }).click()
  await page.getByRole('menuitem', { name: 'Edit rates…' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByLabel('Billable rate (USD per hour)')).toHaveAttribute('placeholder', 'Workspace default, $80.00/h')
  await dialog.getByLabel('Billable rate (USD per hour)').fill('95')
  await dialog.getByLabel('Labor cost (USD per hour)').fill('40,5')
  await dialog.getByRole('button', { name: 'Save rates' }).click()
  await expect(ivo.getByRole('cell', { name: '$95.00/h' })).toBeVisible()
  await expect(ivo.getByRole('cell', { name: '$40.50/h' })).toBeVisible()

  // A new role keeps the rates.
  await ivo.getByRole('combobox', { name: 'Role of Ivo Ivić' }).click()
  await page.getByRole('option', { name: 'Admin' }).click()
  await expect(ivo.getByRole('combobox', { name: 'Role of Ivo Ivić' })).toHaveText('Admin')
  await page.reload()
  await expect(ivo.getByRole('cell', { name: '$95.00/h' })).toBeVisible()
  await expect(ivo.getByRole('cell', { name: '$40.50/h' })).toBeVisible()

  // Groups.
  const groups = page.getByRole('region', { name: /^Groups/ })
  await expect(groups.getByText('No groups yet.')).toBeVisible()
  await page.getByRole('button', { name: 'New group' }).click()
  await dialog.getByLabel('Name').fill('Design')
  await dialog.getByRole('checkbox', { name: /Ivo Ivić/ }).check()
  await dialog.getByRole('button', { name: 'Create group' }).click()
  await expect(groups.getByText('1 member · Ivo Ivić')).toBeVisible()

  await groups.getByRole('button', { name: 'Actions for the group Design' }).click()
  await page.getByRole('menuitem', { name: 'Edit' }).click()
  await expect(dialog.getByRole('checkbox', { name: /Ivo Ivić/ })).toBeChecked()
  await dialog.getByLabel('Name').fill('Product design')
  await dialog.getByRole('checkbox', { name: /Ana Anić/ }).check()
  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(groups.getByText('Product design')).toBeVisible()
  await expect(groups.getByText('2 members · Ana Anić, Ivo Ivić')).toBeVisible()

  await groups.getByRole('button', { name: 'Actions for the group Product design' }).click()
  await page.getByRole('menuitem', { name: 'Delete' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click()
  await expect(groups.getByText('No groups yet.')).toBeVisible()

  // A project's own rate, and a rate for one person on it.
  await page.getByRole('link', { name: 'Projects' }).click()
  await page.getByRole('button', { name: 'New project' }).click()
  await dialog.getByLabel('Name').fill('Website')
  await expect(dialog.getByLabel('Hourly rate (USD)')).toHaveAttribute('placeholder', 'Member rates, or $80.00/h')
  await dialog.getByLabel('Hourly rate (USD)').fill('120')
  await dialog.getByLabel('Billable', { exact: true }).click()
  await dialog.getByRole('button', { name: 'Create project' }).click()
  await page.getByRole('link', { name: 'Website' }).click()
  await expect(page.getByText('Billable · $120.00/h')).toBeVisible()

  const members = page.getByRole('list', { name: 'Project members' })
  await expect(members.getByRole('listitem').filter({ hasText: 'Ana Anić' })).toContainText('Manager')
  await page.getByRole('button', { name: 'Add people' }).click()
  await page.getByPlaceholder('Search people').fill('Ivo')
  await page.keyboard.press('Enter')
  const ivoOnProject = members.getByRole('listitem').filter({ hasText: 'Ivo Ivić' })
  await expect(ivoOnProject).toBeVisible()
  await expect(ivoOnProject).not.toContainText('Manager')

  await ivoOnProject.getByRole('button', { name: 'Actions for Ivo Ivić on the project' }).click()
  await page.getByRole('menuitem', { name: 'Make manager' }).click()
  await expect(ivoOnProject).toContainText('Manager')
  await ivoOnProject.getByRole('button', { name: 'Actions for Ivo Ivić on the project' }).click()
  await page.getByRole('menuitem', { name: 'Set rate on this project…' }).click()
  await dialog.getByLabel('Hourly rate (USD)').fill('150')
  await dialog.getByRole('button', { name: 'Save rate' }).click()
  await expect(ivoOnProject).toContainText('$150.00/h')
  // Setting the rate kept him a manager.
  await expect(ivoOnProject).toContainText('Manager')
})

test('a member works within the rules the admins set', async ({ page }) => {
  const admin = await createUser('Ana Anić')
  const member = await addMember(admin, 'Ivo Ivić')
  await call(admin, 'POST', '/api/workspaces/{ws}/projects', { name: 'Website', color: '#0B83D9' })
  await call(admin, 'POST', '/api/workspaces/{ws}/projects', { name: 'Payroll', color: '#D94182', isPrivate: true })
  // Yesterday's work, before the admin locks everything before today.
  await call(member, 'POST', '/api/workspaces/{ws}/time-entries', { description: 'Invoiced work', start: localTime(-1, 9), stop: localTime(-1, 10) })
  await updateSettings(admin, { onlyAdminsCreateProjects: true, onlyAdminsCreateTags: true, lockedBefore: localDay(0), requireProject: true })
  await signIn(page, member)

  // Locked time is shown, not changed.
  await page.getByRole('button', { name: 'Load the week before' }).click()
  const lockedEntry = page.getByRole('listitem').filter({ has: page.getByRole('button', { name: 'View “Invoiced work”' }) })
  await expect(lockedEntry.getByLabel('Locked')).toBeVisible()
  await lockedEntry.getByRole('button', { name: 'View “Invoiced work”' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('heading', { name: 'Time entry' })).toBeVisible()
  await expect(dialog.getByText(/^Time before .* is locked\. Ask an admin to change it\.$/)).toBeVisible()
  await expect(dialog.getByLabel('Description')).toBeDisabled()
  await expect(dialog.getByRole('button', { name: 'Delete' })).toHaveCount(0)
  await dialog.getByRole('button', { name: 'Done' }).click()
  await lockedEntry.getByRole('button', { name: 'More actions for “Invoiced work”' }).click()
  await expect(page.getByRole('menuitem', { name: 'Delete' })).toBeDisabled()
  await expect(page.getByRole('menuitem', { name: 'Duplicate' })).toBeDisabled()
  await page.keyboard.press('Escape')

  // A finished entry needs a project here, so stopping the timer asks for one.
  await page.getByRole('combobox', { name: 'Description' }).fill('Research')
  const timer = page.getByRole('region', { name: 'Timer' })
  await timer.getByRole('button', { name: 'Start timer' }).click()
  await timer.getByRole('button', { name: 'Stop timer' }).click()
  await expect(page.getByText('Add the details this workspace requires to stop the timer.')).toBeVisible()
  await expect(dialog.getByRole('heading', { name: 'Stop timer' })).toBeVisible()
  await expect(dialog.getByText('A project is required in this workspace.')).toBeVisible()

  // Only projects the member can see are offered, and none can be created.
  await dialog.getByRole('button', { name: 'Choose project' }).click()
  await expect(page.getByRole('option', { name: 'Payroll' })).toHaveCount(0)
  await expect(page.getByRole('option', { name: /Create/ })).toHaveCount(0)
  await page.getByRole('option', { name: 'Website' }).click()
  await dialog.getByRole('button', { name: 'Save and stop' }).click()
  await expect(page.getByRole('dialog', { name: 'Stop timer' })).toBeHidden()
  await expect(entryRow(page, 'Research')).toContainText('Website')
  await expect(timer.getByRole('button', { name: 'Start timer' })).toBeVisible()

  // The calendar shows locked time, but does not move it.
  await page.goto(`/calendar?view=day&date=${localDay(-1)}`)
  const grid = page.getByRole('grid', { name: 'Time entries by time of day' })
  await expect(grid).toHaveAttribute('aria-busy', 'false')
  const block = page.getByRole('button', { name: /^Invoiced work, no project, 09:00 – 10:00, locked$/ })
  await expect(block).toBeVisible()
  const box = (await grid.boundingBox())!
  const x = box.x + 56 + (box.width - 56) / 2
  await page.mouse.move(x, box.y + 9.5 * 60 * PX_PER_MINUTE)
  await page.mouse.down()
  await page.mouse.move(x, box.y + 11.5 * 60 * PX_PER_MINUTE, { steps: 8 })
  await page.mouse.up()
  await expect(dialog.getByRole('heading', { name: 'Time entry' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Done' }).click()
  await expect(block).toBeVisible()

  // So does the timesheet.
  await page.goto(`/timesheet?date=${localDay(-1)}`)
  const lockedDay = page.getByRole('textbox', { name: new RegExp(` on ${dayColumn(-1)}$`) })
  await expect(lockedDay.first()).toBeVisible()
  for (const cell of await lockedDay.all()) {
    await expect(cell).toBeDisabled()
  }

  // Members add nothing to the workspace here, and change only what they manage.
  await page.getByRole('link', { name: 'Projects' }).click()
  await expect(page.getByRole('link', { name: 'Website' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Payroll' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'New project' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Actions for Website' })).toHaveCount(0)
  await page.getByRole('link', { name: 'Website' }).click()
  await expect(page.getByRole('heading', { name: 'Website' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Edit project' })).toHaveCount(0)
  await expect(page.getByLabel('New task name')).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Add people' })).toHaveCount(0)

  await page.getByRole('link', { name: 'Tags' }).click()
  await expect(page.getByRole('heading', { name: 'Tags' })).toBeVisible()
  await expect(page.getByLabel('New tag name')).toHaveCount(0)
  await page.getByRole('link', { name: 'Clients' }).click()
  await expect(page.getByRole('heading', { name: 'Clients' })).toBeVisible()
  await expect(page.getByLabel('New client name')).toHaveCount(0)

  await openSettings(page)
  await expect(page.getByText('Only workspace admins can change these settings.')).toBeVisible()
  await expect(page.getByLabel('Name')).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Save settings' })).toHaveCount(0)
})
