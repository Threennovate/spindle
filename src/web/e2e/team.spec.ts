import { expect, test } from '@playwright/test'
import { latestEmailTo, newEmail, PASSWORD, registerNewUser } from './support.ts'

test('an admin invites someone by email, who joins by creating an account', async ({ page, browser }) => {
  await registerNewUser(page)
  await page.getByRole('link', { name: 'Team' }).click()
  await expect(page.getByRole('heading', { name: 'Team' })).toBeVisible()

  const invitee = newEmail()
  await page.getByRole('button', { name: 'Invite people' }).click()
  await page.getByRole('dialog').getByLabel('Email').fill(invitee)
  await page.getByRole('dialog').getByRole('button', { name: 'Send invitation' }).click()
  await expect(page.getByRole('dialog').getByRole('heading', { name: 'Invitation sent' })).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: 'Done' }).click()
  await expect(page.getByText(invitee)).toBeVisible()

  const email = await latestEmailTo(invitee)
  expect(email.subject).toBe('Ana Anić invited you to Ana Anić on Spindle')
  const link = /http:\/\/localhost:5173\/invitations\/[\w-]+/.exec(email.text)![0]

  // The invitee follows the link in a browser that is not signed in.
  const inviteeContext = await browser.newContext()
  const inviteePage = await inviteeContext.newPage()
  await inviteePage.goto(link)
  await expect(inviteePage.getByText('Join Ana Anić', { exact: true })).toBeVisible()
  await inviteePage.getByRole('link', { name: 'Create an account' }).click()
  await expect(inviteePage.getByLabel('Email')).toHaveValue(invitee)
  await inviteePage.getByLabel('Full name').fill('Ivo Ivić')
  await inviteePage.getByLabel('Password').fill(PASSWORD)
  await inviteePage.getByRole('button', { name: 'Create account' }).click()
  await expect(inviteePage).toHaveURL(/\/$/)
  await expect(inviteePage.getByRole('button', { name: /^Workspace/ }).first()).toContainText('Ana Anić')
  await inviteeContext.close()

  await page.reload()
  const row = page.getByRole('row').filter({ hasText: 'Ivo Ivić' })
  await expect(row).toBeVisible()
  await expect(page.getByText('No pending invitations.')).toBeVisible()
  await row.getByRole('combobox', { name: 'Role of Ivo Ivić' }).click()
  await page.getByRole('option', { name: 'Admin' }).click()
  await expect(row.getByRole('combobox', { name: 'Role of Ivo Ivić' })).toHaveText('Admin')
})

test('switch between workspaces, and create a new one', async ({ page }) => {
  await registerNewUser(page)

  const switcher = page.getByRole('button', { name: /^Workspace/ }).first()
  await switcher.click()
  await page.getByRole('menuitem', { name: 'New workspace' }).click()
  await page.getByLabel('Workspace name').fill('Consulting')
  await page.getByRole('button', { name: 'Create', exact: true }).click()
  await expect(switcher).toContainText('Consulting')

  await switcher.click()
  await page.getByRole('menuitem', { name: 'Ana Anić' }).click()
  await expect(switcher).toContainText('Ana Anić')
  await expect(page).toHaveURL(/\/$/)
})

test('a signed-in person accepts an invitation to their address', async ({ page, browser }) => {
  // Someone who already has an account.
  const existing = newEmail()
  const existingContext = await browser.newContext()
  const existingPage = await existingContext.newPage()
  await registerNewUser(existingPage, existing)

  await registerNewUser(page)
  await page.getByRole('link', { name: 'Team' }).click()
  await page.getByRole('button', { name: 'Invite people' }).click()
  await page.getByRole('dialog').getByLabel('Email').fill(existing)
  await page.getByRole('dialog').getByRole('button', { name: 'Send invitation' }).click()
  const link = await page.getByRole('dialog').getByLabel('Invitation link', { exact: true }).inputValue()

  await existingPage.goto(link)
  await existingPage.getByRole('button', { name: 'Join Ana Anić' }).click()
  await expect(existingPage).toHaveURL(/\/$/)
  await existingPage.getByRole('button', { name: /^Workspace/ }).first().click()
  // Their own workspace and the one they joined; as a member of the new organization they cannot create workspaces there.
  await expect(existingPage.getByRole('menuitem')).toHaveCount(2)
  await existingContext.close()
})
