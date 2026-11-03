import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { authApi } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import type { AuthResponse, Role } from '@/types/api'

const response = (role: Role, token = 'jeton'): AuthResponse => ({
  accessToken: token,
  expiresIn: 900,
  userId: 'u1',
  email: 'moi@example.test',
  organization: { id: 'o1', name: 'Atelier Nova', slug: 'atelier-nova', role },
})

describe('useAuthStore', () => {
  beforeEach(() => {
    sessionStorage.clear()
    localStorage.clear()
    setActivePinia(createPinia())
    vi.restoreAllMocks()
  })

  it("garde le jeton en mémoire seulement : rien dans le stockage du navigateur", () => {
    const auth = useAuthStore()
    auth.open(response('Owner'))

    expect(auth.isAuthenticated).toBe(true)
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('jeton')
    expect(JSON.stringify({ ...localStorage })).not.toContain('jeton')
  })

  it('reprend la session à partir du cookie de renouvellement', async () => {
    vi.spyOn(authApi, 'refresh').mockResolvedValue(response('Admin', 'nouveau'))
    const auth = useAuthStore()

    expect(await auth.renew()).toBe(true)
    expect(auth.session?.accessToken).toBe('nouveau')
    expect(auth.session?.organization.role).toBe('Admin')
  })

  it('ferme la session si le renouvellement est refusé', async () => {
    vi.spyOn(authApi, 'refresh').mockRejectedValue(new Error('401'))
    const auth = useAuthStore()
    auth.open(response('Owner'))

    expect(await auth.renew()).toBe(false)
    expect(auth.isAuthenticated).toBe(false)
  })

  it('partage un seul renouvellement entre des appels simultanés', async () => {
    const refresh = vi.spyOn(authApi, 'refresh').mockResolvedValue(response('Owner'))
    const auth = useAuthStore()

    await Promise.all([auth.renew(), auth.renew(), auth.renew()])
    expect(refresh).toHaveBeenCalledTimes(1)
  })

  it('ferme la session locale même si la déconnexion échoue côté serveur', async () => {
    vi.spyOn(authApi, 'logout').mockRejectedValue(new Error('hors ligne'))
    const auth = useAuthStore()
    auth.open(response('Owner'))

    await auth.logout()
    expect(auth.isAuthenticated).toBe(false)
  })

  it.each<[Role, boolean, boolean, boolean]>([
    ['Owner', true, true, true],
    ['Admin', true, true, true],
    ['DevOps', true, true, false],
    ['Developer', false, true, false],
    ['Viewer', false, false, false],
  ])('le rôle %s : services %s, incidents %s, équipe %s', (role, services, incidents, team) => {
    const auth = useAuthStore()
    auth.open(response(role))
    expect(auth.canManageServices).toBe(services)
    expect(auth.canRespond).toBe(incidents)
    expect(auth.canManageTeam).toBe(team)
  })
})
