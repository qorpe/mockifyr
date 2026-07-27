import { TENANT_HEADER } from '@/lib/tenants'
import type { EnvironmentKey } from '@/lib/environments'

// A stub row as the dashboard needs it — a flat projection of a mapping.
export type Protocol = 'http' | 'grpc' | 'graphql' | 'websocket'
export type StubStatus = 'live' | 'proxy' | 'draft'

export interface Stub {
  id: string
  name: string | null
  method: string
  url: string
  protocol: Protocol
  priority: number
  scenario: string | null
  persistence: string
  lastMatched: string | null
  status: StubStatus
  /** The HTTP response status code (from response.status), when the mapping declares one. */
  responseStatus: number | null
  /** The full mapping (when a host returned it), so the editor can round-trip an edit. */
  raw?: Record<string, unknown>
}

// Admin Basic-auth credentials (base64 of user:pass), stored locally when the host requires auth. The
// host only enforces this when started with --admin-user/--admin-pass; otherwise there is no login.
const AUTH_KEY = 'ui.adminAuth'
export const hasAdminAuth = () => !!localStorage.getItem(AUTH_KEY)
export const setAdminAuth = (user: string, pass: string) => localStorage.setItem(AUTH_KEY, btoa(`${user}:${pass}`))
export const clearAdminAuth = () => localStorage.removeItem(AUTH_KEY)

/**
 * Probes the admin surface with the given credentials and, when accepted, stores them. Used by the
 * login screen so it can show an inline error instead of silently re-triggering the gate. A network
 * error (no host reachable) is treated as "not an auth failure" — the credentials are stored and the
 * regular fetch path will surface any real 401 later.
 */
export async function verifyAdminAuth(user: string, pass: string): Promise<boolean> {
  const token = btoa(`${user}:${pass}`)
  try {
    const res = await fetch('/__admin/mappings', { headers: { Authorization: `Basic ${token}`, [TENANT_HEADER]: 'default' } })
    if (res.status === 401) return false
  } catch {
    // Unreachable host is not an authentication failure; fall through and store optimistically.
  }
  localStorage.setItem(AUTH_KEY, token)
  return true
}

