import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '@/stores/auth'
import type { AuthResponse } from '@/types/api'

const response = (role: string, expiresIn = 900): AuthResponse => ({
  accessToken: 'jeton',
  expiresIn,
  userId: 'u1',
  email: 'moi@example.test',
  organization: { id: 'o1', name: 'Atelier Nova', slug: 'atelier-nova', role },
})

describe('useAuthStore', () => {
  beforeEach(() => {
    sessionStorage.clear()
    setActivePinia(createPinia())
    vi.useRealTimers()
  })

  it('ouvre puis ferme une session, y compris dans le stockage', () => {
    const auth = useAuthStore()
    auth.open(response('Owner'))
    expect(auth.isAuthenticated).toBe(true)
    expect(sessionStorage.getItem('devpulse.session')).toContain('jeton')

    auth.close()
    expect(auth.isAuthenticated).toBe(false)
    expect(sessionStorage.getItem('devpulse.session')).toBeNull()
  })

  it('retrouve la session après un rechargement de page', () => {
    useAuthStore().open(response('Owner'))

    setActivePinia(createPinia())
    expect(useAuthStore().session?.email).toBe('moi@example.test')
  })

  it('ignore une session expirée dans le stockage', () => {
    vi.useFakeTimers({ now: Date.parse('2026-11-03T12:00:00Z') })
    useAuthStore().open(response('Owner', 60))

    vi.setSystemTime(Date.parse('2026-11-03T12:02:00Z'))
    setActivePinia(createPinia())
    expect(useAuthStore().session).toBeNull()
  })

  it('ignore un contenu de stockage illisible', () => {
    sessionStorage.setItem('devpulse.session', '{pas du json')
    expect(useAuthStore().session).toBeNull()
  })

  it.each([
    ['Owner', true],
    ['Admin', true],
    ['DevOps', true],
    ['Developer', false],
    ['Viewer', false],
  ])('le rôle %s peut gérer les services : %s', (role, expected) => {
    const auth = useAuthStore()
    auth.open(response(role))
    expect(auth.canManageServices).toBe(expected)
  })
})
