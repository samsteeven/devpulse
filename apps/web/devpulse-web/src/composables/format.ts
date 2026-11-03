import type { AlertEvent, HealthStatus, IncidentSeverity, IncidentStatus, Role } from '@/types/api'

export const STATUS_LABELS: Record<HealthStatus, string> = {
  Operational: 'Opérationnel',
  Degraded: 'Dégradé',
  Down: 'En panne',
  Unknown: 'En attente',
}

export const SEVERITY_LABELS: Record<IncidentSeverity, string> = {
  Critical: 'Critique',
  Major: 'Majeur',
  Minor: 'Mineur',
}

export const INCIDENT_STATUS_LABELS: Record<IncidentStatus, string> = {
  Open: 'Ouvert',
  Acknowledged: 'Pris en charge',
  Resolved: 'Résolu',
}

export const ROLE_LABELS: Record<Role, string> = {
  Owner: 'Propriétaire',
  Admin: 'Administrateur',
  DevOps: 'DevOps',
  Developer: 'Développeur',
  Viewer: 'Observateur',
}

export const EVENT_LABELS: Record<AlertEvent, string> = {
  IncidentCreated: 'Incident ouvert',
  IncidentAcknowledged: 'Incident pris en charge',
  IncidentResolved: 'Incident résolu',
}

const FAILURE_LABELS: Record<string, string> = {
  BlockedTarget: 'Cible refusée (adresse interne ou non autorisée)',
  Timeout: 'Délai dépassé',
  ConnectionError: 'Connexion impossible',
  TlsError: 'Erreur de certificat TLS',
  Redirect: 'Redirection non suivie',
  UnexpectedStatus: 'Code HTTP inattendu',
  AgentSilent: "L'agent ne donne plus de nouvelles",
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

/** Durée d'un incident : précise au début, arrondie ensuite. */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined) return '–'
  if (seconds < 60) return `${seconds} s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ${String(seconds % 60).padStart(2, '0')} s`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')} min`
  return `${Math.floor(seconds / 86400)} j ${Math.floor((seconds % 86400) / 3600)} h`
}

export function formatDateTime(iso: string | null): string {
  if (!iso) return '–'
  return new Date(iso).toLocaleString('fr-FR', { dateStyle: 'short', timeStyle: 'medium' })
}
