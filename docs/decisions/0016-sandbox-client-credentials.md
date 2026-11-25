# 0016 — A simulated client-credentials grant for sandbox tenants

## Status

Accepted. The owner decided it on 2026-11-17 on issue #450. This record **amends ADR 0011**, which is
not edited. It narrows one of 0011's non-goals and leaves the rest of that ADR as it was.

## Context

ADR 0011 lists "OAuth server" among the sandbox's non-goals and restates "no OAuth issuance". A sandbox
key is a static `mfk_` token that selects a tenant.

The APIs this sandbox stands in for (the partner APIs of banks and card schemes) are called with a
bearer token. The client obtains it by the OAuth 2.0 client-credentials grant, often over mutual TLS.
If the sandbox accepts only a static key, the partner's token code is first exercised in production,
and that is the failure a sandbox exists to prevent.

What existed before this record is no substitute:
- A token stub can be hand-authored with `{{jwt}}` and `{{jwks}}`. That stub is per mapping, it is
  not bound to the tenant's key, and nothing checks the token on later calls.

## Decision

**0011's non-goal narrows.** It was "no OAuth server". It becomes "no general-purpose authorization
server; a simulated client-credentials grant for sandbox tenants".

What the grant is:

- **Opt-in.** It is on only with `--sandbox-oauth` beside `--sandbox-auth`. Without the flag nothing is
  mapped, and both paths belong to the mocks as before.
- **The client is an existing sandbox key.** The key's id is the `client_id`, and the token shown once
  at issue is the `client_secret`. There is no new credential, store or lifecycle. Revoking a key,
  letting it expire or making it read-only applies to its tokens as well, because the serve path checks
  the key on every call. The token is accepted wherever the key is, in `Authorization: Bearer` and in
  `X-Api-Key` alike. A key of a suspended tenant (#357) is refused at the token endpoint, not one call
  later.
- **One grant.** `POST /oauth/token` (`--oauth-token-path`) accepts only `client_credentials`, with HTTP
  Basic or body client authentication, exactly one of the two per request. Errors are RFC 6749 §5.2's.
- **The token is an RS256 JWT in the RFC 9068 shape.** It is signed by a host key and published at
  `/.well-known/jwks.json`. Its `kid` is the key's RFC 7638 thumbprint, so replicas given one
  `--oauth-signing-key-file` accept each other's tokens. Without that flag the host generates a key at
  startup and says so.
- **The tenant's clock** (#290) sets `iat` and `exp`, and decides expiry. An expiry is therefore
  testable without waiting for it.
- **Validation is fixed, not negotiated.** The header may hold only `alg` (exactly `RS256`), `typ` and
  this key's `kid`. A token that asks for anything else is refused for what it says.

What it is not (stated in the docs, binding):

- no authorization-code flow;
- no consent;
- no refresh tokens;
- no user store;
- no identity provider;
- no token introspection or revocation endpoint.

## Consequences

- `/__admin/*` still ignores sandbox credentials, tokens included. ADR 0011's invariant ("a sandbox key
  never grants admin access") holds, and a wire test asserts it for a token.
- `/__sandbox/*`, the partner surface, still takes the static key only in this first part.
- These are later parts of #450, each recorded in `docs/parity/deferred-edges.md`:
  - a per-tenant mode in which static keys are refused and only tokens are accepted;
  - `private_key_jwt` client authentication;
  - tokens on the partner surface.
- The issue sketched the flag as `--sandbox-auth=oauth`. `--sandbox-auth` is parsed as a boolean, and
  that spelling would stop every host that passes `true`. The grant is therefore its own flag.
