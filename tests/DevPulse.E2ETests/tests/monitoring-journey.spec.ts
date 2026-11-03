import { expect, test, type APIRequestContext, type Page } from '@playwright/test'

const DEMO = 'http://localhost:5020'
const GATEWAY = 'http://localhost:8080'
const PASSWORD = 'un-mot-de-passe-solide'
// Intervalle minimal de 30 s, plus la réservation et le rafraîchissement de la page (5 s).
const NEXT_CHECK = 50_000

const unique = () => `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`

async function setDemoMode(request: APIRequestContext, mode: 'ok' | 'slow' | 'down') {
  expect((await request.post(`${DEMO}/mode/${mode}`)).ok()).toBe(true)
}

async function register(page: Page, organization: string, email: string) {
  await page.goto('/register')
  await page.getByLabel("Nom de l'organisation").fill(organization)
  await page.getByLabel('Adresse e-mail').fill(email)
  await page.getByLabel('Mot de passe').fill(PASSWORD)
  await page.getByRole('button', { name: 'Créer mon organisation' }).click()
  await expect(page).toHaveURL(/\/services$/)
  await expect(page.getByTestId('organization')).toHaveText(organization)
}

async function addService(page: Page, name: string) {
  await page.getByRole('button', { name: 'Ajouter un service' }).click()
  await page.getByLabel('Nom', { exact: true }).fill(name)
  await page.getByLabel('URL de base').fill(DEMO)
  await page.getByLabel('Intervalle').fill('30')
  await page.getByLabel("Délai d'attente").fill('5')
  await page.getByRole('button', { name: 'Enregistrer' }).click()
  await expect(page.getByTestId('service-row').filter({ hasText: name })).toBeVisible()
}

const accessToken = (page: Page) =>
  page.evaluate(() => JSON.parse(sessionStorage.getItem('devpulse.session')!).accessToken as string)

test.beforeEach(async ({ request }) => {
  await setDemoMode(request, 'ok')
})

test.afterEach(async ({ request }) => {
  await setDemoMode(request, 'ok')
})

test("enregistrer une API, la voir tomber puis revenir, à travers toute la chaîne", async ({ page, request }) => {
  test.setTimeout(240_000)

  const name = `Payment API ${unique()}`
  await register(page, `Atelier ${unique()}`, `e2e-${unique()}@example.test`)
  await addService(page, name)

  await page.getByRole('link', { name: 'Tableau de bord' }).click()
  const row = page.getByTestId('service-row').filter({ hasText: name })

  // Registry → synchronisation → réservation → sonde → PostgreSQL → API Monitoring → passerelle → navigateur.
  await expect(row.locator('[data-status="Operational"]')).toBeVisible({ timeout: 30_000 })
  await expect(page.getByTestId('tile-operational')).toHaveText('1')

  await setDemoMode(request, 'slow')
  await expect(row.locator('[data-status="Degraded"]')).toBeVisible({ timeout: NEXT_CHECK })
  await expect(page.getByTestId('tile-degraded')).toHaveText('1')

  await setDemoMode(request, 'down')
  await expect(row.locator('[data-status="Down"]')).toBeVisible({ timeout: NEXT_CHECK })
  await expect(page.getByTestId('tile-down')).toHaveText('1')
  await expect(row).toContainText('Code HTTP inattendu (503)')
  await expect(row).toContainText('1 échec(s) consécutif(s)')
  await page.screenshot({ path: '../../docs/screenshots/dashboard-panne.png', fullPage: true })

  await setDemoMode(request, 'ok')
  await expect(row.locator('[data-status="Operational"]')).toBeVisible({ timeout: NEXT_CHECK })
  await expect(row).not.toContainText('échec(s)')

  await row.getByRole('link', { name }).click()
  await expect(page.getByTestId('service-status')).toHaveAttribute('data-status', 'Operational')
  await expect(page.getByTestId('checks')).not.toHaveText('0', { timeout: 20_000 })

  // 4 vérifications dont 1 échec : la disponibilité est calculée sur de vraies mesures, elle n'est plus à 100 %.
  const checks = Number(await page.getByTestId('checks').textContent())
  expect(checks).toBeGreaterThanOrEqual(4)
  await expect(page.getByTestId('uptime')).not.toHaveText('100 %')
  await expect(page.locator('canvas')).toBeVisible()
  await page.screenshot({ path: '../../docs/screenshots/service-detail.png', fullPage: true })
})

