export type HealthStatus = 'Unknown' | 'Operational' | 'Degraded' | 'Down'
export type ServiceEnvironment = 'Production' | 'Staging' | 'Development'
export type Role = 'Owner' | 'Admin' | 'DevOps' | 'Developer' | 'Viewer'

export interface OrganizationDto {
  id: string
  name: string
  slug: string
  role: Role
}

export interface AuthResponse {
  accessToken: string
  expiresIn: number
  userId: string
  email: string
  organization: OrganizationDto
}

export interface ServiceRequest {
  name: string
  environment: ServiceEnvironment
  baseUrl: string
  healthCheckPath: string
  checkIntervalSeconds: number
  timeoutSeconds: number
}

export interface ServiceResponse extends ServiceRequest {
  id: string
  enabled: boolean
  createdAt: string
  updatedAt: string
}

export interface ServiceHealth {
  serviceId: string
  name: string
  environment: string
  status: HealthStatus
  consecutiveFailures: number
  lastCheckedAt: string | null
  lastResponseTimeMs: number | null
  lastHttpStatusCode: number | null
  lastFailure: string | null
}

export interface Overview {
  total: number
  operational: number
  degraded: number
  down: number
  unknown: number
  services: ServiceHealth[]
}

export interface MetricsPoint {
  bucketStart: string
  checks: number
  failures: number
  averageResponseTimeMs: number
}

export interface ServiceMetrics {
  serviceId: string
  hours: number
  checks: number
  uptimePercent: number | null
  averageResponseTimeMs: number | null
  p95ResponseTimeMs: number | null
  series: MetricsPoint[]
}

export type IncidentStatus = 'Open' | 'Acknowledged' | 'Resolved'
export type IncidentSeverity = 'Critical' | 'Major' | 'Minor'

export interface IncidentSummary {
  id: string
  serviceId: string
  serviceName: string
  environment: string
  title: string
  severity: IncidentSeverity
  status: IncidentStatus
  source: 'Probe' | 'Agent'
  failure: string
  detectedAt: string
  openedAt: string
  acknowledgedAt: string | null
  resolvedAt: string | null
  resolvedAutomatically: boolean
  durationSeconds: number
  hasAnalysis: boolean
}

export interface TimelineEntry {
  at: string
  kind: 'Detected' | 'Acknowledged' | 'Note' | 'Analysis' | 'Recovered' | 'Resolved'
  message: string
  author: string | null
}

export interface Analysis {
  provider: 'Rules' | 'Llm'
  model: string | null
  summary: string
  probableCauses: string[]
  suggestedActions: string[]
  generatedAt: string
}

export interface IncidentDetails {
  incident: IncidentSummary
  failureCount: number
  httpStatusCode: number | null
  responseTimeMs: number | null
  analysis: Analysis | null
  timeline: TimelineEntry[]
}

export interface IncidentStats {
  open: number
  acknowledged: number
  resolvedInPeriod: number
  meanTimeToResolveSeconds: number | null
  periodDays: number
}

export type ChannelKind = 'Webhook' | 'Slack'
export type AlertEvent = 'IncidentCreated' | 'IncidentAcknowledged' | 'IncidentResolved'

export interface ChannelRequest {
  name: string
  kind: ChannelKind
  url: string
  events: AlertEvent[]
  environments: ServiceEnvironment[]
  enabled?: boolean
}

export interface ChannelResponse {
  id: string
  name: string
  kind: ChannelKind
  url: string
  events: AlertEvent[]
  environments: ServiceEnvironment[]
  enabled: boolean
  createdAt: string
  signingSecret: string | null
}

export interface DeliveryResponse {
  id: string
  channelId: string
  channelName: string
  incidentId: string
  event: AlertEvent
  serviceName: string
  status: 'Pending' | 'Sent' | 'Failed'
  attempts: number
  lastStatusCode: number | null
  lastError: string | null
  occurredAt: string
  sentAt: string | null
  latencyMs: number | null
}

export interface TestResult {
  success: boolean
  statusCode: number | null
  error: string | null
}

export interface MemberResponse {
  userId: string
  email: string
  role: Role
  joinedAt: string
}

export interface InvitationResponse {
  id: string
  email: string
  role: Role
  createdAt: string
  expiresAt: string
}

export interface InvitationCreated {
  id: string
  email: string
  role: Role
  expiresAt: string
  token: string
}

export interface InvitationPreview {
  organizationName: string
  email: string
  role: Role
  accountExists: boolean
  expiresAt: string
}

export interface AgentResponse {
  id: string
  name: string
  environment: ServiceEnvironment
  status: HealthStatus
  lastSeenAt: string | null
  hostname: string | null
  operatingSystem: string | null
  agentVersion: string | null
  intervalSeconds: number
  cpuPercent: number | null
  memoryPercent: number | null
  diskPercent: number | null
  uptimeSeconds: number | null
  createdAt: string
  revoked: boolean
}

export interface AgentCreated {
  agent: AgentResponse
  key: string
}

/** Messages poussés par le hub temps réel. */
export interface ServiceStatusNotification {
  serviceId: string
  serviceName: string
  previousStatus: HealthStatus
  currentStatus: HealthStatus
  changedAt: string
}

export interface IncidentNotification {
  incidentId: string
  serviceId: string
  serviceName: string
  change: 'created' | 'acknowledged' | 'resolved' | 'analyzed'
  severity: IncidentSeverity
  title: string
  at: string
}
