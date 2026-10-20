import { defineConfig, devices } from '@playwright/test'

/**
 * End-to-end tests in real browsers against the real API and database.
 * Needs the local database (../../scripts/local-db.sh); the API and the dev server are started automatically.
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'webkit', use: { ...devices['Desktop Safari'] } },
  ],
  webServer: [
    {
      // Built first, then run directly (exec): `dotnet run` would start the API as a child that outlives the tests,
      // and a later run would reuse it with old code.
      command: 'dotnet build -v quiet -nologo && exec dotnet bin/Debug/net10.0/Spindle.Api.dll',
      // The API's folder, where it finds its settings.
      cwd: '../Spindle.Api',
      env: { ASPNETCORE_ENVIRONMENT: 'Development', ASPNETCORE_URLS: 'http://localhost:5258' },
      url: 'http://localhost:5258/health',
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
    },
    {
      command: 'npm run dev',
      url: 'http://localhost:5173',
      reuseExistingServer: !process.env.CI,
    },
  ],
})