/** Low-level admin fetch: scopes every call to the active tenant, and attaches admin auth when present. */
async function adminFetch(path: string, tenant: string, init?: RequestInit): Promise<Response> {
  const auth = localStorage.getItem(AUTH_KEY)
  const res = await fetch(`/__admin${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      [TENANT_HEADER]: tenant,
      ...(auth ? { Authorization: `Basic ${auth}` } : {}),
      ...init?.headers,
    },
  })
  // A 401 means the host requires admin auth; let the app surface the login screen.
  if (res.status === 401) window.dispatchEvent(new Event('mockifyr-auth-required'))
  return res
}

/**
 * Loads the tenant's stubs. Talks to GET /__admin/mappings when a host is reachable; when it isn't
 * (design-time / no server), it falls back to representative sample data so the dashboard is always
 * explorable. The `mock` flag lets the UI show a "sample data" hint.
 */
export async function fetchStubs(tenant: string): Promise<{ stubs: Stub[]; mock: boolean }> {
  try {
    const res = await adminFetch('/mappings', tenant)
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { mappings?: RawMapping[] }
    return { stubs: (body.mappings ?? []).map(projectMapping), mock: false }
  } catch {
    return { stubs: sampleStubs(tenant), mock: true }
  }
}

interface RawMapping {
  id?: string
  uuid?: string
  name?: string
  priority?: number
  scenarioName?: string
  /** Computed server-side per ADR 0010 (G18-pre); never part of the stored mapping. */
  protocol?: string
  request?: { method?: string; url?: string; urlPath?: string; urlPattern?: string; urlPathPattern?: string }
  response?: { proxyBaseUrl?: string; status?: number }
  metadata?: { 'mockifyr:persistence'?: string }
}

const isProtocol = (p: string | undefined): p is Protocol =>
  p === 'http' || p === 'grpc' || p === 'graphql' || p === 'websocket'

function projectMapping(m: RawMapping): Stub {
  const req = m.request ?? {}
  const url = req.url ?? req.urlPath ?? req.urlPattern ?? req.urlPathPattern ?? '/'
  return {
    id: m.id ?? m.uuid ?? crypto.randomUUID(),
    name: typeof m.name === 'string' && m.name.trim() ? m.name : null,
    method: (typeof req.method === 'string' ? req.method : 'ANY').toUpperCase(),
    url,
    // The host computes the protocol (descriptor lookup + matcher inspection, ADR 0010); the old
    // URL-shape guess only remains for sample mode / pre-G18 hosts.
    protocol: isProtocol(m.protocol) ? m.protocol
      : url.includes('/grpc') ? 'grpc' : url.includes('graphql') ? 'graphql' : 'http',
    priority: m.priority ?? 5,
    scenario: m.scenarioName ?? null,
    persistence: m.metadata?.['mockifyr:persistence'] ?? 'In-memory',
    lastMatched: null,
    status: m.response?.proxyBaseUrl ? 'proxy' : 'live',
    responseStatus: typeof m.response?.status === 'number' ? m.response.status : null,
    // `protocol` is computed by the server per response (ADR 0010) — strip it from the editable
    // mapping so a JSON-tab or channel-form save can never persist it into the stored Source.
    raw: (({ protocol: _p, ...rest }) => rest)(m as unknown as Record<string, unknown>),
  }
}

/**
 * Persists a stub (mapping JSON). Create = POST, update = PUT /__admin/mappings/{id}.
 * Returns `mock: true` when no host answered, so the caller can toast "sample mode" instead of failing.
 */
export async function saveStub(tenant: string, mappingJson: string, id?: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch(id ? `/mappings/${id}` : '/mappings', tenant, {
      method: id ? 'PUT' : 'POST',
      body: mappingJson,
    })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

/**
 * Bulk-imports mappings from an export — a single mapping or a `{"mappings":[…]}` bundle —
 * via POST /__admin/mappings/import. This is the migration path: a `GET /__admin/mappings`
 * dump drops straight in. Returns `mock: true` when no host answered.
 */
export async function importMappings(tenant: string, json: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch('/mappings/import', tenant, { method: 'POST', body: json })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

/** Tenants that exist server-side (materialized once they have stubs). Empty in sample mode. */
export async function fetchTenants(): Promise<{ tenants: string[]; mock: boolean }> {
  try {
    const res = await adminFetch('/tenants', 'default')
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { tenants?: string[] }
    return { tenants: body.tenants ?? [], mock: false }
  } catch {
    return { tenants: [], mock: true }
  }
}

// WebSocket message-mappings (G15d/G18-pre): a separate admin resource from request/response stubs.
export interface MessageMapping {
  id: string
  /** A one-line summary of the trigger for list rows (matcher op + value, or "on connect"). */
  trigger: string
  onConnect: boolean
  raw: Record<string, unknown>
}

function projectMessageMapping(m: Record<string, unknown>): MessageMapping {
  const trigger = m.trigger as { type?: string; message?: { body?: Record<string, unknown> } } | undefined
  const onConnect = trigger?.type === 'connection'
  const body = trigger?.message?.body ?? {}
  const [op, value] = Object.entries(body)[0] ?? []
  return {
    id: String(m.id ?? crypto.randomUUID()),
    trigger: onConnect ? 'connection' : op ? `${op}: ${String(value)}` : '*',
    onConnect,
    raw: m,
  }
}

export async function fetchMessageMappings(tenant: string): Promise<{ messageMappings: MessageMapping[]; mock: boolean }> {
  try {
    const res = await adminFetch('/message-mappings', tenant)
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { messageMappings?: Record<string, unknown>[] }
    return { messageMappings: (body.messageMappings ?? []).map(projectMessageMapping), mock: false }
  } catch {
    return { messageMappings: [], mock: true }
  }
}

export async function saveMessageMapping(tenant: string, json: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch('/message-mappings', tenant, { method: 'POST', body: json })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

export async function deleteMessageMapping(tenant: string, id: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch(`/message-mappings/${id}`, tenant, { method: 'DELETE' })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

// Captured messages (G18, ADR 0009): the tenant-scoped inbox the SMTP/SMS facades write into.
export type MessageChannel = 'email' | 'sms'

export interface CapturedMessage {
  id: string
  channel: MessageChannel
  from: string
  to: string[]
  subject: string | null
  body: string
  htmlBody: string | null
  meta: Record<string, string>
  attachments: { name: string; contentType: string; size: number }[]
  receivedAt: string
}

export async function fetchMessages(
  tenant: string,
  filters?: { channel?: MessageChannel; recipient?: string; contains?: string },
): Promise<{ messages: CapturedMessage[]; mock: boolean }> {
  const params = new URLSearchParams()
  if (filters?.channel) params.set('channel', filters.channel)
  if (filters?.recipient) params.set('recipient', filters.recipient)
  if (filters?.contains) params.set('contains', filters.contains)
  const query = params.size ? `?${params.toString()}` : ''
  try {
    const res = await adminFetch(`/messages${query}`, tenant)
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { messages?: CapturedMessage[] }
    return { messages: body.messages ?? [], mock: false }
  } catch {
    return { messages: [], mock: true }
  }
}

export async function deleteMessage(tenant: string, id: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch(`/messages/${id}`, tenant, { method: 'DELETE' })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

export async function resetMessages(tenant: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch('/messages/reset', tenant, { method: 'POST' })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

// Channel behavior directives (G18e): SMTP faults/delay, simulated SMS provider errors, and the
// capture webhook — per tenant, applied by the facades like HTTP delay/fault directives.
export interface MessageBehaviors {
  smtpFault: 'none' | 'reject' | 'drop'
  smtpDelayMs: number
  smsErrorCode: number | null
  webhookUrl: string | null
}

export const defaultBehaviors: MessageBehaviors = { smtpFault: 'none', smtpDelayMs: 0, smsErrorCode: null, webhookUrl: null }

export async function fetchMessageBehaviors(tenant: string): Promise<{ behaviors: MessageBehaviors; mock: boolean }> {
  try {
    const res = await adminFetch('/messages/behaviors', tenant)
    if (!res.ok) throw new Error(String(res.status))
    return { behaviors: (await res.json()) as MessageBehaviors, mock: false }
  } catch {
    return { behaviors: defaultBehaviors, mock: true }
  }
}

/** PUT the tenant's behaviors; the server validates (negative delay, non-5-digit code → error). */
export async function saveMessageBehaviors(tenant: string, behaviors: MessageBehaviors): Promise<{ ok: boolean; error?: string }> {
  try {
    const res = await adminFetch('/messages/behaviors', tenant, { method: 'PUT', body: JSON.stringify(behaviors) })
    if (res.ok) return { ok: true }
    const body = (await res.json().catch(() => null)) as { title?: string } | null
    return { ok: false, error: body?.title }
  } catch {
    return { ok: false }
  }
}

export async function resetMessageBehaviors(tenant: string): Promise<{ ok: boolean }> {
  try {
    const res = await adminFetch('/messages/behaviors', tenant, { method: 'DELETE' })
    return { ok: res.ok }
  } catch {
    return { ok: false }
  }
}

/** The raw wire payload of one message (SMTP: full MIME as received; SMS: the provider form body). */
export async function fetchMessageRaw(tenant: string, id: string): Promise<string | null> {
  try {
    const res = await adminFetch(`/messages/${id}`, tenant)
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { raw?: string | null }
    return body.raw ?? null
  } catch {
    return null
  }
}

/** The per-attachment download URL (served with the stored content type and file name). */
export const messageAttachmentUrl = (id: string, index: number) => `/__admin/messages/${id}/attachments/${index}`

// gRPC descriptor management (G18-pre): host-level (not tenant-scoped) — descriptors live in
// <root-dir>/grpc/ and uploads hot-reload the serving index.
export interface GrpcDescriptors {
  descriptors: { name: string; size: number }[]
  services: { service: string; methods: { method: string; path: string; input: string; output: string }[] }[]
}

export async function fetchGrpcDescriptors(): Promise<{ grpc: GrpcDescriptors; mock: boolean }> {
  try {
    const res = await adminFetch('/grpc/descriptors', 'default')
    if (!res.ok) throw new Error(String(res.status))
    return { grpc: (await res.json()) as GrpcDescriptors, mock: false }
  } catch {
    return { grpc: { descriptors: [], services: [] }, mock: true }
  }
}

export async function uploadGrpcDescriptor(name: string, bytes: ArrayBuffer): Promise<{ ok: boolean }> {
  try {
    const res = await adminFetch(`/grpc/descriptors?name=${encodeURIComponent(name)}`, 'default', {
      method: 'POST',
      headers: { 'Content-Type': 'application/octet-stream' },
      body: bytes,
    })
    return { ok: res.ok }
  } catch {
    return { ok: false }
  }
}

export async function deleteGrpcDescriptor(name: string): Promise<{ ok: boolean }> {
  try {
    const res = await adminFetch(`/grpc/descriptors/${encodeURIComponent(name)}`, 'default', { method: 'DELETE' })
    return { ok: res.ok }
  } catch {
    return { ok: false }
  }
}

// Host status for the Settings/Status screen.
export interface Health {
  name: string
  version: string
  persistence: string
  tenants: number
  totalStubs: number
}

const PERSISTENCE_LABEL: Record<string, string> = {
  NullStubPersistence: 'In-memory (ephemeral)',
  FileSystemStubPersistence: 'File-based (JSON)',
  LiteDbStubPersistence: 'LiteDB (embedded)',
  PostgresStubPersistence: 'PostgreSQL',
  RedisStubPersistence: 'Redis',
}

/** Friendly label for a persistence provider type name. */
export const persistenceLabel = (name: string) => PERSISTENCE_LABEL[name] ?? name

export async function fetchHealth(tenant: string): Promise<{ health: Health; mock: boolean }> {
  try {
    const res = await adminFetch('/health', tenant)
    if (!res.ok) throw new Error(String(res.status))
    return { health: (await res.json()) as Health, mock: false }
  } catch {
    return { health: { name: 'Mockifyr', version: '1.0', persistence: 'NullStubPersistence', tenants: 3, totalStubs: 248 }, mock: true }
  }
}

// Record-through-proxy session (G8/G9). Not tenant-scoped — the recording session is global.
export type RecordingStatus = 'Recording' | 'Stopped'
export interface CapturedStub { method: string; url: string; raw: string }

export async function fetchRecordingStatus(tenant: string): Promise<{ status: RecordingStatus; mock: boolean }> {
  try {
    const res = await adminFetch('/recordings/status', tenant)
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { status?: string }
    return { status: body.status === 'Recording' ? 'Recording' : 'Stopped', mock: false }
  } catch {
    return { status: 'Stopped', mock: true }
  }
}

export async function startRecording(tenant: string, targetBaseUrl: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch('/recordings/start', tenant, { method: 'POST', body: JSON.stringify({ targetBaseUrl }) })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

async function captureVia(tenant: string, path: string): Promise<{ stubs: CapturedStub[]; mock: boolean }> {
  try {
    const res = await adminFetch(path, tenant, { method: 'POST' })
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { mappings?: RawMapping[] }
    return { stubs: (body.mappings ?? []).map(projectCaptured), mock: false }
  } catch {
    return { stubs: [], mock: true }
  }
}

/**
 * Imports an OpenAPI 3.x document (JSON or YAML) as stubs via POST /__admin/openapi/import (G19c).
 * `stateful` wires resource-shaped path pairs to the sandbox state directive. Returns the imported
 * count, or throws with the host's typed message so the form can surface it.
 */
export async function importOpenApi(tenant: string, spec: string, stateful: boolean): Promise<{ imported: number; mock: boolean }> {
  try {
    const res = await adminFetch(`/openapi/import${stateful ? '?stateful=true' : ''}`, tenant, { method: 'POST', body: spec })
    const body = (await res.json()) as { imported?: number; error?: string; message?: string }
    if (!res.ok) throw new Error(body.message ?? body.error ?? String(res.status))
    return { imported: body.imported ?? 0, mock: false }
  } catch (e) {
    if (e instanceof Error && e.message && !e.message.startsWith('Failed to fetch')) throw e
    return { imported: 0, mock: true }
  }
}

export const snapshotRecording = (tenant: string) => captureVia(tenant, '/recordings/snapshot')
export const stopRecording = (tenant: string) => captureVia(tenant, '/recordings/stop')

function projectCaptured(m: RawMapping): CapturedStub {
  const req = m.request ?? {}
  return {
    method: (req.method ?? 'ANY').toUpperCase(),
    url: req.url ?? req.urlPath ?? req.urlPattern ?? req.urlPathPattern ?? '/',
    raw: JSON.stringify(m, null, 2),
  }
}

// A scenario (stateful stub group) and its state machine.
export interface Scenario {
  name: string
  state: string
  possibleStates: string[]
}

/** Loads the tenant's scenarios (GET /__admin/scenarios), with a sample fallback when no host. */
export async function fetchScenarios(tenant: string): Promise<{ scenarios: Scenario[]; mock: boolean }> {
  try {
    const res = await adminFetch('/scenarios', tenant)
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { scenarios?: Scenario[] }
    return { scenarios: body.scenarios ?? [], mock: false }
  } catch {
    return { scenarios: sampleScenarios(tenant), mock: true }
  }
}

/** Moves a scenario to a state (PUT /__admin/scenarios/{name}/state). */
export async function setScenarioState(tenant: string, name: string, state: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch(`/scenarios/${encodeURIComponent(name)}/state`, tenant, { method: 'PUT', body: JSON.stringify({ state }) })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

/** Resets every scenario to its start state (POST /__admin/scenarios/reset). */
export async function resetScenarios(tenant: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch('/scenarios/reset', tenant, { method: 'POST' })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

// The demo tenants that ship with representative sample data. A tenant the operator adds is not one of
// these, so in sample mode (no host) it reads as empty — the same way a brand-new tenant is empty on a
// real host. This avoids the illusion that adding a tenant "copied" every stub into it.
const DEMO_TENANTS = new Set(['default', 'globex', 'acme-pay'])

function sampleScenarios(tenant: string): Scenario[] {
  if (!DEMO_TENANTS.has(tenant)) return []
  const base: Scenario[] = [
    { name: 'Checkout', state: 'Started', possibleStates: ['Started', 'PaymentAuthorized', 'Captured'] },
    { name: 'AccountOnboarding', state: 'KycPending', possibleStates: ['Started', 'KycPending', 'Active'] },
    { name: 'MandateSetup', state: 'Active', possibleStates: ['Started', 'Active', 'Cancelled'] },
  ]
  return tenant === 'globex' ? base.slice(0, 1) : base
}

// A request-journal entry as the dashboard needs it — a flat projection of a serve event.
export interface JournalEntry {
  id: string
  method: string
  url: string
  /** Computed server-side (ADR 0010): grpc/graphql/sms entries read as what they are. */
  protocol?: Protocol | 'sms'
  status: number | null
  wasMatched: boolean
  /** ISO timestamp of when the request was served (for ordering + "time ago"). */
  loggedDate: string | null
}

export interface HeaderPair { name: string; value: string }
/**
 * One callback of a journal entry. `delivered: true` means this is the actual outbound request as
 * sent (templates rendered) plus the target's `response`; `delivered: false` means the delivery has
 * not been recorded (in flight / delayed) and the fields show the configured template. `error` holds
 * the failure when the delivery could not complete (unreachable target, template render error).
 */
export interface JournalWebhook {
  method: string
  url: string
  headers: HeaderPair[]
  body: string | null
  delivered: boolean
  response: { status: number; headers: HeaderPair[]; body: string | null } | null
  error: string | null
}
/** Full detail for one journal entry (GET /__admin/requests/{id}) — backs the detail drawer's tabs. */
export interface JournalDetail {
  id: string
  loggedDate: string | null
  wasMatched: boolean
  stubId: string | null
  request: { method: string; url: string; headers: HeaderPair[]; body: string }
  response: { status: number; statusMessage: string | null; headers: HeaderPair[]; body: string } | null
  webhooks: JournalWebhook[]
}

// The journal is an inspection view of recent traffic. A long-running host can accumulate tens of
// thousands of serve events; handing all of them to the client-side table (which builds a full row
// model for sorting/filtering on every render and poll) blocks the main thread and freezes the tab.
// Cap to the most recent slice so the grid stays responsive, and report the true total separately.
const JOURNAL_CAP = 500

/** Loads the tenant's request journal (GET /__admin/requests), with a sample fallback when no host. */
export async function fetchJournal(tenant: string, unmatchedOnly: boolean): Promise<{ entries: JournalEntry[]; total: number; mock: boolean }> {
  try {
    const res = await adminFetch(`/requests${unmatchedOnly ? '?unmatched=true' : ''}`, tenant)
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { requests?: JournalEntry[] }
    const all = body.requests ?? []
    return { entries: all.slice(0, JOURNAL_CAP), total: all.length, mock: false }
  } catch {
    const all = sampleJournal(tenant)
    const filtered = unmatchedOnly ? all.filter((e) => !e.wasMatched) : all
    return { entries: filtered.slice(0, JOURNAL_CAP), total: filtered.length, mock: true }
  }
}

function sampleJournal(tenant: string): JournalEntry[] {
  if (!DEMO_TENANTS.has(tenant)) return []
  const now = Date.now()
  const ago = (sec: number) => new Date(now - sec * 1000).toISOString()
  const rows: JournalEntry[] = [
    { id: 'r1', method: 'POST', url: '/api/v2/payments', status: 200, wasMatched: true, loggedDate: ago(8) },
    { id: 'r2', method: 'GET', url: '/api/v2/accounts/8891', status: 200, wasMatched: true, loggedDate: ago(95) },
    { id: 'r3', method: 'GET', url: '/api/v2/accounts/8891/statements', status: 404, wasMatched: false, loggedDate: ago(320) },
    { id: 'r4', method: 'POST', url: '/api/v2/payments/9/capture', status: 200, wasMatched: true, loggedDate: ago(1500) },
    { id: 'r5', method: 'DELETE', url: '/api/v2/mandates/44', status: 404, wasMatched: false, loggedDate: ago(7800) },
    { id: 'r6', method: 'GET', url: '/api/v2/rates?from=EUR&to=TRY', status: 200, wasMatched: true, loggedDate: ago(90000) },
  ]
  return tenant === 'globex' ? rows.slice(0, 3) : rows
}

/** Full detail for one journal entry; sampled when no host answers. */
export async function fetchJournalDetail(tenant: string, id: string): Promise<JournalDetail | null> {
  try {
    const res = await adminFetch(`/requests/${id}`, tenant)
    if (!res.ok) throw new Error(String(res.status))
    return (await res.json()) as JournalDetail
  } catch {
    return sampleDetail(id)
  }
}

function sampleDetail(id: string): JournalDetail {
  const matched = id !== 'r3' && id !== 'r5'
  return {
    id, loggedDate: new Date().toISOString(), wasMatched: matched, stubId: matched ? 'stub-1' : null,
    request: {
      method: 'POST', url: '/api/v2/payments',
      headers: [{ name: 'Content-Type', value: 'application/json' }, { name: 'Host', value: 'localhost:8080' }, { name: 'User-Agent', value: 'curl/8.4.0' }],
      body: JSON.stringify({ amount: 4200, currency: 'TRY' }, null, 2),
    },
    response: matched ? {
      status: 200, statusMessage: 'OK',
      headers: [{ name: 'Content-Type', value: 'application/json' }, { name: 'Matched-Stub-Id', value: 'stub-1' }],
      body: JSON.stringify({ ok: true, id: 'pay_9' }, null, 2),
    } : { status: 404, statusMessage: 'Not Found', headers: [{ name: 'Content-Type', value: 'application/json' }], body: JSON.stringify({ error: 'no stub matched' }, null, 2) },
    webhooks: matched ? [{
      method: 'POST', url: 'https://callback.example.com/hook',
      headers: [{ name: 'Content-Type', value: 'application/json' }],
      body: JSON.stringify({ event: 'payment.captured', paymentId: 'pay_9' }, null, 2),
      delivered: true,
      response: {
        status: 200,
        headers: [{ name: 'Content-Type', value: 'application/json' }],
        body: JSON.stringify({ received: true }, null, 2),
      },
      error: null,
    }] : [],
  }
}

/** Deletes a stub by id. Returns `mock: true` when no host answered. */
export async function deleteStub(tenant: string, id: string): Promise<{ mock: boolean }> {
  try {
    const res = await adminFetch(`/mappings/${id}`, tenant, { method: 'DELETE' })
    if (!res.ok) throw new Error(String(res.status))
    return { mock: false }
  } catch {
    return { mock: true }
  }
}

// Representative sample data (used only when no host answers). Varies a little by tenant so switching
// tenants visibly re-scopes the grid.
function sampleStubs(tenant: string): Stub[] {
  if (!DEMO_TENANTS.has(tenant)) return []
  const base: Stub[] = [
    // A few endpoints carry more than one case (same URL+method, different status/name) to show the tree.
    { id: '1', name: 'Account found', method: 'GET', url: '/api/v2/accounts/{id}', protocol: 'http', priority: 5, scenario: null, persistence: 'Postgres', lastMatched: '12s', status: 'live', responseStatus: 200 },
    { id: '1b', name: 'Account not found', method: 'GET', url: '/api/v2/accounts/{id}', protocol: 'http', priority: 3, scenario: null, persistence: 'Postgres', lastMatched: '4m', status: 'live', responseStatus: 404 },
    { id: '2', name: 'Payment accepted', method: 'POST', url: '/api/v2/payments', protocol: 'http', priority: 10, scenario: 'Checkout', persistence: 'Postgres', lastMatched: '3s', status: 'live', responseStatus: 201 },
    { id: '2b', name: 'Payment declined', method: 'POST', url: '/api/v2/payments', protocol: 'http', priority: 10, scenario: 'Checkout', persistence: 'Postgres', lastMatched: '30s', status: 'live', responseStatus: 402 },
    { id: '3', name: null, method: 'POST', url: '/api/v2/payments/{id}/capture', protocol: 'http', priority: 10, scenario: 'Checkout', persistence: 'Postgres', lastMatched: '7s', status: 'live', responseStatus: 200 },
    { id: '4', name: null, method: 'GET', url: '/api/v2/rates?from={a}&to={b}', protocol: 'http', priority: 3, scenario: null, persistence: 'Redis', lastMatched: '1m', status: 'proxy', responseStatus: null },
    { id: '5', name: null, method: 'PUT', url: '/api/v2/accounts/{id}/limits', protocol: 'http', priority: 5, scenario: null, persistence: 'Postgres', lastMatched: '18m', status: 'live', responseStatus: 200 },
    { id: '6', name: null, method: 'DELETE', url: '/api/v2/mandates/{id}', protocol: 'http', priority: 5, scenario: null, persistence: 'Postgres', lastMatched: '2h', status: 'draft', responseStatus: 204 },
    { id: '7', name: null, method: 'PATCH', url: '/api/v2/webhooks/{id}', protocol: 'http', priority: 1, scenario: null, persistence: 'LiteDB', lastMatched: '1d', status: 'live', responseStatus: 200 },
    { id: '8', name: null, method: 'POST', url: 'mockifyr.grpc.Greeter/SayHello', protocol: 'grpc', priority: 8, scenario: null, persistence: 'Postgres', lastMatched: '41s', status: 'live', responseStatus: 200 },
    { id: '9', name: null, method: 'POST', url: '/graphql · query Balance', protocol: 'graphql', priority: 8, scenario: null, persistence: 'Postgres', lastMatched: '55s', status: 'live', responseStatus: 200 },
    { id: '10', name: null, method: 'GET', url: '/ws/notifications', protocol: 'websocket', priority: 5, scenario: null, persistence: 'In-memory', lastMatched: '2m', status: 'live', responseStatus: 101 },
  ]
  if (tenant === 'globex') return base.slice(0, 6).map((s) => ({ ...s, url: s.url.replace('/api/v2', '/retail/v1') }))
  if (tenant === 'default') return base.slice(0, 3)
  return base
}

// ---------------------------------------------------------------------------
// Git sync (ADR 0007): host-level, explicit push/pull of the root-dir working
// copy. Status is null when no host answers (sample mode) — the UI hides the
// controls; a host without --git-remote answers configured:false.

export interface GitStatus {
  configured: boolean
  remote: string | null
  branch: string | null
  dirty: boolean
  ahead: number
  behind: number
  fetchError: string | null
  /** 'flags' = pinned by host flags (read-only in the UI); 'repository' = connected from the dashboard. */
  configuredBy: 'flags' | 'repository' | null
  workingCopy: string | null
  /** Where HTTPS credentials come from: dashboard-set, the host environment, or nowhere yet (#153). */
  credentialsSource: 'none' | 'environment' | 'dashboard'
}

export async function fetchGitStatus(tenant: string): Promise<GitStatus | null> {
  try {
    const res = await adminFetch('/git/status', tenant)
    if (!res.ok) return null
    return (await res.json()) as GitStatus
  } catch {
    return null
  }
}

/**
 * Push/pull outcome. Failures carry the host's typed error code and human message
 * (e.g. Git.RemoteAhead → "pull first"), which the UI surfaces verbatim in a toast.
 */
export type GitSyncResult =
  | { ok: true; reason: string; commit?: string | null; pushed?: boolean; updated?: boolean; stubsLoaded?: number }
  | { ok: false; error: string; message: string }

export const gitPush = (tenant: string, message?: string): Promise<GitSyncResult> =>
  gitOp('/git/push', tenant, message?.trim() ? JSON.stringify({ message: message.trim() }) : undefined)

export const gitPull = (tenant: string): Promise<GitSyncResult> => gitOp('/git/pull', tenant)

/**
 * Connects the host's working copy to a Git remote (#151). The local directory is resolved
 * host-side; credentials never pass through here (private HTTPS remotes use MOCKIFYR_GIT_TOKEN).
 */
export async function gitConfigure(tenant: string, remoteUrl: string, branch?: string): Promise<GitStatus | { error: string; message: string }> {
  try {
    const res = await adminFetch('/git/configure', tenant, {
      method: 'POST',
      body: JSON.stringify({ remoteUrl, ...(branch?.trim() ? { branch: branch.trim() } : {}) }),
    })
    const json = (await res.json().catch(() => ({}))) as Record<string, unknown>
    if (res.ok) return json as unknown as GitStatus
    return { error: String(json.error ?? res.status), message: String(json.message ?? 'Configuration failed.') }
  } catch {
    return { error: 'network', message: 'No host reachable.' }
  }
}

/**
 * Sets (or, with an empty token, clears) the HTTPS credentials used for pull/push (#153). The token
 * is sent once over the admin API and held in host process memory only — the status echoes just its
 * source (none/environment/dashboard), never the value.
 */
export async function gitSetCredentials(tenant: string, token: string, username?: string): Promise<GitStatus | { error: string; message: string }> {
  try {
    const res = await adminFetch('/git/credentials', tenant, {
      method: 'POST',
      body: JSON.stringify({ token, ...(username?.trim() ? { username: username.trim() } : {}) }),
    })
    const json = (await res.json().catch(() => ({}))) as Record<string, unknown>
    if (res.ok) return json as unknown as GitStatus
    return { error: String(json.error ?? res.status), message: String(json.message ?? 'Saving credentials failed.') }
  } catch {
    return { error: 'network', message: 'No host reachable.' }
  }
}

async function gitOp(path: string, tenant: string, body?: string): Promise<GitSyncResult> {
  try {
    const res = await adminFetch(path, tenant, { method: 'POST', ...(body ? { body } : {}) })
    const json = (await res.json().catch(() => ({}))) as Record<string, unknown>
    if (res.ok) return { ok: true, reason: String(json.reason ?? 'ok'), ...json }
    return {
      ok: false,
      error: String(json.error ?? res.status),
      message: String(json.message ?? 'Git operation failed.'),
    }
  } catch {
    return { ok: false, error: 'network', message: 'No host reachable.' }
  }
}

// ---- environments (#165, #166) ------------------------------------------------------------------
// Every call goes through adminFetch, which stamps the X-Mockifyr-Tenant header — so the tenant a key
// is written to, read from, or deleted in is always the one the dashboard is currently showing.

/** Loads the tenant's environment keys (GET /__admin/environments). */
export async function fetchEnvironments(tenant: string): Promise<{ environments: EnvironmentKey[]; mock: boolean }> {
  try {
    const res = await adminFetch('/environments', tenant)
    if (!res.ok) throw new Error(String(res.status))
    const body = (await res.json()) as { environments?: EnvironmentKey[] }
    return { environments: body.environments ?? [], mock: false }
  } catch {
    // No host: environments are server state, so there is nothing meaningful to fake. An empty list
    // plus the mock flag is honest — the page tells the user it is not connected.
    return { environments: [], mock: true }
  }
}

/** Creates or replaces a key (PUT /__admin/environments/{key}). Returns the server's error code, if any. */
export async function putEnvironmentKey(
  tenant: string,
  key: EnvironmentKey,
): Promise<{ ok: boolean; error?: string; message?: string }> {
  try {
    const res = await adminFetch(`/environments/${encodeURIComponent(key.key)}`, tenant, {
      method: 'PUT',
      body: JSON.stringify({ activeValue: key.activeValue, values: key.values }),
    })
    if (res.ok) return { ok: true }
    const body = (await res.json().catch(() => ({}))) as { error?: string; message?: string }
    return { ok: false, error: body.error, message: body.message }
  } catch {
    return { ok: false, error: 'Network' }
  }
}

/** Selects which value is active for a key (PUT /__admin/environments/{key}/active). */
export async function setEnvironmentActiveValue(tenant: string, key: string, activeValue: string): Promise<boolean> {
  try {
    const res = await adminFetch(`/environments/${encodeURIComponent(key)}/active`, tenant, {
      method: 'PUT',
      body: JSON.stringify({ activeValue }),
    })
    return res.ok
  } catch {
    return false
  }
}

/** Deletes a key (DELETE /__admin/environments/{key}). */
export async function deleteEnvironmentKey(tenant: string, key: string): Promise<boolean> {
  try {
    const res = await adminFetch(`/environments/${encodeURIComponent(key)}`, tenant, { method: 'DELETE' })
    return res.ok
  } catch {
    return false
  }
}

// ---- outbound certificate trust (#174) ----------------------------------------------------------
// Host-level, not tenant-scoped: the outbound HttpClient is shared, so trust cannot belong to one
// tenant. The tenant argument only rides along because adminFetch stamps the header for every call.

export interface OutboundTrust {
  hosts: string[]
  /** Verification disabled for every target (flag-only; never settable from here). */
  trustAll: boolean
  /** Pinned by a --trust-* startup flag, so the dashboard shows it read-only. */
  pinned: boolean
  /** Whether changes survive a restart — false on a host with no root directory. */
  persistent: boolean
}

export async function fetchOutboundTrust(tenant: string): Promise<OutboundTrust | null> {
  try {
    const res = await adminFetch('/outbound-trust', tenant)
    if (!res.ok) return null
    return (await res.json()) as OutboundTrust
  } catch {
    // Null hides the card entirely in sample mode, the same way the Git card behaves.
    return null
  }
}

async function trustOp(path: string, tenant: string, init: RequestInit): Promise<OutboundTrust | { error: string; message: string }> {
  try {
    const res = await adminFetch(path, tenant, init)
    const body = await res.json().catch(() => ({}))
    return res.ok ? (body as OutboundTrust) : (body as { error: string; message: string })
  } catch {
    return { error: 'Network', message: 'The host is unreachable.' }
  }
}

export const trustHost = (tenant: string, host: string) =>
  trustOp('/outbound-trust/hosts', tenant, { method: 'POST', body: JSON.stringify({ host }) })

export const distrustHost = (tenant: string, host: string) =>
  trustOp(`/outbound-trust/hosts/${encodeURIComponent(host)}`, tenant, { method: 'DELETE' })
