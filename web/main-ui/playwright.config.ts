import { defineConfig, devices } from '@playwright/test'

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  timeout: 30_000,
  use: { baseURL: process.env.IZ_E2E_URL ?? 'http://127.0.0.1:8099', trace: 'retain-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 1000 } } },
    { name: 'mobile', use: { ...devices['iPhone 13'], defaultBrowserType: 'chromium' } },
  ],
})