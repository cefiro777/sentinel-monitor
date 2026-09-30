// Тонкий клиент API сервера. Токен хранится в localStorage; 401 → выход.

export type CheckStatus = 'ok' | 'warning' | 'critical' | 'unknown'
export type UserRole = 'admin' | 'engineer' | 'readOnly' | 'clientViewer'

export interface User { id: string; email: string; displayName: string; role: UserRole; tenantId?: string; isActive: boolean; totpEnabled: boolean; mfa: boolean }
export interface LoginResponse { token?: string; expiresAt?: string; user?: User; totpChallenge?: string }
export interface AuditEntry { id: number; at: string; userName?: string; agentId?: string; tenantName?: string; action: string; targetType?: string; targetId?: string; details?: string; remoteIp?: string }
export interface Tenant { id: string; name: string; slug: string; notes?: string; isActive: boolean; hostCount: number; agentsOnline: number; worstStatus: CheckStatus }
export interface AgentInfo { id: string; isOnline: boolean; lastSeenAt?: string; agentVersion: string; osVersion: string; reportedConfigVersion: number; configVersion: number; queueDepth: number }
export interface Host { id: string; tenantId: string; tenantName: string; name: string; kind: string; address?: string; description?: string; agent?: AgentInfo; worstStatus: CheckStatus; checksTotal: number; checksProblem: number; checksCritical: number; checksWarning: number; checksInProgress: number }
export interface CheckState { key: string; status: CheckStatus; summary: string; details?: unknown; lastResultAt?: string; lastChangeAt?: string; pendingStatus?: CheckStatus; pendingCount: number; confirmCount: number; problemSince?: string }
export interface Check { id: string; moduleId: string; name: string; enabled: boolean; intervalSeconds: number; confirmCount: number; settings: Record<string, unknown>; states: CheckState[]; ignoredKeys: string[]; alertDelayMinutes: number }
export interface MetricSeries { checkId: string; key: string; name: string; points: [number, number][] }
export interface EventHint { source: string; ids: number[]; title: string; cause: string; fix: string }
export type IncidentStatus = 'open' | 'acknowledged' | 'resolved' | 'inProgress'
export type IncidentKind = 'checkState' | 'agentOffline' | 'backupOverdue'
export interface Incident { id: string; tenantId: string; tenantName: string; hostId: string; hostName: string; checkId?: string; key: string; kind: IncidentKind; severity: CheckStatus; status: IncidentStatus; title: string; summary: string; openedAt: string; acknowledgedAt?: string; resolvedAt?: string; updatedAt: string; snoozedUntil?: string }
export interface IncidentEvent { at: string; type: string; message: string; userName?: string }
export interface IncidentSummary { open: number; acknowledged: number; critical: number; resolvedToday: number }
export type ChannelType = 'email' | 'telegram' | 'max'
export interface Channel { id: string; name: string; type: ChannelType; settings: Record<string, unknown>; isEnabled: boolean; lastUsedAt?: string; lastError?: string }
export interface Route { id: string; channelId: string; channelName: string; tenantId?: string; tenantName?: string; minSeverity: CheckStatus; notifyOnResolve: boolean; quietFromHour?: number; quietToHour?: number; remindMinutes: number; isEnabled: boolean }
export interface AgentPackage { version: string; file: string; sizeBytes: number; sha256: string; publishedAt: string }
export interface TemplateItem { moduleId: string; name: string; enabled: boolean; intervalSeconds: number; confirmCount: number; settings: Record<string, unknown>; ignoredKeys?: string[]; alertDelayMinutes?: number }
export interface CheckTemplate { id: string; tenantId?: string; tenantName?: string; name: string; description: string; items: TemplateItem[]; updatedAt: string }
export interface ServicePreset { id: string; title: string; description: string; services: { name: string; autoRestart: boolean; critical: boolean; comment: string }[] }
export interface ApplyResult { hostId: string; hostName: string; created: number; updated: number; skipped: number }
export interface MaintenanceWindow { id: string; tenantId?: string; tenantName?: string; hostId?: string; hostName?: string; startsAt: string; endsAt: string; comment: string; active: boolean }
export interface NotificationLogEntry { id: number; incidentId: string; incidentTitle: string; channelId: string; channelName: string; kind: string; at: string; success: boolean; error?: string }
export interface BackupRun { id: number; kind: string; target: string; startedAt: string; finishedAt: string; success: boolean; artifactPath?: string; sizeBytes: number; verified: boolean; cloudEnabled: boolean; cloudUploaded: boolean; cloudPath?: string; error?: string; log?: string }
export interface BackupJob { checkId: string; hostId: string; hostName: string; tenantId: string; tenantName: string; name: string; moduleId: string; enabled: boolean; scheduleText: string; target: string; destination: string; cloudFolder?: string; cloudEnabled: boolean; agentOnline: boolean; lastRun?: BackupRun; lastSuccessAt?: string; lastCloudAt?: string; overdue: boolean }
export interface CctvDevice { checkId: string; moduleId: string; checkName: string; hostId: string; hostName: string; tenantId: string; tenantName: string; agentOnline: boolean; key: string; name: string; status: CheckStatus; summary: string; details?: unknown; lastResultAt?: string; lastChangeAt?: string }
export interface Command { id: string; type: string; status: 'pending' | 'sent' | 'succeeded' | 'failed' | 'expired'; issuedAt: string; finishedAt?: string; output?: string; error?: string }
export interface EnrollmentToken { id: string; tenantId: string; hostId?: string; expiresAt: string; usedAt?: string; comment?: string }
export interface EnrollmentTokenCreated { id: string; token: string; expiresAt: string; installHint: string; serverUrl: string; certificatePin?: string }

