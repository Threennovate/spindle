import { expect, request, test, type Page } from '@playwright/test'
import { PASSWORD, registerNewUser } from './support.ts'

test.beforeEach(async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'Virtual WebAuthn authenticators require Chromium CDP.')
  const cdp = await page.context().newCDPSession(page)
  await cdp.send('WebAuthn.enable')
  await cdp.send('WebAuthn.addVirtualAuthenticator', {
    options: {
      protocol: 'ctap2', transport: 'internal', hasResidentKey: true,
      hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true,
    },
  })
})

async function addPasskey(page: Page) {
  await registerNewUser(page)
  await page.goto('/account')
  await page.getByRole('button', { name: 'Add a passkey' }).click()
  await page.getByLabel('Passkey name').fill('My laptop')
  await page.getByLabel('Your password', { exact: true }).fill('wrong password')
  await page.getByRole('button', { name: 'Create passkey', exact: true }).click()
  await expect(page.getByText('The password is incorrect or the account is temporarily locked.')).toBeVisible()
  await page.getByLabel('Your password', { exact: true }).fill(PASSWORD)
  await page.getByRole('button', { name: 'Create passkey', exact: true }).click()
  await expect(page.getByRole('list', { name: 'Your passkeys' })).toContainText('My laptop')
}

async function signOut(page: Page) {
  await page.getByRole('button', { name: 'Account', exact: true }).click()
  await page.getByRole('menuitem', { name: 'Sign out', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible()
}

test('a passkey registers, signs in without an email, survives reload and can be removed', async ({ page }) => {
  await addPasskey(page)
  await signOut(page)
  const assertionRequest = page.waitForRequest('**/api/auth/web/passkeys/login')
  await page.getByRole('button', { name: 'Sign in with a passkey' }).click()
  const signedRequest = await assertionRequest
  const headers = await signedRequest.allHeaders()
  await expect(page).toHaveURL(/\/$/)
  // Even a copy of the successful signed assertion and its original HttpOnly cookie cannot start another session.
  const replay = await page.context().request.post(signedRequest.url(), {
    data: signedRequest.postDataJSON(), headers: { cookie: headers.cookie },
  })
  expect(replay.status()).toBe(401)
  await page.reload()
  await expect(page.getByRole('combobox', { name: 'Description' })).toBeVisible()
  await page.goto('/account')
  await page.getByRole('list', { name: 'Your passkeys' }).getByRole('button', { name: 'Remove', exact: true }).click()
  await page.getByRole('button', { name: 'Remove passkey', exact: true }).click()
  await expect(page.getByText("You haven't added any passkeys yet.")).toBeVisible()
  await signOut(page)
  await page.getByRole('button', { name: 'Sign in with a passkey' }).click()
  await expect(page.getByText('The passkey could not be verified. Please try again.')).toBeVisible()
})

for (const corruption of ['signature', 'challenge', 'origin', 'verification'] as const) {
  test(`a passkey assertion with an invalid ${corruption} is rejected`, async ({ page }) => {
    await addPasskey(page)
    await signOut(page)
    await page.route('**/api/auth/web/passkeys/login', async (route) => {
      const body = route.request().postDataJSON()
      const credential = JSON.parse(body.credentialJson)
      if (corruption === 'signature') {
        credential.response.signature = Buffer.from('invalid signature').toString('base64url')
      } else if (corruption === 'verification') {
        const authData = Buffer.from(credential.response.authenticatorData, 'base64url')
        authData[32] &= ~4
        credential.response.authenticatorData = authData.toString('base64url')
      } else {
        const clientData = JSON.parse(Buffer.from(credential.response.clientDataJSON, 'base64url').toString())
        if (corruption === 'challenge') clientData.challenge = Buffer.from('different challenge').toString('base64url')
        else clientData.origin = 'https://attacker.example'
        credential.response.clientDataJSON = Buffer.from(JSON.stringify(clientData)).toString('base64url')
      }
      await route.continue({ postData: JSON.stringify({ ...body, credentialJson: JSON.stringify(credential) }) })
    }, { times: 1 })
    await page.getByRole('button', { name: 'Sign in with a passkey' }).click()
    await expect(page.getByText('The passkey could not be verified. Please try again.')).toBeVisible()
    await expect(page).toHaveURL(/\/login$/)
    await page.getByRole('button', { name: 'Sign in with a passkey' }).click()
    await expect(page).toHaveURL(/\/$/)
  })
}

test('native passkey sign-in exchanges a signed assertion for Keychain tokens without cookies', async ({ page }) => {
  await addPasskey(page)
  await signOut(page)
  const native = await request.newContext({ baseURL: 'http://localhost:5173' })
  try {
    const options = await (await native.post('/api/auth/passkeys/options')).json()
    const credentialJson = await page.evaluate(async (optionsJson: string) => {
      const publicKey = PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(optionsJson))
      const credential = await navigator.credentials.get({ publicKey }) as PublicKeyCredential
      return JSON.stringify(credential.toJSON())
    }, options.optionsJson)
    const body = { credentialJson, challengeToken: options.challengeToken, deviceName: 'Native Mac' }
    const response = await native.post('/api/auth/passkeys/login', { data: body })
    expect(response.status(), await response.text()).toBe(200)
    expect(response.headers()['set-cookie']).toBeUndefined()
    const tokens = await response.json()
    expect(tokens.refreshToken).toBeTruthy()
    const me = await native.get('/api/me', { headers: { Authorization: `Bearer ${tokens.accessToken}` } })
    expect(me.status()).toBe(200)
    expect((await native.post('/api/auth/passkeys/login', { data: body })).status()).toBe(401)
    expect((await native.post('/api/auth/refresh', { data: { refreshToken: tokens.refreshToken } })).status()).toBe(200)
  } finally { await native.dispose() }
})
