import { expect, request, test } from '@playwright/test'
import { addMember, call, createUser, localTime, signIn } from './api.ts'
import { entryRow, PASSWORD } from './support.ts'

test('people set their profile and how time is shown', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await call(user, 'POST', '/api/workspaces/{ws}/time-entries', { description: 'Design review', start: localTime(0, 9), stop: localTime(0, 10, 30) })
  await signIn(page, user)
  await expect(entryRow(page, 'Design review')).toContainText('1:30:00')

  await page.getByRole('button', { name: 'Account', exact: true }).click()
  await page.getByRole('menuitem', { name: 'Account settings' }).click()
  await expect(page.getByRole('heading', { name: 'Account', exact: true })).toBeVisible()

  await page.getByLabel('Full name').fill('Ana Horvat')
  await page.getByRole('combobox', { name: 'Time zone' }).click()
  await page.getByPlaceholder('Search cities or regions').fill('Tokyo')
  await page.getByRole('option', { name: /Tokyo/ }).click()
  await expect(page.getByRole('combobox', { name: 'Time zone' })).toContainText('Tokyo (Asia)')
  await page.getByRole('button', { name: 'Save profile' }).click()
  await expect(page.getByText('Profile saved')).toBeVisible()

  await page.getByLabel('Time format').click()
  await page.getByRole('option', { name: '12-hour (1:30 PM)' }).click()
  await page.getByLabel('Duration format').click()
  await page.getByRole('option', { name: '1.50 h' }).click()
  await page.getByRole('button', { name: 'Save preferences' }).click()
  await expect(page.getByText('Preferences saved')).toBeVisible()

  await page.reload()
  await expect(page.getByLabel('Full name')).toHaveValue('Ana Horvat')
  await expect(page.getByLabel('Duration format')).toHaveText('1.50 h')

  // Back in Zagreb, the entry shows in the chosen formats.
  await page.getByRole('combobox', { name: 'Time zone' }).click()
  await page.getByPlaceholder('Search cities or regions').fill('Zagreb')
  await page.getByRole('option', { name: /Zagreb/ }).click()
  await page.getByRole('button', { name: 'Save profile' }).click()
  await expect(page.getByText('Profile saved')).toBeVisible()
  await page.getByRole('link', { name: 'Entries' }).click()
  const row = entryRow(page, 'Design review')
  await expect(row).toContainText('1.50 h')
  await expect(row).toContainText(/9:00\s?AM – 10:30\s?AM/i)
})

test('people change their password and sign other devices out', async ({ page }) => {
  const user = await createUser('Ana Anić')
  const api = await request.newContext({ baseURL: 'http://localhost:5258' })
  const phone = await (await api.post('/api/auth/login', { data: { email: user.email, password: PASSWORD, deviceName: 'Phone' } })).json()
  await signIn(page, user)
  await page.goto('/account')

  const devices = page.getByRole('list', { name: 'Signed-in devices' })
  await expect(devices.getByRole('listitem')).toHaveCount(3)
  await expect(devices.getByRole('listitem').first()).toContainText('This device')
  await devices.getByRole('listitem').filter({ hasText: 'Phone' }).getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByText('Signed out of Phone')).toBeVisible()
  await expect(devices.getByRole('listitem').filter({ hasText: 'Phone' })).toHaveCount(0)
  expect((await api.post('/api/auth/refresh', { data: { refreshToken: phone.refreshToken } })).status()).toBe(401)

  await page.getByLabel('Current password').fill('not my password')
  await page.getByLabel('New password', { exact: true }).fill('a much better secret')
  await page.getByLabel('Repeat the new password').fill('a much better secret')
  await page.getByRole('button', { name: 'Change password' }).click()
  await expect(page.getByText('That is not your current password.')).toBeVisible()
  await page.getByLabel('Current password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Change password' }).click()
  await expect(page.getByText('Password changed. Your other devices were signed out.')).toBeVisible()
  await expect(devices.getByRole('listitem')).toHaveCount(1)

  // The new password is the one that works now.
  await page.getByRole('button', { name: 'Account', exact: true }).click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await page.getByLabel('Email').fill(user.email)
  await page.getByLabel('Password').fill('a much better secret')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page).toHaveURL(/\/$/)
})

test('people download their data and delete their account', async ({ page }) => {
  const admin = await createUser('Ana Anić')
  const member = await addMember(admin, 'Ivo Ivić')
  await call(member, 'POST', '/api/workspaces/{ws}/time-entries', { description: 'Design', start: localTime(-1, 9), stop: localTime(-1, 10) })
  await signIn(page, member)
  await page.goto('/account')

  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download my data' }).click()
  expect((await download).suggestedFilename()).toMatch(/^spindle-data-\d{4}-\d{2}-\d{2}\.json$/)

  await page.getByRole('button', { name: 'Delete my account…' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Your password').fill('wrong password')
  await dialog.getByRole('button', { name: 'Delete account' }).click()
  await expect(dialog.getByText('That is not your current password.')).toBeVisible()
  await dialog.getByLabel('Your password').fill(PASSWORD)
  await dialog.getByRole('button', { name: 'Delete account' }).click()
  await expect(page).toHaveURL(/\/login/)

  await page.getByLabel('Email').fill(member.email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page).toHaveURL(/\/login/)
})
