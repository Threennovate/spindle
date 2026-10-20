import { expect, request, type APIRequestContext, type Page } from '@playwright/test'
import { newEmail, openEntries, PASSWORD } from './support.ts'

const API = 'http://localhost:5258'

/** Someone with an account, driven through the API: fast setup for what a test is not about. */
export interface ApiUser {
  email: string
  fullName: string
  workspaceId: string
  api: APIRequestContext
}

/** Creates an account; with an invitation to its address, it joins that workspace. */
export async function createUser(fullName: string, invitation?: { email: string; token: string }): Promise<ApiUser> {
  const email = invitation?.email ?? newEmail()
  const anonymous = await request.newContext({ baseURL: API })
  const registered = await anonymous.post('/api/auth/register', {
    data: { email, password: PASSWORD, fullName, timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone, invitationToken: invitation?.token },
  })
  expect(registered.ok(), await registered.text()).toBe(true)
  const { accessToken } = await registered.json()
  await anonymous.dispose()

  const api = await request.newContext({ baseURL: API, extraHTTPHeaders: { Authorization: `Bearer ${accessToken}` } })
  const me = await (await api.get('/api/me')).json()
  return { email, fullName, workspaceId: me.defaultWorkspaceId, api }
}

/** Sends a request and returns the JSON it answers with, failing the test on an error. */
export async function call<T = unknown>(user: ApiUser, method: 'GET' | 'POST' | 'PUT' | 'DELETE', path: string, data?: unknown): Promise<T> {
  const response = await user.api.fetch(path.replace('{ws}', user.workspaceId), { method, data })
  expect(response.ok(), `${method} ${path}: ${await response.text()}`).toBe(true)
  return response.status() === 204 ? (undefined as T) : ((await response.json()) as T)
}

/** Invites someone into the admin's workspace as a member, and creates their account from the invitation. */
export async function addMember(admin: ApiUser, fullName: string): Promise<ApiUser> {
  const email = newEmail()
  const sent = await call<{ inviteUrl: string }>(admin, 'POST', '/api/workspaces/{ws}/invitations', { email, role: 'member' })
  const member = await createUser(fullName, { email, token: sent.inviteUrl.split('/').at(-1)! })
  expect(member.workspaceId).toBe(admin.workspaceId)
  return member
}

interface Settings {
  currency: string
  defaultHourlyRate: number | null
  onlyAdminsCreateProjects: boolean
  onlyAdminsCreateTags: boolean
  lockedBefore: string | null
  requireDescription: boolean
  requireProject: boolean
  requireTask: boolean
  requireTag: boolean
}

const DEFAULT_SETTINGS: Settings = {
  currency: 'EUR',
  defaultHourlyRate: null,
  onlyAdminsCreateProjects: false,
  onlyAdminsCreateTags: false,
  lockedBefore: null,
  requireDescription: false,
  requireProject: false,
  requireTask: false,
  requireTag: false,
}

/** Replaces the workspace's settings: the defaults, with these changes. */
export async function updateSettings(admin: ApiUser, changes: Partial<Settings>) {
  await call(admin, 'PUT', '/api/workspaces/{ws}/settings', { name: admin.fullName, ...DEFAULT_SETTINGS, ...changes })
}

/** Signs in through the login page. */
export async function signIn(page: Page, user: ApiUser) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(user.email)
  await page.getByLabel('Password').fill(PASSWORD)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await expect(page).toHaveURL(/\/$/)
  // Signed in and loaded, so what the test does next does not race the app's own navigation.
  await expect(page.getByRole('combobox', { name: 'Description' })).toBeVisible()
  await openEntries(page)
}

/** A calendar day in the local time zone, as `yyyy-mm-dd`, `offset` days from today. */
export function localDay(offset = 0): string {
  const date = new Date()
  date.setDate(date.getDate() + offset)
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

/** An instant on a local calendar day, `offset` days from today. */
export function localTime(offset: number, hours: number, minutes = 0): string {
  const date = new Date()
  date.setDate(date.getDate() + offset)
  date.setHours(hours, minutes, 0, 0)
  return date.toISOString()
}
