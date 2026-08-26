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

// The tenant header now lives with the rest of the host's runtime configuration (#396); re-exported
// here so the existing imports keep reading naturally.
export { TENANT_HEADER } from '@/lib/host-config'
