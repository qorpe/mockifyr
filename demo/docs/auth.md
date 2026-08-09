# Authentication: two doors, different locks

Mockifyr has two surfaces with different threat models:

- the **mock surface** — the APIs your applications call (`/payments`, gRPC, WS, …)
- the **admin surface** — `/__admin/*` and the dashboard, where the platform is managed

## Door 1 · The mock surface: sandbox API keys

By default the mock surface is open — it's a dev tool serving synthetic data. The moment you
hand a sandbox to an outside team, switch on `--sandbox-auth` and issue keys:

```
POST /__admin/apikeys   {"name": "partner-portal", "quotaPerHour": 100}
→ 201 { "key": "mfk_…", "prefix": "mfk_a1b2c3d4", … }
```

Facts that matter:

- **The server mints the secret** (256-bit CSPRNG, `mfk_` prefix). You never choose it.
- **The secret is shown exactly once.** Only a salted SHA-256 hash is stored (compared in
  constant time); the 12-char prefix exists purely for recognition in lists.
- It is a **static secret, not a session token** — no login call, no JWT, no expiry/refresh.
  Think Stripe's `sk_live_…`: put it in config, send it on every request
  (`X-Api-Key: mfk_…` or `Authorization: Bearer mfk_…`).
- **The key IS the tenant.** It resolves the tenant ahead of any header — a partner cannot
  reach another tenant's data by writing a different tenant name.
- Honest failures: invalid key → **401** (never a silent fallthrough); over quota → **429**
  with `Retry-After` and `X-RateLimit-*` headers.
- A sandbox key is **never** valid on `/__admin` — data-plane credentials can't open the
  management door. Revoke anytime with `DELETE /__admin/apikeys/{id}`.

## Door 2 · The admin surface: three principal sources, side by side

One authentication chain sits in front of `/__admin`; whatever the request presents, the
chain resolves it to a principal:

```
Authorization: Basic …    → global admin (--admin-user/--admin-pass)
                            or a per-tenant credential (--tenant-credential t:u:p)
Authorization: Bearer …   → OIDC: validated against the issuer's discovery keys
nothing                   → 401 if auth is on; open if it never was (local mode + startup warning)
```

- **Per-tenant credentials** turn the tenant header from a claim into an authorization
  decision: acme's admin managing globex gets a **403**.
- **OIDC** (`--oidc-authority`, `--oidc-client-id`, optional `--oidc-audience`,
  `--oidc-tenant-claim`, `--oidc-required-role`) plugs into the corporate IdP
  (Keycloak, Entra, Okta…). A claim scopes the identity to exactly one tenant, exactly like
  a tenant credential; a role can be required. Basic keeps working beside it — humans use
  SSO, CI and machine accounts keep Basic.
- **The dashboard login shapes itself** from what the server reports (via the auth-exempt
  `/__admin/health`): no auth → no login screen; Basic → username/password form, sent on
  every call; OIDC → "sign in with SSO" using authorization code + **PKCE**.
- **Audit**: with `--audit`, every admin change records who (`oidc:<user>` included), which
  tenant, what action, what outcome — on `/__admin/audit`, the dashboard, and as a log line
  for a SIEM.

## Channel footnotes

- **SMTP**: the AUTH username *names the tenant* (addressing, not identity — password unchecked).
- **SMS profile**: provider credentials are not verified; the account SID is echoed back so
  official SDKs work unchanged.
- **Stub-level auth** (`basicAuth` matcher, header matchers like the demo's `X-Partner-Key`)
  simulates the *mocked API's own* auth behavior — that's scenario content, not platform identity.
