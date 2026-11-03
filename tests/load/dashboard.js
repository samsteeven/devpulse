// Charge de lecture sur la passerelle : 20 utilisateurs simultanés pendant 30 s, tableau de bord et catalogue.
// Mesure le chemin complet passerelle → service → PostgreSQL, sur le poste ou le runner qui l'exécute.
//   docker run --rm --network host -v "$PWD/tests/load:/scripts" grafana/k6:1.8.1 run /scripts/dashboard.js
import { check } from 'k6'
import http from 'k6/http'

const BASE = __ENV.GATEWAY_URL || 'http://localhost:8080'
const TARGET = __ENV.DEMO_TARGET_URL || 'http://demo:8080'
const json = { 'Content-Type': 'application/json' }

export const options = {
  scenarios: { lecture: { executor: 'constant-vus', vus: 20, duration: '30s' } },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{endpoint:overview}': ['p(95)<300'],
    'http_req_duration{endpoint:services}': ['p(95)<300'],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
}

export function setup() {
  const email = `charge-${Date.now()}@example.test`
  const registered = http.post(`${BASE}/api/auth/register`,
    JSON.stringify({ email, password: 'un-mot-de-passe-solide', organizationName: 'Essai de charge' }), { headers: json })
  check(registered, { 'inscription': (r) => r.status === 201 })
  const token = registered.json('accessToken')
  const auth = { ...json, Authorization: `Bearer ${token}` }

  // Quelques services, pour que la vue d'ensemble ait un contenu réaliste.
  for (let i = 0; i < 10; i++) {
    http.post(`${BASE}/api/services`, JSON.stringify({
      name: `service-${i}`, environment: 'Production', baseUrl: TARGET, healthCheckPath: '/health',
      checkIntervalSeconds: 30, timeoutSeconds: 5,
    }), { headers: auth })
  }
  return { token }
}

export default function (data) {
  const params = { headers: { Authorization: `Bearer ${data.token}` } }
  check(http.get(`${BASE}/api/dashboard/overview`, { ...params, tags: { endpoint: 'overview' } }), { 'vue d\'ensemble': (r) => r.status === 200 })
  check(http.get(`${BASE}/api/services`, { ...params, tags: { endpoint: 'services' } }), { 'catalogue': (r) => r.status === 200 })
}
