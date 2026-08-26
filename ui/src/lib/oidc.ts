/**
 * OIDC sign-in for the dashboard (#251) — authorization code with PKCE.
 *
 * PKCE and no client secret, because this is a public client: anything shipped to a browser is
 * readable, so the flow has to be one that stays safe when its parameters are. The access token lives
 * in sessionStorage rather than localStorage, so closing the tab ends the session — a shared machine
 * should not keep somebody signed in to a mock platform indefinitely.
 */

import { DASHBOARD_PATH } from '@/lib/host-config'

const TOKEN_KEY = 'ui.oidcToken'
const VERIFIER_KEY = 'ui.oidcVerifier'
const RETURN_KEY = 'ui.oidcReturn'

/** The public parameters the host advertises on /__admin/health. */
export interface OidcConfig {
  authority: string
  clientId: string
}

export const getBearer = () => sessionStorage.getItem(TOKEN_KEY)
export const clearBearer = () => sessionStorage.removeItem(TOKEN_KEY)

function randomString(bytes = 32): string {
  const buffer = new Uint8Array(bytes)
  crypto.getRandomValues(buffer)
  return base64Url(buffer)
}

function base64Url(bytes: Uint8Array): string {
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

async function challengeFor(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))
  return base64Url(new Uint8Array(digest))
}

/** Reads the issuer's discovery document for its authorize/token endpoints. */
async function discover(authority: string): Promise<{ authorization_endpoint: string; token_endpoint: string }> {
  const res = await fetch(`${authority.replace(/\/$/, '')}/.well-known/openid-configuration`)
  if (!res.ok) throw new Error(`The identity provider did not answer discovery (${res.status})`)
  return (await res.json()) as { authorization_endpoint: string; token_endpoint: string }
}

/** The redirect URI registered with the provider: this app's own base path. */
function redirectUri(): string {
  return `${window.location.origin}${DASHBOARD_PATH}` + '/'
}

/** Sends the browser to the identity provider. */
export async function beginLogin(config: OidcConfig): Promise<void> {
  const { authorization_endpoint } = await discover(config.authority)
  const verifier = randomString()
  sessionStorage.setItem(VERIFIER_KEY, verifier)
  // Where the user was, so a mid-session expiry returns them to the page they were on rather than
  // dropping them at the dashboard root.
  sessionStorage.setItem(RETURN_KEY, window.location.pathname + window.location.search)

  const params = new URLSearchParams({
    client_id: config.clientId,
    response_type: 'code',
    redirect_uri: redirectUri(),
    scope: 'openid profile email',
    code_challenge: await challengeFor(verifier),
    code_challenge_method: 'S256',
    state: randomString(16),
  })
  window.location.assign(`${authorization_endpoint}?${params}`)
}

/**
 * Completes the flow when the provider redirects back with `?code=`. Returns true when a token was
 * obtained, so the caller knows to refetch. The code is exchanged and then removed from the URL —
 * an authorization code left in the address bar ends up in history and in shared links.
 */
export async function completeLoginIfRedirected(config: OidcConfig): Promise<boolean> {
  const params = new URLSearchParams(window.location.search)
  const code = params.get('code')
  if (!code) return false

  const verifier = sessionStorage.getItem(VERIFIER_KEY)
  sessionStorage.removeItem(VERIFIER_KEY)
  if (!verifier) return false

  try {
    const { token_endpoint } = await discover(config.authority)
    const res = await fetch(token_endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        grant_type: 'authorization_code',
        code,
        redirect_uri: redirectUri(),
        client_id: config.clientId,
        code_verifier: verifier,
      }),
    })
    if (!res.ok) return false

    const payload = (await res.json()) as { access_token?: string }
    if (!payload.access_token) return false
    sessionStorage.setItem(TOKEN_KEY, payload.access_token)
    return true
  } finally {
    const back = sessionStorage.getItem(RETURN_KEY) ?? (DASHBOARD_PATH || '/')
    sessionStorage.removeItem(RETURN_KEY)
    window.history.replaceState({}, '', back)
  }
}
