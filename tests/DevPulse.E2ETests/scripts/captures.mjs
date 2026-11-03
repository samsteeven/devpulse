// Captures d'écran de présentation (README, portfolio), avec des données réalistes.
// Prérequis : plateforme démarrée (docker compose up -d --wait ; infrastructure/scripts/dev.ps1 ; front sur 5173).
//   npm run captures                     → docs/screenshots/vitrine-*.png
// Environ 5 minutes : il faut laisser le temps à deux pannes d'être confirmées, puis à la supervision d'accumuler des points.
import { chromium } from '@playwright/test'
import { mkdir } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'

const WEB = process.env.BASE_URL ?? 'http://localhost:5173'
const GATEWAY = process.env.GATEWAY_URL ?? 'http://localhost:8080'
const DEMO = process.env.DEMO_URL ?? 'http://localhost:5020'
const DEMO_TARGET = process.env.DEMO_TARGET_URL ?? DEMO
const GRAFANA = process.env.GRAFANA_URL ?? 'http://localhost:3000'
const OUT = fileURLToPath(new URL('../../../docs/screenshots/', import.meta.url))

const suffix = Date.now().toString(36).slice(-4)
const account = { email: `samen+${suffix}@atelier-nova.example`, password: 'demo-atelier-nova-2026', organizationName: 'Atelier Nova' }
const json = { 'Content-Type': 'application/json' }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const log = (m) => console.log(`[${new Date().toLocaleTimeString('fr-FR')}] ${m}`)

async function api(path, { method = 'GET', body, token } = {}) {
  const response = await fetch(`${GATEWAY}${path}`, {
    method,
    headers: { ...json, ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  })
  if (!response.ok) throw new Error(`${method} ${path} : HTTP ${response.status} ${await response.text()}`)
  return response.status === 204 ? null : response.json()
}

async function until(label, check, timeoutMs = 180_000) {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const value = await check()
    if (value) return value
    if (Date.now() > deadline) throw new Error(`Délai dépassé : ${label}`)
    await sleep(2000)
  }
}

const setMode = (mode) => fetch(`${DEMO}/mode/${mode}`, { method: 'POST' })

// --- Données ----------------------------------------------------------------------------------------------------
await setMode('ok')
const session = await api('/api/auth/register', { method: 'POST', body: account })
const token = session.accessToken
log(`Organisation « ${account.organizationName} » créée`)

const services = [
  { name: 'Payment API', environment: 'Production', path: '/health' },          // suit le mode global : tombera
  { name: 'Catalogue API', environment: 'Production', path: '/status/ok' },
  { name: 'Search API', environment: 'Production', path: '/status/slow' },      // dégradé
  { name: 'Notifications API', environment: 'Production', path: '/status/ok' },
  { name: 'Auth API', environment: 'Staging', path: '/status/ok' },
  { name: 'Reporting API', environment: 'Development', path: '/status/ok' },
]
for (const s of services) {
  await api('/api/services', {
    method: 'POST', token,
    body: { name: s.name, environment: s.environment, baseUrl: DEMO_TARGET, healthCheckPath: s.path, checkIntervalSeconds: 30, timeoutSeconds: 5 },
  })
}
await api('/api/alert-channels', {
  method: 'POST', token,
  body: { name: 'Astreinte production', kind: 'Webhook', url: `${DEMO_TARGET}/webhooks/astreinte-${suffix}`,
          events: ['IncidentCreated', 'IncidentAcknowledged', 'IncidentResolved'], environments: ['Production'] },
})
log(`${services.length} services et un canal d'alerte enregistrés`)

const overview = () => api('/api/dashboard/overview', { token })
await until('premières vérifications', async () => (await overview()).services.filter((s) => s.status !== 'Unknown').length === services.length)

// --- Une première panne, résolue : historique, durée de résolution, envois ---------------------------------------
const activeIncident = async () => (await api('/api/incidents?status=Active', { token }))[0]
await setMode('down')
log('Payment API en panne (1/2)')
let incident = await until('premier incident', activeIncident)
await setMode('ok')
await until('fermeture automatique', async () => !(await activeIncident()))
log('Premier incident résolu')