const TOKEN_KEY = 'sentinel.token'
const USER_KEY = 'sentinel.user'

export const auth = {
  get token(): string | null { try { return localStorage.getItem(TOKEN_KEY) } catch { return null } },
  get user(): User | null { try { const u = localStorage.getItem(USER_KEY); return u ? JSON.parse(u) : null } catch { return null } },
  set(token: string, user: User) { try { localStorage.setItem(TOKEN_KEY, token); localStorage.setItem(USER_KEY, JSON.stringify(user)) } catch { /* private mode */ } },
  clear() { try { localStorage.removeItem(TOKEN_KEY); localStorage.removeItem(USER_KEY) } catch { /* ignore */ } },
  canOperate(): boolean { const r = auth.user?.role; return r === 'admin' || r === 'engineer' },
  isAdmin(): boolean { return auth.user?.role === 'admin' },
  hasMfa(): boolean { return !!auth.user?.mfa },
}

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message) }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  const token = auth.token
  if (token) headers.Authorization = `Bearer ${token}`
  const res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (res.status === 401 && !path.endsWith('/auth/login')) {
    auth.clear()
    window.location.href = '/login'
    throw new ApiError(401, 'Сессия истекла')
  }
  if (!res.ok) {
    let msg = `${res.status} ${res.statusText}`
    try { const j = await res.json(); msg = j.error ?? j.title ?? msg } catch { /* not json */ }
    throw new ApiError(res.status, msg)
  }
  if (res.status === 204) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const api = {
  login: (email: string, password: string) => request<LoginResponse>('POST', '/api/auth/login', { email, password }),
  totpVerify: (challenge: string, code: string) => request<LoginResponse>('POST', '/api/auth/totp/verify', { challenge, code }),
  totpSetup: () => request<{ secret: string; otpauthUrl: string }>('POST', '/api/auth/totp/setup'),
  totpEnable: (code: string) => request<LoginResponse>('POST', '/api/auth/totp/enable', { code }),
  totpDisable: (code: string) => request<LoginResponse>('POST', '/api/auth/totp/disable', { code }),
  totpReset: (userId: string) => request<void>('POST', `/api/users/${userId}/totp/reset`),
  me: () => request<User>('GET', '/api/auth/me'),
  audit: (action?: string) => request<AuditEntry[]>('GET', '/api/audit' + (action ? '?action=' + action : '')),

  tenants: () => request<Tenant[]>('GET', '/api/tenants'),
  createTenant: (name: string, slug: string, notes?: string) => request<Tenant>('POST', '/api/tenants', { name, slug, notes }),
  deleteTenant: (id: string, confirm: string) => request<{ deletedHosts: number }>('DELETE', `/api/tenants/${id}`, { confirm }),

  hosts: (tenantId?: string) => request<Host[]>('GET', tenantId ? `/api/hosts?tenantId=${tenantId}` : '/api/hosts'),
  host: (id: string) => request<Host>('GET', `/api/hosts/${id}`),
  deleteHost: (id: string) => request<void>('DELETE', `/api/hosts/${id}`),

  checks: (hostId: string) => request<Check[]>('GET', `/api/hosts/${hostId}/checks`),
  metrics: (hostId: string, hours = 24) => request<MetricSeries[]>('GET', `/api/hosts/${hostId}/metrics?hours=${hours}`),
  createCheck: (hostId: string, c: Omit<Check, 'id' | 'states' | 'ignoredKeys' | 'alertDelayMinutes'> & { ignoredKeys?: string[]; alertDelayMinutes?: number }) => request<Check>('POST', `/api/hosts/${hostId}/checks`, c),
  updateCheck: (hostId: string, id: string, c: Omit<Check, 'id' | 'states' | 'ignoredKeys' | 'alertDelayMinutes'> & { ignoredKeys?: string[]; alertDelayMinutes?: number }) => request<Check>('PUT', `/api/hosts/${hostId}/checks/${id}`, c),
  deleteCheck: (hostId: string, id: string) => request<void>('DELETE', `/api/hosts/${hostId}/checks/${id}`),
  templates: () => request<CheckTemplate[]>('GET', '/api/check-templates'),
  createTemplate: (t: Omit<CheckTemplate, 'id' | 'tenantName' | 'updatedAt'>) => request<CheckTemplate>('POST', '/api/check-templates', t),
  createTemplateFromHost: (hostId: string, name: string, tenantId?: string, description?: string) => request<CheckTemplate>('POST', '/api/check-templates/from-host', { hostId, tenantId, name, description }),
  updateTemplate: (id: string, t: Omit<CheckTemplate, 'id' | 'tenantName' | 'updatedAt'>) => request<CheckTemplate>('PUT', `/api/check-templates/${id}`, t),
  deleteTemplate: (id: string) => request<void>('DELETE', `/api/check-templates/${id}`),
  eventHints: () => request<EventHint[]>('GET', '/api/event-hints'),
  servicePresets: () => request<ServicePreset[]>('GET', '/api/service-presets'),
  applyTemplate: (id: string, hostIds: string[], overwrite: boolean) => request<ApplyResult[]>('POST', `/api/check-templates/${id}/apply`, { hostIds, overwrite }),

  commands: (hostId: string) => request<Command[]>('GET', `/api/hosts/${hostId}/commands`),
  issueCommand: (hostId: string, type: string, payload?: unknown) => request<Command>('POST', `/api/hosts/${hostId}/commands`, { type, payload }),

  enrollmentTokens: (tenantId?: string) => request<EnrollmentToken[]>('GET', tenantId ? `/api/enrollment-tokens?tenantId=${tenantId}` : '/api/enrollment-tokens'),
  createEnrollmentToken: (tenantId: string, comment?: string, ttlHours?: number) => request<EnrollmentTokenCreated>('POST', '/api/enrollment-tokens', { tenantId, comment, ttlHours }),
  revokeEnrollmentToken: (id: string) => request<void>('DELETE', `/api/enrollment-tokens/${id}`),

  incidents: (p: { status?: string; hostId?: string; tenantId?: string; take?: number } = {}) => {
    const q = new URLSearchParams(); Object.entries(p).forEach(([k, v]) => v !== undefined && q.set(k, String(v)))
    return request<Incident[]>('GET', '/api/incidents?' + q.toString())
  },
  incidentSummary: () => request<IncidentSummary>('GET', '/api/incidents/summary'),
  incident: (id: string) => request<{ incident: Incident; events: IncidentEvent[] }>('GET', `/api/incidents/${id}`),
  ackIncident: (id: string, comment?: string) => request<void>('POST', `/api/incidents/${id}/ack`, { comment }),
  incidentInProgress: (id: string, hours: number, comment?: string) => request<void>('POST', `/api/incidents/${id}/in-progress`, { hours, comment }),
  resolveIncident: (id: string, comment?: string) => request<void>('POST', `/api/incidents/${id}/resolve`, { comment }),
  commentIncident: (id: string, comment: string) => request<void>('POST', `/api/incidents/${id}/comment`, { comment }),

  channels: () => request<Channel[]>('GET', '/api/notifications/channels'),
  createChannel: (c: Omit<Channel, 'id' | 'lastUsedAt' | 'lastError'>) => request<Channel>('POST', '/api/notifications/channels', c),
  updateChannel: (id: string, c: Omit<Channel, 'id' | 'lastUsedAt' | 'lastError'>) => request<Channel>('PUT', `/api/notifications/channels/${id}`, c),
  deleteChannel: (id: string) => request<void>('DELETE', `/api/notifications/channels/${id}`),
  testChannel: (id: string) => request<{ ok: boolean }>('POST', `/api/notifications/channels/${id}/test`),
  routes: () => request<Route[]>('GET', '/api/notifications/routes'),
  createRoute: (r: Omit<Route, 'id' | 'channelName' | 'tenantName'>) => request<Route>('POST', '/api/notifications/routes', r),
  updateRoute: (id: string, r: Omit<Route, 'id' | 'channelName' | 'tenantName'>) => request<Route>('PUT', `/api/notifications/routes/${id}`, r),
  deleteRoute: (id: string) => request<void>('DELETE', `/api/notifications/routes/${id}`),
  notificationLog: () => request<NotificationLogEntry[]>('GET', '/api/notifications/log'),

  backups: () => request<BackupJob[]>('GET', '/api/backups'),
  backupRuns: (checkId: string) => request<BackupRun[]>('GET', `/api/backups/${checkId}/runs`),
  runBackup: (checkId: string) => request<{ id: string; status: string }>('POST', `/api/backups/${checkId}/run`),

  cctv: () => request<CctvDevice[]>('GET', '/api/cctv'),
  setCctvChannels: (checkId: string, key: string, rules: { ignoreChannels: number[]; motionChannels: number[] }) => request<{ applied: boolean }>('PUT', `/api/cctv/${checkId}/${encodeURIComponent(key)}/channels`, rules),

  agentPackage: () => request<AgentPackage | null>('GET', '/api/agent-package'),
  uploadAgentPackage: async (file: File) => {
    const fd = new FormData(); fd.append('file', file)
    const res = await fetch('/api/agent-package', { method: 'POST', headers: { Authorization: `Bearer ${auth.token}` }, body: fd })
    if (!res.ok) { let msg = `${res.status}`; try { msg = (await res.json()).error ?? msg } catch { /* */ } throw new ApiError(res.status, msg) }
    return (await res.json()) as AgentPackage
  },
  maintenance: (hostId?: string) => request<MaintenanceWindow[]>('GET', '/api/maintenance' + (hostId ? `?hostId=${hostId}` : '')),
  createMaintenance: (w: { tenantId?: string; hostId?: string; startsAt: string; endsAt: string; comment?: string }) => request<{ id: string }>('POST', '/api/maintenance', w),
  deleteMaintenance: (id: string) => request<void>('DELETE', `/api/maintenance/${id}`),

  users: () => request<User[]>('GET', '/api/users'),
  createUser: (u: { email: string; displayName: string; password: string; role: UserRole; tenantId?: string }) => request<User>('POST', '/api/users', u),
}

export const statusOrder: Record<CheckStatus, number> = { ok: 0, warning: 1, critical: 2, unknown: 3 }
export const statusLabel: Record<CheckStatus, string> = { ok: 'OK', warning: 'Внимание', critical: 'Критично', unknown: 'Неизвестно' }

export function timeAgo(iso?: string): string {
  if (!iso) return '—'
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000)
  if (s < 60) return `${Math.round(s)} с назад`
  if (s < 3600) return `${Math.round(s / 60)} мин назад`
  if (s < 86400) return `${Math.round(s / 3600)} ч назад`
  return `${Math.round(s / 86400)} д назад`
}
