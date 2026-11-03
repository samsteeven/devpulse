export type HealthStatus = 'Unknown' | 'Operational' | 'Degraded' | 'Down'
export type ServiceEnvironment = 'Production' | 'Staging' | 'Development'

export interface OrganizationDto {
  id: string
  name: string
  slug: string
  role: string
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