// --- La panne en cours, prise en charge ----------------------------------------------------------------------------
await setMode('down')
log('Payment API en panne (2/2)')
incident = await until('second incident', activeIncident)
await until('analyse', async () => (await api(`/api/incidents/${incident.id}`, { token })).analysis)
await api(`/api/incidents/${incident.id}/acknowledge`, { method: 'POST', token })
await api(`/api/incidents/${incident.id}/notes`, {
  method: 'POST', token, body: { text: 'Le fournisseur de paiement renvoie 503 depuis 10 h 42 ; bascule sur le fournisseur de secours en cours.' },
})
await until('envois d\'alertes', async () => (await api('/api/alert-deliveries', { token })).filter((d) => d.status === 'Sent').length >= 4)
log('Incident pris en charge et annoté')

// --- Captures --------------------------------------------------------------------------------------------------
await mkdir(OUT, { recursive: true })
const browser = await chromium.launch()
const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, locale: 'fr-FR' })
const page = await context.newPage()

await page.goto(`${WEB}/login`)
await page.getByLabel('Adresse e-mail').fill(account.email)
await page.getByLabel('Mot de passe').fill(account.password)
await page.getByRole('button', { name: 'Se connecter' }).click()
await page.waitForURL(`${WEB}/`)
await page.getByTestId('active-incidents').waitFor()
await page.locator('[data-status="Degraded"]').first().waitFor()
await sleep(1500)
await page.screenshot({ path: OUT + 'vitrine-tableau-de-bord.png' })
log('Capture : tableau de bord')

await page.goto(`${WEB}/incidents/${incident.id}`)
await page.getByTestId('analysis').getByText('Causes probables').waitFor()
await sleep(1000)
await page.screenshot({ path: OUT + 'vitrine-incident.png', fullPage: true })
log('Capture : incident')

await page.goto(`${WEB}/alerts`)
await page.getByTestId('delivery-row').first().waitFor()
await sleep(1000)
await page.screenshot({ path: OUT + 'vitrine-alertes.png', fullPage: true })
log('Capture : alertes')

// Grafana : le tableau de bord de la plateforme, sur les 20 dernières minutes, sans l'habillage de l'interface.
const grafana = await browser.newContext({ viewport: { width: 1600, height: 1000 }, deviceScaleFactor: 1 })
const g = await grafana.newPage()
// Un panneau en erreur ne dit rien dans la capture : on journalise la réponse des requêtes en échec.
g.on('response', async (response) => {
  if (!response.url().includes('/api/ds/query')) return
  const body = await response.text().catch(() => '')
  if (response.status() >= 400 || body.includes('"error"')) log(`Grafana ${response.status()} : ${body.slice(0, 600)}`)
})
await g.goto(`${GRAFANA}/d/devpulse?orgId=1&from=now-20m&to=now&theme=dark&kiosk`)
if (g.url().includes('/login')) {
  await g.getByLabel(/user/i).fill('admin')
  await g.getByLabel(/password/i).fill('admin')
  await g.getByRole('button', { name: /log in/i }).click()
  await g.goto(`${GRAFANA}/d/devpulse?orgId=1&from=now-20m&to=now&theme=dark&kiosk`)
}
await g.getByText('Vérifications par minute').waitFor()
await sleep(8000) // rendu des panneaux

// Diagnostic : ce que Grafana voit réellement, si un panneau est en erreur.
const diagnostic = await g.evaluate(async () => {
  const sources = await fetch('/api/datasources').then(async (r) => `${r.status} ${(await r.text()).slice(0, 400)}`)
  const query = await fetch('/api/ds/query', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ from: 'now-20m', to: 'now', queries: [{ refId: 'A', datasource: { type: 'prometheus', uid: 'prometheus' },
      expr: 'sum(rate(devpulse_checks_total[5m])) * 60', instant: true, range: false }] }),
  }).then(async (r) => `${r.status} ${(await r.text()).slice(0, 600)}`)
  return { sources, query }
})
log(`Grafana sources : ${diagnostic.sources}`)
log(`Grafana requête : ${diagnostic.query}`)
const errorIcon = g.locator('[data-testid*="status"] svg, [aria-label*="rror"]').first()
if (await errorIcon.count()) {
  await errorIcon.hover().catch(() => {})
  await sleep(1000)
  log(`Grafana infobulle : ${(await g.locator('[role="tooltip"]').allInnerTexts().catch(() => [])).join(' | ').slice(0, 400)}`)
}
await g.screenshot({ path: OUT + 'vitrine-grafana.png' })
log('Capture : Grafana')

await browser.close()
await setMode('ok')
log(`Terminé : ${OUT}`)
