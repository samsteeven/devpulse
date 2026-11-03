import axios, { AxiosError, type InternalAxiosRequestConfig } from 'axios'
import type {
  AgentCreated,
  AgentResponse,
  AuthResponse,
  ChannelRequest,
  ChannelResponse,
  DeliveryResponse,
  IncidentDetails,
  IncidentStats,
  IncidentSummary,
  InvitationCreated,
  InvitationPreview,
  InvitationResponse,
  MemberResponse,
  OrganizationDto,
  Overview,
  Role,
  ServiceHealth,
  ServiceMetrics,
  ServiceRequest,
  ServiceResponse,
  TestResult,
} from '@/types/api'

export const http = axios.create({ baseURL: '/api', timeout: 15000 })

/** En-tête exigé par les routes qui lisent le cookie de session : seul un script de la même origine peut le poser. */
const SESSION_HEADERS = { 'X-Requested-With': 'DevPulse' }

interface ProblemDetails {
  title?: string
  errors?: Record<string, string[]>
}

/** Message lisible à partir d'une réponse Problem Details, sans jamais afficher une erreur technique brute. */
export function errorMessage(error: unknown): string {
  if (error instanceof AxiosError) {
    if (error.response?.status === 429) return 'Trop de tentatives. Réessayez dans une minute.'
    if (error.response?.status === 403) return "Votre rôle ne permet pas cette action."

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

/**
 * Sur un 401, un seul renouvellement est tenté pour toutes les requêtes en attente, puis elles sont rejouées.
 * Les routes d'authentification ne déclenchent jamais de renouvellement : un 401 y veut dire « refusé ».
 */
export function installSessionRenewal(options: {
  token: () => string | null
  renew: () => Promise<boolean>
  expired: () => void
}) {
  let pending: Promise<boolean> | null = null

  http.interceptors.request.use((config) => {
    const token = options.token()
    if (token) config.headers.Authorization = `Bearer ${token}`
    return config
  })

  http.interceptors.response.use(undefined, async (error: unknown) => {
    if (!(error instanceof AxiosError) || error.response?.status !== 401 || !error.config) throw error
    const config = error.config as InternalAxiosRequestConfig & { _retried?: boolean }
    if (config.url?.startsWith('/auth/') || config._retried) throw error

    pending ??= options.renew().finally(() => (pending = null))
    if (!(await pending)) {
      options.expired()
      throw error
    }
    config._retried = true
    return http.request(config)
  })
}

export const authApi = {
  login: (email: string, password: string) => http.post<AuthResponse>('/auth/login', { email, password }).then((r) => r.data),
  register: (email: string, password: string, organizationName: string) =>
    http.post<AuthResponse>('/auth/register', { email, password, organizationName }).then((r) => r.data),
  refresh: () => http.post<AuthResponse>('/auth/refresh', null, { headers: SESSION_HEADERS }).then((r) => r.data),
  logout: () => http.post('/auth/logout', null, { headers: SESSION_HEADERS }),
  organizations: () => http.get<OrganizationDto[]>('/auth/organizations').then((r) => r.data),
  switch: (organizationId: string) => http.post<AuthResponse>('/auth/switch', { organizationId }).then((r) => r.data),
  previewInvitation: (token: string) => http.post<InvitationPreview>('/auth/invitations/preview', { token }).then((r) => r.data),
  acceptInvitation: (token: string, password: string) =>
    http.post<AuthResponse>('/auth/invitations/accept', { token, password }).then((r) => r.data),
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
  metrics: (id: string, hours: number) => http.get<ServiceMetrics>(`/services/${id}/metrics`, { params: { hours } }).then((r) => r.data),
}

export const incidentsApi = {
  list: (status: 'Active' | 'Resolved' | 'All', serviceId?: string) =>
    http.get<IncidentSummary[]>('/incidents', { params: { status, serviceId } }).then((r) => r.data),
  get: (id: string) => http.get<IncidentDetails>(`/incidents/${id}`).then((r) => r.data),
  stats: () => http.get<IncidentStats>('/incidents/stats').then((r) => r.data),
  acknowledge: (id: string) => http.post<IncidentDetails>(`/incidents/${id}/acknowledge`).then((r) => r.data),
  resolve: (id: string, note: string) => http.post<IncidentDetails>(`/incidents/${id}/resolve`, { note }).then((r) => r.data),
  addNote: (id: string, text: string) => http.post<IncidentDetails>(`/incidents/${id}/notes`, { text }).then((r) => r.data),
}

export const alertsApi = {
  channels: () => http.get<ChannelResponse[]>('/alert-channels').then((r) => r.data),
  create: (request: ChannelRequest) => http.post<ChannelResponse>('/alert-channels', request).then((r) => r.data),
  update: (id: string, request: ChannelRequest) => http.put<ChannelResponse>(`/alert-channels/${id}`, request).then((r) => r.data),
  remove: (id: string) => http.delete(`/alert-channels/${id}`),
  test: (id: string) => http.post<TestResult>(`/alert-channels/${id}/test`).then((r) => r.data),
  deliveries: () => http.get<DeliveryResponse[]>('/alert-deliveries', { params: { limit: 50 } }).then((r) => r.data),
}

export const teamApi = {
  members: () => http.get<MemberResponse[]>('/organization/members').then((r) => r.data),
  changeRole: (userId: string, role: Role) => http.put<MemberResponse>(`/organization/members/${userId}`, { role }).then((r) => r.data),
  remove: (userId: string) => http.delete(`/organization/members/${userId}`),
  invitations: () => http.get<InvitationResponse[]>('/organization/invitations').then((r) => r.data),
  invite: (email: string, role: Role) => http.post<InvitationCreated>('/organization/invitations', { email, role }).then((r) => r.data),
  revoke: (id: string) => http.delete(`/organization/invitations/${id}`),
}

export const agentsApi = {
  list: () => http.get<AgentResponse[]>('/agents').then((r) => r.data),
  register: (name: string, environment: string) => http.post<AgentCreated>('/agents', { name, environment }).then((r) => r.data),
  revoke: (id: string) => http.delete(`/agents/${id}`),
}
