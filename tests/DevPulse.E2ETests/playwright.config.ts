import { defineConfig, devices } from '@playwright/test'

// En local : docker compose up -d, puis infrastructure/scripts/dev.ps1 ; le front est démarré ici.
// Contre les images (CI) : BASE_URL désigne le front déjà servi, aucun serveur n'est lancé.
const baseURL = process.env.BASE_URL ?? 'http://localhost:5173'

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    ...devices['Desktop Chrome'],
  },
  webServer: process.env.BASE_URL
    ? undefined
    : {
        command: 'npm run dev',
        cwd: '../../apps/web/devpulse-web',
        url: baseURL,
        reuseExistingServer: true,
        timeout: 30_000,
      },
})
