// The host injects its runtime configuration into the served shell (#396). Everything renameable
// about this dashboard arrives here rather than being compiled in, because an operator running the
// platform under their own name has to be able to put that name on the screen.
//
// Read through this module and nowhere else, so there is one place that knows the shape and one place
// that decides what an unset field means. Every field is optional: absent means "use the dashboard's
// own default", never "show nothing" — a host that configures none of this looks exactly as it did.
//
// In `pnpm dev` there is no host to inject anything, so the fallbacks are also what a developer sees.

export interface HostConfig {
  tenantHeader?: string
  dashboardPath?: string
  brandName?: string
  brandSubtitle?: string
  supportUrl?: string
  brandLogo?: string
}

declare global {
  interface Window {
    __MOCKIFYR__?: HostConfig
  }
}

const config: HostConfig = window.__MOCKIFYR__ ?? {}

/** The header every admin call names its tenant in. */
export const TENANT_HEADER = config.tenantHeader || 'X-Mockifyr-Tenant'

/** The product name, or null to fall back to the localised default. */
export const BRAND_NAME = config.brandName || null

/** The line under the name, or null for the localised default. */
export const BRAND_SUBTITLE = config.brandSubtitle || null

/** A logo URL served by the host, or null to draw the built-in mark. */
export const BRAND_LOGO = config.brandLogo || null

/**
 * The prefix this dashboard is served under, without a trailing slash (#396).
 *
 * The build-time base is the fallback rather than the source of truth: Vite bakes it into the asset
 * URLs and into `import.meta.env.BASE_URL`, but the host may serve the same bundle from somewhere
 * else — it rewrites the shell's asset URLs and tells us here. In `pnpm dev` there is no host, and
 * BASE_URL is '/'.
 */
export const DASHBOARD_PATH =
  (config.dashboardPath || import.meta.env.BASE_URL).replace(/\/$/, '') || ''

/** Where "report an issue" points. */
export const SUPPORT_URL = config.supportUrl || 'https://github.com/qorpe/mockifyr/issues'
