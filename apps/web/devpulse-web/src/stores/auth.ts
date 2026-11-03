import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type { AuthResponse, OrganizationDto } from '@/types/api'

const STORAGE_KEY = 'devpulse.session'
const MANAGER_ROLES = ['Owner', 'Admin', 'DevOps']

interface Session {
  accessToken: string
  email: string
  organization: OrganizationDto
  expiresAt: number
}

function readStoredSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY)
    if (!raw) return null
    const session = JSON.parse(raw) as Session
    return session.expiresAt > Date.now() ? session : null
  } catch {
    return null
  }
}

/** Le jeton vit dans sessionStorage : il disparaît avec l'onglet. Il expire au bout de 15 minutes (pas encore de renouvellement). */
export const useAuthStore = defineStore('auth', () => {
  const session = ref<Session | null>(readStoredSession())

  const isAuthenticated = computed(() => session.value !== null && session.value.expiresAt > Date.now())
  const canManageServices = computed(() => MANAGER_ROLES.includes(session.value?.organization.role ?? ''))

  function open(response: AuthResponse) {
    session.value = {
      accessToken: response.accessToken,
      email: response.email,
      organization: response.organization,
      expiresAt: Date.now() + response.expiresIn * 1000,
    }
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session.value))
    } catch {
      // Stockage indisponible : la session reste valable tant que la page est ouverte.
    }
  }

  function close() {
    session.value = null
    try {
      sessionStorage.removeItem(STORAGE_KEY)
    } catch {
      // Rien à nettoyer.
    }
  }

  return { session, isAuthenticated, canManageServices, open, close }
})
