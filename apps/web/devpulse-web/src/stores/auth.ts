import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { authApi } from '@/api/client'
import type { AuthResponse, OrganizationDto, Role } from '@/types/api'

const MANAGER_ROLES: Role[] = ['Owner', 'Admin', 'DevOps']
const RESPONDER_ROLES: Role[] = ['Owner', 'Admin', 'DevOps', 'Developer']
const TEAM_ROLES: Role[] = ['Owner', 'Admin']

interface Session {
  accessToken: string
  email: string
  userId: string
  organization: OrganizationDto
  expiresAt: number
}

/**
 * Le jeton d'accès ne vit qu'en mémoire : un script injecté ne le retrouve dans aucun stockage, et il disparaît avec
 * l'onglet. La session survit au rechargement grâce au cookie de renouvellement (HttpOnly), échangé au démarrage.
 */
export const useAuthStore = defineStore('auth', () => {
  const session = ref<Session | null>(null)
  let restoring: Promise<boolean> | null = null

  const isAuthenticated = computed(() => session.value !== null)
  const role = computed(() => session.value?.organization.role)
  const canManageServices = computed(() => MANAGER_ROLES.includes(role.value as Role))
  const canManageAlerts = canManageServices
  const canRespond = computed(() => RESPONDER_ROLES.includes(role.value as Role))
  const canManageTeam = computed(() => TEAM_ROLES.includes(role.value as Role))

  function open(response: AuthResponse) {
    session.value = {
      accessToken: response.accessToken,
      email: response.email,
      userId: response.userId,
      organization: response.organization,
      expiresAt: Date.now() + response.expiresIn * 1000,
    }
  }

  function close() {
    session.value = null
  }

  /** Échange le cookie contre un jeton d'accès. Plusieurs appels simultanés partagent la même requête. */
  function renew(): Promise<boolean> {
    restoring ??= authApi
      .refresh()
      .then((response) => {
        open(response)
        return true
      })
      .catch(() => {
        close()
        return false
      })
      .finally(() => (restoring = null))
    return restoring
  }

  async function logout() {
    try {
      await authApi.logout()
    } catch {
      // Hors ligne : le cookie expirera de lui-même ; la session locale est fermée quand même.
    }
    close()
  }

  return { session, isAuthenticated, canManageServices, canManageAlerts, canRespond, canManageTeam, open, close, renew, logout }
})
