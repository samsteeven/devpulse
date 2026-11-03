import { defineConfig, devices } from '@playwright/test'

// Prérequis : docker compose up -d, puis infrastructure/scripts/dev.ps1. Le front est démarré ici.
export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    ...devices['Desktop Chrome'],
  },
  webServer: {
    command: 'npm run dev',
    cwd: '../../apps/web/devpulse-web',
    url: 'http://localhost:5173',
    reuseExistingServer: true,
    timeout: 30_000,
  },
})
