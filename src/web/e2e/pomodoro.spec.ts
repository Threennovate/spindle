import { expect, test } from '@playwright/test'
import { call, createUser, signIn } from './api.ts'

test('a Pomodoro interval ends with a reminder, stops the timer and starts a break', async ({ page }) => {
  const user = await createUser('Ana Anić')
  await signIn(page, user)

  await page.goto('/account')
  await page.getByLabel('Pomodoro reminders').click()
  await page.getByLabel('Focus (minutes)').fill('5')
  await page.getByLabel('Break (minutes)').fill('1')
  await page.getByLabel('Stop the timer when an interval ends').click()
  await page.getByRole('button', { name: 'Save Pomodoro settings' }).click()
  await expect(page.getByText('Pomodoro is on')).toBeVisible()

  // A timer that is a few seconds away from five minutes.
  await call(user, 'POST', '/api/workspaces/{ws}/time-entries', { description: 'Deep work', start: new Date(Date.now() - (5 * 60 - 4) * 1000).toISOString() })
  // Opened fresh, as the timer was started elsewhere.
  await page.goto('/')
  const timer = page.getByRole('region', { name: 'Timer' })
  await expect(timer.getByLabel(/^Focus interval: 0:0\d left$/)).toBeVisible()

  await expect(page.getByText('5 minutes of focus done. Take a 1-minute break.')).toBeVisible({ timeout: 15_000 })
  await expect(timer.getByRole('button', { name: 'Start timer' })).toBeVisible()
  await expect(timer.getByLabel(/^Break: [01]:\d\d left$/)).toBeVisible()

  await timer.getByRole('button', { name: 'End the break' }).click()
  await expect(timer.getByLabel(/^Break:/)).toHaveCount(0)
})