test("une organisation ne voit rien de ce qui appartient à une autre", async ({ page, browser, request }) => {
  const name = `Secret API ${unique()}`
  await register(page, `Alice ${unique()}`, `alice-${unique()}@example.test`)
  await addService(page, name)
  await page.getByTestId('service-row').filter({ hasText: name }).getByRole('link', { name }).click()
  await expect(page).toHaveURL(/\/services\/[0-9a-f-]{36}$/)
  const serviceId = page.url().split('/').pop()!

  const bobContext = await browser.newContext()
  const bob = await bobContext.newPage()
  await register(bob, `Bob ${unique()}`, `bob-${unique()}@example.test`)

  await expect(bob.getByText('Aucun service enregistré')).toBeVisible()
  await bob.goto(`/services/${serviceId}`)
  await expect(bob.getByRole('alert')).toHaveText("Ce service n'existe pas.")
  await expect(bob.getByText(name)).toHaveCount(0)

  // La même chose directement sur la passerelle, sans passer par l'interface.
  const headers = { Authorization: `Bearer ${await accessToken(bob)}` }
  for (const path of ['', '/health', '/metrics']) {
    expect((await request.get(`${GATEWAY}/api/services/${serviceId}${path}`, { headers })).status()).toBe(404)
  }
  expect((await request.delete(`${GATEWAY}/api/services/${serviceId}`, { headers })).status()).toBe(404)

  await bobContext.close()
})

test("la connexion refuse un mauvais mot de passe et protège les pages", async ({ page, request }) => {
  const email = `carol-${unique()}@example.test`
  await register(page, `Carol ${unique()}`, email)
  await page.getByRole('button', { name: 'Se déconnecter' }).click()
  await expect(page).toHaveURL(/\/login$/)

  await page.goto('/services')
  await expect(page).toHaveURL(/\/login\?redirect=/)

  await page.getByLabel('Adresse e-mail').fill(email)
  await page.getByLabel('Mot de passe').fill('pas-le-bon-mot-de-passe')
  await page.getByRole('button', { name: 'Se connecter' }).click()
  await expect(page.getByRole('alert')).toHaveText('Adresse e-mail ou mot de passe incorrect.')

  await page.getByLabel('Mot de passe').fill(PASSWORD)
  await page.getByRole('button', { name: 'Se connecter' }).click()
  // Le chemin complet : « /login?redirect=/services » finit aussi par « /services ».
  await expect(page).toHaveURL('/services')
  await expect(page.getByTestId('organization')).toBeVisible()

  // La route interne de Registry n'existe pas derrière la passerelle : sans jeton, 401 comme toute route ; avec jeton, 404.
  expect((await request.get(`${GATEWAY}/internal/services/active`)).status()).toBe(401)
  const headers = { Authorization: `Bearer ${await accessToken(page)}` }
  expect((await request.get(`${GATEWAY}/internal/services/active`, { headers })).status()).toBe(404)
})

test("une URL refusée par Registry affiche la raison dans le formulaire", async ({ page }) => {
  await register(page, `Dora ${unique()}`, `dora-${unique()}@example.test`)
  await page.getByRole('button', { name: 'Ajouter un service' }).click()
  await page.getByLabel('Nom', { exact: true }).fill('Mauvais protocole')
  await page.getByLabel('URL de base').fill('ftp://api.example.com')
  await page.getByRole('button', { name: 'Enregistrer' }).click()

  await expect(page.getByRole('alert')).toHaveText('Seuls http et https sont acceptés.')
})
