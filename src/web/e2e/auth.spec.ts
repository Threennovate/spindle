import { expect, test, type Page } from '@playwright/test'

const PASSWORD = 'correct horse battery'

const newEmail = () => `e2e-${crypto.randomUUID()}@example.com`

async function register(page: Page, email: string) {
  await page.goto('/register')
  await page.getByLabel('Full name').fill('Ana Anić')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Create account' }).click()
  await skipWelcome(page)
  await expectHomePage(page)
}

async function signOut(page: Page) {
  await page.getByRole('button', { name: 'Account' }).click()
  await page.getByRole('menuitem', { name: 'Sign out' }).click()
  await expectSignInPage(page)
}

/** Right after registering: the organization keeps the name registration gave it. */
async function skipWelcome(page: Page) {
  await expect(page).toHaveURL(/\/welcome$/)
  await page.getByRole('button', { name: 'Skip' }).click()
}

async function expectHomePage(page: Page) {
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByRole('combobox', { name: 'Description' })).toBeVisible()
}

// The URL changes before the page is rendered; wait for both, or a reload can interrupt the navigation.
async function expectSignInPage(page: Page) {
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible()
}

test('a new user registers, stays signed in across reloads, signs out and signs in again', async ({ page }) => {
  const email = newEmail()

  await page.goto('/')
  await expectSignInPage(page)
  await page.getByRole('link', { name: 'Create an account' }).click()

  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByText('Enter your name.')).toBeVisible()
  await expect(page.getByText('Enter your email address.')).toBeVisible()
  await expect(page.getByText('Use at least 8 characters.')).toBeVisible()

  await page.getByLabel('Full name').fill('Ana Anić')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Create account' }).click()

  await skipWelcome(page)
  await expectHomePage(page)
  await expect(page).toHaveTitle('Home · Spindle')

  await page.reload()
  await expectHomePage(page)

  await page.getByRole('button', { name: 'Account' }).click()
  await expect(page.getByRole('menu').getByText(email)).toBeVisible()
  await page.keyboard.press('Escape')
  await signOut(page)

  await page.reload()
  await expectSignInPage(page)

  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill('wrong password')
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page.getByText('Invalid email or password.')).toBeVisible()

  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expectHomePage(page)
})

test('an email that is already registered is reported next to the field', async ({ page }) => {
  const email = newEmail()
  await register(page, email)
  await signOut(page)

  await page.goto('/register')
  await page.getByLabel('Full name').fill('Someone Else')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Create account' }).click()

  await expect(page.getByLabel('Email')).toHaveAttribute('aria-invalid', 'true')
  await expect(page.getByText(/is already taken/)).toBeVisible()
})

test('tabs share the session, and signing out in one tab signs out the others', async ({ context }) => {
  const first = await context.newPage()
  await register(first, newEmail())

  // A second tab restores the session from the same cookie.
  const second = await context.newPage()
  await second.goto('/')
  await expectHomePage(second)

  await signOut(first)

  await expectSignInPage(second)
})

test('signed-in users skip the sign-in page, and unknown pages say so', async ({ page }) => {
  await register(page, newEmail())

  await page.goto('/login')
  await expectHomePage(page)

  await page.goto('/no-such-page')
  await expect(page.getByRole('heading', { name: 'Page not found' })).toBeVisible()
})
