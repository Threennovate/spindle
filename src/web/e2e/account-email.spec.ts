import { expect, test } from '@playwright/test'
import { createUser, signIn } from './api.ts'
import { latestEmailTo } from './support.ts'

test('a forgotten password is reset through an emailed link', async ({ page }) => {
  const user = await createUser('Ana Anić')

  await page.goto('/login')
  await page.getByRole('link', { name: 'Forgot your password?' }).click()
  await expect(page).toHaveURL(/\/forgot-password$/)
  // The URL changes before React replaces the login form, which also has an Email field.
  await expect(page.getByRole('button', { name: 'Send the link' })).toBeVisible()
  await page.getByLabel('Email').fill(user.email)
  await page.getByRole('button', { name: 'Send the link' }).click()
  await expect(page.getByText('Check your inbox')).toBeVisible()

  const email = await latestEmailTo(user.email, 'Reset your Spindle password')
  const link = /http:\/\/localhost:5173\/reset-password\?\S+/.exec(email.text)![0]
  await page.goto(link)
  await page.getByLabel('New password', { exact: true }).fill('a fresh start here')
  await page.getByLabel('Repeat the new password').fill('a fresh start here')
  await page.getByRole('button', { name: 'Save the new password' }).click()

  await expect(page).toHaveURL(/\/login\?notice=password-reset/)
  await expect(page.getByText('Your password was changed. Sign in with the new one.')).toBeVisible()
  await page.getByLabel('Email').fill(user.email)
  await page.getByLabel('Password').fill('a fresh start here')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByRole('combobox', { name: 'Description' })).toBeVisible()

  // The link works once.
  await page.goto(link)
  await page.getByLabel('New password', { exact: true }).fill('yet another password')
  await page.getByLabel('Repeat the new password').fill('yet another password')
  await page.getByRole('button', { name: 'Save the new password' }).click()
  await expect(page.getByText('This link has expired or was already used.')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Ask for a new link' })).toBeVisible()
})

test('new accounts are reminded to confirm their email until they do', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await signIn(page, user)

  const reminder = page.getByRole('status').filter({ hasText: 'Confirm your email address' })
  await expect(reminder).toContainText(user.email)
  await reminder.getByRole('button', { name: 'Send it again' }).click()
  await expect(page.getByText(`We sent a new link to ${user.email}`)).toBeVisible()

  const email = await latestEmailTo(user.email, 'Confirm your email for Spindle')
  await page.goto(/http:\/\/localhost:5173\/confirm-email\?\S+/.exec(email.text)![0])
  await expect(page.getByText('Your email is confirmed')).toBeVisible()
  await page.getByRole('link', { name: 'Continue to Spindle' }).click()
  await expect(page).toHaveURL(/\/$/)
  await expect(reminder).toHaveCount(0)

  await page.goto('/account')
  await expect(page.getByText('Confirmed', { exact: true })).toBeVisible()
})

test('a broken confirmation link says so', async ({ page }) => {
  await page.goto('/confirm-email?user=0199a8f4-1b2c-7d3e-8f40-123456789abc&token=nope')
  await expect(page.getByText('This link did not work')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Sign in' })).toBeVisible()
})
