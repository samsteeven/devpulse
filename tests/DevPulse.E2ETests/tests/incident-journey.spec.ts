import { createHmac } from 'node:crypto'
import { expect, test, type APIRequestContext, type Page } from '@playwright/test'

const DEMO = process.env.DEMO_URL ?? 'http://localhost:5020'
const DEMO_TARGET = process.env.DEMO_TARGET_URL ?? DEMO
const PASSWORD = 'un-mot-de-passe-solide'
// Objectif du cahier des charges : l'alerte part moins de 10 s après la panne confirmée.
const NOTIFICATION_BUDGET_MS = 10_000

const unique = () => `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`

interface ReceivedWebhook {
  receivedAt: string
  event: string
  signature: string
  timestamp: string
  body: string
}

async function setDemoMode(request: APIRequestContext, mode: 'ok' | 'slow' | 'down') {
  expect((await request.post(`${DEMO}/mode/${mode}`)).ok()).toBe(true)
}

async function register(page: Page, organization: string) {
  await page.goto('/register')
  await page.getByLabel("Nom de l'organisation").fill(organization)
  await page.getByLabel('Adresse e-mail').fill(`e2e-${unique()}@example.test`)
  await page.getByLabel('Mot de passe').fill(PASSWORD)
  await page.getByRole('button', { name: 'Créer mon organisation' }).click()
  await expect(page).toHaveURL(/\/services$/)
}

async function webhooks(request: APIRequestContext, inbox: string): Promise<ReceivedWebhook[]> {
  return (await request.get(`${DEMO}/webhooks/${inbox}`)).json()
}

test.afterEach(async ({ request }) => {
  await setDemoMode(request, 'ok')
})

