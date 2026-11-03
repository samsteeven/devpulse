import axios, { AxiosError } from 'axios'
import type {
  AuthResponse,
  Overview,
  ServiceHealth,
  ServiceMetrics,
  ServiceRequest,
  ServiceResponse,
} from '@/types/api'

export const http = axios.create({ baseURL: '/api', timeout: 15000 })

interface ProblemDetails {
  title?: string
  errors?: Record<string, string[]>
}

/** Message lisible à partir d'une réponse Problem Details, sans jamais afficher une erreur technique brute. */
export function errorMessage(error: unknown): string {
  if (error instanceof AxiosError) {
    if (error.response?.status === 429) return 'Trop de tentatives. Réessayez dans une minute.'

    const problem = error.response?.data as ProblemDetails | undefined
    const details = Object.values(problem?.errors ?? {}).flat()
    if (details.length > 0) return details.join(' ')
    if (problem?.title) return problem.title

    if (!error.response) return 'Le service ne répond pas. Vérifiez votre connexion.'
  }
  return 'Une erreur inattendue est survenue.'
}

export function isNotFound(error: unknown): boolean {
  return error instanceof AxiosError && error.response?.status === 404
}

export const authApi = {
  login: (email: string, password: string) =>
    http.post<AuthResponse>('/auth/login', { email, password }).then((r) => r.data),
  register: (email: string, password: string, organizationName: string) =>
    http.post<AuthResponse>('/auth/register', { email, password, organizationName }).then((r) => r.data),
}

export const servicesApi = {
  list: () => http.get<ServiceResponse[]>('/services').then((r) => r.data),
  get: (id: string) => http.get<ServiceResponse>(`/services/${id}`).then((r) => r.data),
  create: (request: ServiceRequest) => http.post<ServiceResponse>('/services', request).then((r) => r.data),
  remove: (id: string) => http.delete(`/services/${id}`),
}

export const monitoringApi = {
  overview: () => http.get<Overview>('/dashboard/overview').then((r) => r.data),
  health: (id: string) => http.get<ServiceHealth>(`/services/${id}/health`).then((r) => r.data),
  metrics: (id: string, hours: number) =>
    http.get<ServiceMetrics>(`/services/${id}/metrics`, { params: { hours } }).then((r) => r.data),
}
