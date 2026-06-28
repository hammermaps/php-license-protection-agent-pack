import { useAuthStore } from './stores/auth'

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const auth = useAuthStore()
  const res = await fetch(path, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${auth.apiKey}`,
      ...(init?.headers ?? {}),
    },
  })
  if (!res.ok) {
    const body = await res.json().catch(() => ({}))
    throw new Error(body?.error?.message ?? `HTTP ${res.status}`)
  }
  if (res.status === 204) return undefined as T
  return res.json()
}

// ── Stats ────────────────────────────────────────────────────────────

export interface StatsDto {
  licenses: { total: number; active: number; revoked: number; suspended: number }
  builds: { total: number; signed: number; revoked: number }
  activations: { total: number; active: number; revoked: number }
  leases: { issued24h: number }
  database: string
}
export const fetchStats = () => request<StatsDto>('/api/v1/admin/stats')

// ── Licenses ──────────────────────────────────────────────────────────

export interface LicenseDto {
  licenseId: string
  licenseKey: string
  customerId: string
  projectId: string
  status: string
  validUntil: string | null
  maxActivations: number
  createdAt: string
}
export const fetchLicenses = (status?: string) =>
  request<{ licenses: LicenseDto[] }>('/api/v1/admin/licenses' + (status ? `?status=${status}` : ''))
    .then(r => r.licenses)

export const revokeLicense = (licenseId: string, reason?: string) =>
  request(`/api/v1/admin/licenses/${licenseId}/revoke`, {
    method: 'POST',
    body: JSON.stringify({ reason: reason ?? null }),
  })

// ── Activations ───────────────────────────────────────────────────────

export interface ActivationDto {
  activationId: string
  licenseId: string
  machineFingerprint: string
  status: string
  firstSeenAt: string
  lastSeenAt: string
}
export const fetchActivations = (licenseId?: string) =>
  request<{ activations: ActivationDto[] }>('/api/v1/admin/activations' + (licenseId ? `?licenseUid=${licenseId}` : ''))
    .then(r => r.activations)

export const revokeActivation = (id: string) =>
  request(`/api/v1/admin/activations/${id}/revoke`, { method: 'POST', body: '{}' })

export const deleteActivation = (id: string) =>
  request(`/api/v1/admin/activations/${id}`, { method: 'DELETE' })

// ── Audit log ─────────────────────────────────────────────────────────

export interface AuditEventDto {
  eventId: string
  actorType: string
  eventType: string
  entityType: string | null
  entityUid: string | null
  ipAddress: string | null
  details: string | null
  createdAt: string
}
export const fetchAuditLog = (params?: Record<string, string>) =>
  request<{ events: AuditEventDto[] }>(
    '/api/v1/admin/audit-log' + (params ? '?' + new URLSearchParams(params) : '')
  ).then(r => r.events)

// ── API clients ───────────────────────────────────────────────────────

export interface ApiClientDto {
  clientUid: string
  name: string
  scope: string
  isActive: boolean
  createdAt: string
}
export interface ApiClientCreateResponse {
  clientUid: string
  apiKey: string
  name: string
  scope: string
  createdAt: string
}
export const fetchApiClients = () =>
  request<{ clients: ApiClientDto[] }>('/api/v1/admin/api-clients')
    .then(r => r.clients)

export const createApiClient = (name: string, scope: string) =>
  request<ApiClientCreateResponse>('/api/v1/admin/api-clients', {
    method: 'POST',
    body: JSON.stringify({ name, scope }),
  })

export const deleteApiClient = (clientUid: string) =>
  request(`/api/v1/admin/api-clients/${clientUid}`, { method: 'DELETE' })

// ── Telemetry ─────────────────────────────────────────────────────────

export interface TelemetryDto {
  id: number
  source: string
  eventType: string
  licenseId: string | null
  buildId: string | null
  projectId: string | null
  occurredAt: string
  payloadJson: string | null
}
export const fetchTelemetry = (params?: Record<string, string>) =>
  request<{ events: TelemetryDto[] }>(
    '/api/v1/admin/telemetry' + (params ? '?' + new URLSearchParams(params) : '')
  ).then(r => r.events)

// ── Error reports ─────────────────────────────────────────────────────

export interface ErrorReportDto {
  id: number
  licenseId: string
  buildId: string | null
  reportedAt: string
  errorLevel: number
  errorMessage: string
  errorFile: string | null
  errorLine: number | null
  phpVersion: string | null
  sapi: string | null
  machineFingerprint: string | null
}
export const fetchErrorReports = (params?: Record<string, string>) =>
  request<{ reports: ErrorReportDto[] }>(
    '/api/v1/admin/error-reports' + (params ? '?' + new URLSearchParams(params) : '')
  ).then(r => r.reports)

// ── Health check (used on login) ──────────────────────────────────────

export const checkHealth = (apiKey: string) =>
  fetch('/api/v1/admin/stats', {
    headers: { Authorization: `Bearer ${apiKey}` },
  })
