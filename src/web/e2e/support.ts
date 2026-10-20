import { expect, request as playwrightRequest, type Page } from '@playwright/test'

export const PASSWORD = 'correct horse battery'

export const newEmail = () => `e2e-${crypto.randomUUID()}@example.com`

/** Registers a fresh user through the UI, skips naming the organization, and opens Entries from Home, where tests track time and read the list. */
export async function registerNewUser(page: Page, email = newEmail()) {
  await page.goto('/register')
  await page.getByLabel('Full name').fill('Ana Anić')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Create account' }).click()
  // The organization keeps the name registration gave it.
  await expect(page).toHaveURL(/\/welcome$/)
  await page.getByRole('button', { name: 'Skip' }).click()
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByRole('combobox', { name: 'Description' })).toBeVisible()
  await openEntries(page)
  return email
}

/**
 * Opens Entries: the timer above the list of entries. Waits for its own heading, since Home's timer is still on screen
 * for a moment, and text typed into it would be lost.
 */
export async function openEntries(page: Page) {
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Entries' }).click()
  await expect(page).toHaveURL(/\/entries$/)
  await expect(page.getByRole('heading', { name: 'Entries', level: 1 })).toBeAttached()
  await expect(page.getByRole('heading', { name: 'Home', level: 1 })).toHaveCount(0)
  await expect(page.getByRole('combobox', { name: 'Description' })).toBeVisible()
}

/** A time entry row in the list, found by its description. */
export function entryRow(page: Page, description: string) {
  return page.getByRole('listitem').filter({ has: page.getByRole('button', { name: `Edit “${description}”` }) })
}

/**
 * The latest email to an address, read from Mailpit, which catches everything the dev API sends. With a subject,
 * waits for an email with that subject, since an account also gets other emails, such as the confirmation.
 */
export async function latestEmailTo(address: string, subject?: string): Promise<{ subject: string; text: string }> {
  const mailpit = await playwrightRequest.newContext({ baseURL: 'http://localhost:8025' })
  const query = encodeURIComponent(subject ? `to:"${address}" subject:"${subject}"` : `to:"${address}"`)
  await expect.poll(async () => (await (await mailpit.get(`/api/v1/search?query=${query}`)).json()).messages_count, { timeout: 10_000 }).toBeGreaterThan(0)
  const { messages } = await (await mailpit.get(`/api/v1/search?query=${query}`)).json()
  const message = await (await mailpit.get(`/api/v1/message/${messages[0].ID}`)).json()
  return { subject: message.Subject, text: message.Text }
}
