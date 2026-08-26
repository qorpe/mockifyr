// Multi-tenancy is first-class in the engine: every admin call is scoped to a tenant and carries the
// X-Mockifyr-Tenant header. Until an admin `list tenants` endpoint exists these are seeded; the active
// tenant is persisted so a reload keeps the operator's context.
export interface Tenant {
  id: string
  name: string
}

export const TENANTS: Tenant[] = [
  { id: 'default', name: 'Default' },
  { id: 'acme-pay', name: 'Acme Payments' },
  { id: 'globex', name: 'Globex Retail' },
]

// The host injects its runtime configuration into the served shell (#396), because the tenant header
// is renameable and a dashboard that assumed the default would put every call in the wrong tenant the
// moment an operator renamed it — showing the operator data that is not theirs, with no error.
//
// The fallback is the historical name: in `pnpm dev` there is no host to inject anything, and a host
// that has not been reconfigured serves exactly this value anyway.
declare global {
  interface Window {
    __MOCKIFYR__?: { tenantHeader?: string }
  }
}

export const TENANT_HEADER = window.__MOCKIFYR__?.tenantHeader || 'X-Mockifyr-Tenant'
