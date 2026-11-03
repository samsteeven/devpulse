import { AxiosError, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { http, installSessionRenewal } from '@/api/client'

/** Faux serveur : répond 401 tant que le jeton n'est pas « frais ». */
function server(validToken: () => string) {
  const calls: string[] = []
  const adapter: AxiosAdapter = async (config: InternalAxiosRequestConfig) => {
    calls.push(`${config.url} ${String(config.headers.Authorization ?? '')}`)
    const ok = config.headers.Authorization === `Bearer ${validToken()}`
    const response = { data: ok ? { ok: true } : {}, status: ok ? 200 : 401, statusText: '', headers: {}, config }
    if (ok) return response
    throw new AxiosError('401', 'ERR_BAD_REQUEST', config, null, response)
  }
  return { adapter, calls }
}

describe('installSessionRenewal', () => {
  let token: string
  beforeEach(() => {
    token = 'expiré'
    http.interceptors.request.clear()
    http.interceptors.response.clear()
  })

  it('renouvelle une seule fois pour plusieurs requêtes refusées, puis les rejoue', async () => {
    const { adapter, calls } = server(() => 'frais')
    http.defaults.adapter = adapter
    const renew = vi.fn(async () => {
      token = 'frais'
      return true
    })
    installSessionRenewal({ token: () => token, renew, expired: vi.fn() })

    const results = await Promise.all([http.get('/services'), http.get('/incidents'), http.get('/agents')])

    expect(results.every((r) => r.status === 200)).toBe(true)
    expect(renew).toHaveBeenCalledTimes(1)
    expect(calls.filter((c) => c.endsWith('frais'))).toHaveLength(3)
  })

  it('signale la fin de session si le renouvellement échoue, sans boucler', async () => {
    const { adapter, calls } = server(() => 'jamais')
    http.defaults.adapter = adapter
    const expired = vi.fn()
    installSessionRenewal({ token: () => token, renew: async () => false, expired })

    await expect(http.get('/services')).rejects.toBeInstanceOf(AxiosError)
    expect(expired).toHaveBeenCalledTimes(1)
    expect(calls).toHaveLength(1)
  })

  it("ne renouvelle jamais sur les routes d'authentification : un 401 y veut dire « refusé »", async () => {
    const { adapter } = server(() => 'jamais')
    http.defaults.adapter = adapter
    const renew = vi.fn(async () => true)
    installSessionRenewal({ token: () => null, renew, expired: vi.fn() })

    await expect(http.post('/auth/login', {})).rejects.toBeInstanceOf(AxiosError)
    expect(renew).not.toHaveBeenCalled()
  })

  it('ne rejoue une requête qu’une fois', async () => {
    const { adapter, calls } = server(() => 'jamais')
    http.defaults.adapter = adapter
    installSessionRenewal({ token: () => token, renew: async () => true, expired: vi.fn() })

    await expect(http.get('/services')).rejects.toBeInstanceOf(AxiosError)
    expect(calls).toHaveLength(2)
  })
})
