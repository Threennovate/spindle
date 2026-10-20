import { execSync } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { defineConfig, devices } from '@playwright/test'
import base from './playwright.config.ts'

/**
 * Screenshots of the web app for the Spindle docs (spindletrack.com/docs), taken against the local API with made-up
 * data: `npm run screenshots`. Needs the local PostgreSQL (../../scripts/local-db.sh) and Mailpit, like the end-to-end
 * tests, but uses a database of its own, SpindleDocs, which the API creates and migrates when it starts. The pictures
 * go to artifacts/docs-screenshots, or to DOCS_SCREENSHOTS (the README's to artifacts/readme-screenshots); see
 * screenshots/README.md. POSTGRES_USER overrides the database user, for a local PostgreSQL made before the rename.
 */

// The local PostgreSQL's password: from the environment, or from infra/.env of the main checkout (a worktree has none
// of its own; scripts/local-db.sh writes it there).
function postgresPassword(): string {
  if (process.env.POSTGRES_PASSWORD) return process.env.POSTGRES_PASSWORD
  const main = dirname(execSync('git rev-parse --path-format=absolute --git-common-dir', { encoding: 'utf8' }).trim())
  const file = join(main, 'infra/.env')
  const match = existsSync(file) ? /^POSTGRES_PASSWORD=(.*)$/m.exec(readFileSync(file, 'utf8')) : null
  if (!match) throw new Error('Set POSTGRES_PASSWORD, or run scripts/local-db.sh first.')
  return match[1].trim()
}

const [api, web] = base.webServer as [object, object]

export default defineConfig({
  ...base,
  testDir: './screenshots',
  testMatch: '*.screenshots.ts',
  fullyParallel: false,
  retries: 0,
  use: {
    ...base.use,
    ...devices['Desktop Chrome'],
    viewport: { width: 1280, height: 820 },
    deviceScaleFactor: 2,
    colorScheme: 'light',
    locale: 'en-GB',
    timezoneId: 'Europe/Zagreb',
    // Date fields follow the computer's region, not the page's language, so on a Mac set to another region they show
    // its format (e.g. 01.08.2026.). Change the region to English (UK) for the run if that matters.
  },
  projects: [{ name: 'chromium' }],
  webServer: [
    {
      ...api,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: 'http://localhost:5258',
        ConnectionStrings__Default: `Host=localhost;Port=15432;Username=${process.env.POSTGRES_USER ?? 'spindle'};Password=${postgresPassword()};Database=SpindleDocs`,
        Database__MigrateOnStartup: 'true',
      },
      reuseExistingServer: false,
    },
    web,
  ],
})
