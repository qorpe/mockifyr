// Environments (#165, #166): tenant-scoped key/value config referenced from stubs as {{key}}.
//
// This replaces the original localStorage design (#157), which had two defects the issues report:
// values were global across tenants, and {{name}} was resolved EAGERLY in the browser at save time,
// so the stored mapping carried a frozen URL. Both are gone. Keys now live server-side behind
// /__admin/environments, scoped by the X-Mockifyr-Tenant header, and the engine substitutes {{key}}
// at serve time — so changing which value is active changes what an already-saved stub returns, with
// no re-save. The UI never resolves a reference into a stub it is about to store.
//
// Resolution here exists only to PREVIEW what the server will produce, and to flag a reference that
// names no key. It is never written anywhere.

/**
 * One selectable value of a key, e.g. `dev` -> `https://dev.example.com`.
 *
 * `value` is OPTIONAL because a secret one is withheld by the admin API (#348) — the read carries the
 * marker and no literal. Writing the value back is therefore how a secret gets destroyed, so a row
 * with `secret` and no `value` means "unchanged" and the server resolves it against what it holds.
 */
export interface EnvironmentValue {
  name: string
  value?: string
  /** Withheld from every reporting surface; still resolved when a stub is served. */
  secret?: boolean
}

/** A key and its values, one of which is active. */
export interface EnvironmentKey {
  key: string
  activeValue: string
  /** True when the active value is a secret — `resolved` is then withheld rather than absent (#348). */
  secret?: boolean
  /** What the key currently resolves to, computed server-side; null when activeValue names nothing. */
  resolved: string | null
  values: EnvironmentValue[]
}

// Keys are bare identifiers so {{key}} stays distinguishable from Handlebars expressions
// ({{jsonPath …}}, {{request.path}} etc. contain spaces or dots and are never substituted).
// Mirrors ReservedEnvironmentKeys.IsWellFormed on the server.
export const ENV_KEY_PATTERN = /^[A-Za-z_][A-Za-z0-9_-]*$/

// Kept in sync with ReservedEnvironmentKeys.Reserved on the server. Duplicated deliberately: the
// server is authoritative and rejects with Environment.ReservedKey, but repeating the list here turns
// a 400 into inline feedback while the user is still typing.
const RESERVED = new Set([
  'request', 'originalRequest', 'jsonPath', 'xPath', 'soapXPath', 'hostname',
  'parseJson', 'toJson', 'pickRandom', 'size',
  'trim', 'capitalize', 'upper', 'lower', 'abbreviate', 'substring', 'replace', 'join', 'split',
  'stringJoiner', 'truncate', 'padLeft', 'padRight',
  'add', 'subtract', 'multiply', 'divide', 'round', 'abs', 'floor', 'ceil', 'max', 'min',
  'random', 'randomValue', 'randomInt', 'randomDecimal', 'uuid', 'jwt', 'jwks',
  'now', 'date', 'dateFormat', 'parseDate', 'unixEpoch',
  'base64', 'urlEncode', 'urlDecode', 'formData', 'hash',
  'if', 'unless', 'each', 'with', 'eq', 'neq', 'gt', 'lt', 'gte', 'lte', 'and', 'or', 'not',
  'this', 'else', 'lookup', 'log',
])

/** True when a key name would shadow a built-in templating helper (the server refuses these). */
export function isReservedKey(key: string): boolean {
  return RESERVED.has(key.toLowerCase())
}

const VARIABLE = /\{\{\s*([A-Za-z_][A-Za-z0-9_-]*)\s*\}\}/g

export interface ResolvedPreview {
  /** What the server will serve, for display only — never stored. */
  resolved: string
  /** References naming no key: surfaced as a warning rather than silently rendering empty. */
  unknown: string[]
  changed: boolean
}

/**
 * Previews what the engine will substitute. Mirrors EnvironmentSubstitution.Apply on the server:
 * only defined keys are replaced, and every other {{…}} construct passes through so a field can mix
 * both — `{{baseUrl}}/callback?id={{jsonPath originalRequest.body '$.id'}}`.
 */
export function previewEnvironment(text: string, keys: EnvironmentKey[]): ResolvedPreview {
  const unknown = new Set<string>()
  let changed = false
  const resolved = (text ?? '').replace(VARIABLE, (whole, name: string) => {
    const key = keys.find((k) => k.key === name)
    if (key && key.resolved !== null) {
      changed = true
      return key.resolved
    }
    unknown.add(name)
    return whole
  })
  return { resolved, unknown: [...unknown], changed }
}

// ---- migration from the #157 localStorage shape ------------------------------------------------

const LEGACY_KEY = 'ui.environments'

interface LegacyState {
  environments?: { name: string; baseUrl: string }[]
  active?: string | null
}

/**
 * Converts environments created under the old flat structure into the new per-key model, exactly
 * once, and only into the tenant the user is currently in — the old data carried no tenant, so
 * silently fanning it out to every tenant would recreate the leak #166 is about.
 *
 * Each old environment (`local` -> url) becomes one key of the same name with a single value named
 * `default`, which preserves what `{{local}}` meant. The legacy blob is removed only after the
 * server accepts the writes, so a failed migration retries on the next visit rather than losing data.
 */
export async function migrateLegacyEnvironments(
  tenant: string,
  put: (tenant: string, key: EnvironmentKey) => Promise<boolean>,
): Promise<number> {
  let legacy: LegacyState | null = null
  try {
    legacy = JSON.parse(localStorage.getItem(LEGACY_KEY) ?? 'null') as LegacyState | null
  } catch {
    legacy = null
  }

  const environments = legacy?.environments
  if (!Array.isArray(environments) || environments.length === 0) {
    if (legacy) localStorage.removeItem(LEGACY_KEY)
    return 0
  }

  let migrated = 0
  for (const env of environments) {
    if (!env?.name || !ENV_KEY_PATTERN.test(env.name) || isReservedKey(env.name)) continue
    const ok = await put(tenant, {
      key: env.name,
      activeValue: 'default',
      resolved: env.baseUrl,
      values: [{ name: 'default', value: env.baseUrl ?? '' }],
    })
    if (ok) migrated++
  }

  if (migrated === environments.length) localStorage.removeItem(LEGACY_KEY)
  return migrated
}
