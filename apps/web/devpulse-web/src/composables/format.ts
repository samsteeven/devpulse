import type { HealthStatus } from '@/types/api'

export const STATUS_LABELS: Record<HealthStatus, string> = {
  Operational: 'Opérationnel',
  Degraded: 'Dégradé',
  Down: 'En panne',
  Unknown: 'En attente',
}

const FAILURE_LABELS: Record<string, string> = {
  BlockedTarget: 'Cible refusée (adresse interne ou non autorisée)',
  Timeout: 'Délai dépassé',
  ConnectionError: 'Connexion impossible',
  TlsError: 'Erreur de certificat TLS',
  Redirect: 'Redirection non suivie',
  UnexpectedStatus: 'Code HTTP inattendu',
}

export function failureLabel(failure: string | null): string | null {
  return failure ? (FAILURE_LABELS[failure] ?? failure) : null
}

export function formatMs(value: number | null | undefined): string {
  return value === null || value === undefined ? '–' : `${value} ms`
}

export function formatPercent(value: number | null | undefined): string {
  return value === null || value === undefined ? '–' : `${value.toLocaleString('fr-FR', { maximumFractionDigits: 2 })} %`
}

export function formatRelative(iso: string | null, now: number = Date.now()): string {
  if (!iso) return 'jamais'
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000))
  if (seconds < 60) return `il y a ${seconds} s`
  if (seconds < 3600) return `il y a ${Math.floor(seconds / 60)} min`
  if (seconds < 86400) return `il y a ${Math.floor(seconds / 3600)} h`
  return `il y a ${Math.floor(seconds / 86400)} j`
}