test("une panne ouvre un incident, prévient le webhook en moins de 10 s, puis se ferme seule", async ({ page, request }) => {
  test.setTimeout(300_000)
  await setDemoMode(request, 'ok')
  const inbox = `e2e-${unique()}`
  const name = `Billing API ${unique()}`

  await register(page, `Astreinte ${unique()}`)

  // Canal d'alerte vers la boîte de réception de l'API de démonstration.
  await page.getByRole('link', { name: 'Alertes' }).click()
  await page.getByRole('button', { name: 'Ajouter un canal' }).click()
  await page.getByLabel('Nom du canal').fill('Astreinte')
  await page.getByLabel('URL de destination').fill(`${DEMO_TARGET}/webhooks/${inbox}`)
  await page.getByRole('button', { name: 'Enregistrer' }).click()
  const secret = (await page.getByTestId('signing-secret').locator('code').first().textContent())!.trim()
  expect(secret).toMatch(/^[0-9a-f]{64}$/)

  // Le canal fonctionne avant toute panne : bouton d'essai.
  await page.getByRole('button', { name: 'Envoyer un essai' }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Reçu (HTTP 200)' })).toBeVisible()

  await page.getByRole('link', { name: 'Services' }).click()
  await page.getByRole('button', { name: 'Ajouter un service' }).click()
  await page.getByLabel('Nom', { exact: true }).fill(name)
  await page.getByLabel('URL de base').fill(DEMO_TARGET)
  await page.getByLabel('Intervalle').fill('30')
  await page.getByLabel("Délai d'attente").fill('5')
  await page.getByRole('button', { name: 'Enregistrer' }).click()

  await page.getByRole('link', { name: 'Tableau de bord' }).click()
  const row = page.getByTestId('service-row').filter({ hasText: name })
  await expect(row.locator('[data-status="Operational"]')).toBeVisible({ timeout: 60_000 })
  await expect(page.getByTestId('realtime')).toHaveAttribute('data-state', 'live')

  // Deux échecs consécutifs confirment la panne : au plus deux intervalles.
  await setDemoMode(request, 'down')
  await expect(page.getByTestId('active-incidents')).toContainText(name, { timeout: 120_000 })
  await expect(page.getByTestId('toast').filter({ hasText: 'Incident ouvert' })).toBeVisible()

  await expect.poll(async () => (await webhooks(request, inbox)).some((w) => w.event === 'IncidentCreated'), { timeout: 30_000 }).toBe(true)
  const created = (await webhooks(request, inbox)).find((w) => w.event === 'IncidentCreated')!
  const payload = JSON.parse(created.body)
  const latency = Date.parse(created.receivedAt) - Date.parse(payload.occurredAt)
  console.log(`Délai de notification mesuré : ${latency} ms (panne confirmée → webhook reçu)`)
  expect(latency).toBeGreaterThanOrEqual(0)
  expect(latency).toBeLessThan(NOTIFICATION_BUDGET_MS)

  // Le destinataire peut vérifier que le message vient bien de DevPulse et n'a pas été modifié.
  const expected = 'sha256=' + createHmac('sha256', secret).update(`${created.timestamp}.${created.body}`).digest('hex')
  expect(created.signature).toBe(expected)

  await page.getByTestId('active-incidents').getByRole('link', { name: new RegExp(name) }).click()
  await expect(page.getByTestId('analysis')).toContainText('Proposée par les règles locales', { timeout: 30_000 })
  await expect(page.getByTestId('analysis')).toContainText('HTTP 503')
  await page.getByRole('button', { name: 'Prendre en charge' }).click()
  await expect(page.getByTestId('incident-badges')).toContainText('Pris en charge')
  await page.getByLabel('Note').fill('Redémarrage du pool en cours')
  await page.getByRole('button', { name: 'Ajouter' }).click()
  await expect(page.getByTestId('timeline')).toContainText('Redémarrage du pool en cours')

  // Retour à la normale : l'incident se ferme sans intervention, et le webhook le dit.
  await setDemoMode(request, 'ok')
  await expect(page.getByTestId('incident-badges')).toContainText('Résolu', { timeout: 90_000 })
  await expect(page.getByTestId('timeline').locator('[data-kind="Recovered"]')).toBeVisible()
  await page.screenshot({ path: '../../docs/screenshots/incident-detail.png', fullPage: true })
  await expect.poll(async () => (await webhooks(request, inbox)).some((w) => w.event === 'IncidentResolved'), { timeout: 30_000 }).toBe(true)
})

test("une invitation fait entrer un collègue avec le rôle prévu", async ({ page, browser }) => {
  const organization = `Équipe ${unique()}`
  await register(page, organization)

  await page.getByRole('link', { name: 'Équipe' }).click()
  const colleague = `collegue-${unique()}@example.test`
  await page.getByLabel('Adresse e-mail').fill(colleague)
  await page.getByLabel('Rôle de la personne invitée').selectOption('Viewer')
  await page.getByRole('button', { name: 'Créer le lien' }).click()
  const link = (await page.getByTestId('invitation-link').locator('code').textContent())!.trim()

  const other = await browser.newContext()
  const guest = await other.newPage()
  await guest.goto(link)
  await expect(guest.getByText(organization)).toBeVisible()
  await guest.getByLabel('Choisissez un mot de passe').fill(PASSWORD)
  await guest.getByRole('button', { name: 'Créer mon compte et rejoindre' }).click()
  await expect(guest.getByTestId('organization')).toHaveText(organization)

  // Observateur : il voit les services mais ne peut pas en ajouter.
  await guest.getByRole('link', { name: 'Services' }).click()
  await expect(guest.getByRole('heading', { name: 'Services' })).toBeVisible()
  await expect(guest.getByRole('button', { name: 'Ajouter un service' })).toHaveCount(0)

  // Le lien ne sert qu'une fois.
  const third = await browser.newContext()
  const replay = await third.newPage()
  await replay.goto(link)
  await expect(replay.getByRole('alert')).toContainText("n'est pas valable")

  await page.reload()
  await expect(page.getByTestId('member-row').filter({ hasText: colleague })).toContainText('Observateur')
  await other.close()
  await third.close()
})

test("la session survit à un rechargement de page sans jeton stocké dans le navigateur", async ({ page }) => {
  await register(page, `Rechargement ${unique()}`)
  const stored = await page.evaluate(() => JSON.stringify({ ...sessionStorage, ...localStorage }))
  expect(stored).not.toMatch(/eyJ[A-Za-z0-9_-]+\./) // aucun JWT

  await page.reload()
  await expect(page.getByTestId('organization')).toBeVisible()
  await page.getByRole('button', { name: 'Se déconnecter' }).click()
  await expect(page).toHaveURL(/\/login$/)
  // Le cookie a été révoqué côté serveur : un rechargement ne rouvre pas la session.
  await page.goto('/services')
  await expect(page).toHaveURL(/\/login/)
})
