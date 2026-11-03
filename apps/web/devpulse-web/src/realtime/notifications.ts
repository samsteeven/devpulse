import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import type { QueryClient } from '@tanstack/vue-query'
import { readonly, ref } from 'vue'
import type { IncidentNotification, ServiceStatusNotification } from '@/types/api'

export type RealtimeState = 'off' | 'connecting' | 'live' | 'reconnecting'

export interface Toast {
  id: number
  tone: 'bad' | 'warn' | 'ok' | 'info'
  text: string
  link?: string
}

const state = ref<RealtimeState>('off')
const toasts = ref<Toast[]>([])
let connection: HubConnection | null = null
let nextToast = 1

function toast(tone: Toast['tone'], text: string, link?: string) {
  const id = nextToast++
  toasts.value = [...toasts.value.slice(-3), { id, tone, text, link }]
  setTimeout(() => dismiss(id), 8000)
}

export function dismiss(id: number) {
  toasts.value = toasts.value.filter((t) => t.id !== id)
}

const CHANGE_TEXT: Record<IncidentNotification['change'], (n: IncidentNotification) => string> = {
  created: (n) => `Incident ouvert : ${n.title}`,
  acknowledged: (n) => `Incident pris en charge : ${n.serviceName}`,
  resolved: (n) => `Incident résolu : ${n.serviceName}`,
  analyzed: (n) => `Analyse disponible : ${n.serviceName}`,
}

const CHANGE_TONE: Record<IncidentNotification['change'], Toast['tone']> = {
  created: 'bad',
  acknowledged: 'warn',
  resolved: 'ok',
  analyzed: 'info',
}

/**
 * Connexion au hub de l'organisation. Les messages ne transportent pas l'état complet : ils disent seulement quoi
 * rafraîchir. Si la connexion tombe, l'interrogation périodique des pages prend le relais.
 */
export function startRealtime(queries: QueryClient, token: () => Promise<string | null>) {
  if (connection) return

  connection = new HubConnectionBuilder()
    .withUrl('/hubs/notifications', { accessTokenFactory: async () => (await token()) ?? '' })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build()

  connection.on('serviceStatusChanged', (n: ServiceStatusNotification) => {
    void queries.invalidateQueries({ queryKey: ['overview'] })
    void queries.invalidateQueries({ queryKey: ['health', n.serviceId] })
    void queries.invalidateQueries({ queryKey: ['agents'] })
  })

  connection.on('incidentChanged', (n: IncidentNotification) => {
    void queries.invalidateQueries({ queryKey: ['incidents'] })
    void queries.invalidateQueries({ queryKey: ['incident', n.incidentId] })
    void queries.invalidateQueries({ queryKey: ['incident-stats'] })
    if (n.change === 'created' || n.change === 'resolved') void queries.invalidateQueries({ queryKey: ['deliveries'] })
    toast(CHANGE_TONE[n.change], CHANGE_TEXT[n.change](n), `/incidents/${n.incidentId}`)
  })

  connection.onreconnecting(() => (state.value = 'reconnecting'))
  connection.onreconnected(() => {
    state.value = 'live'
    // Des messages ont pu se perdre pendant la coupure : tout est relu.
    void queries.invalidateQueries()
  })
  connection.onclose(() => (state.value = 'off'))

  state.value = 'connecting'
  connection
    .start()
    .then(() => (state.value = 'live'))
    .catch(() => (state.value = 'off'))
}

export async function stopRealtime() {
  const current = connection
  connection = null
  toasts.value = []
  if (current && current.state !== HubConnectionState.Disconnected) await current.stop()
  state.value = 'off'
}

export const realtime = { state: readonly(state), toasts: readonly(toasts) }
