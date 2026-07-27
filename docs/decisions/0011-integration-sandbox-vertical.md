# 0011 — Integration sandbox vertical: stateful resources, OpenAPI import, sandbox access

## Status

Accepted (planning PR merged). Design for the G19 roadmap group; to be implemented incrementally
by the G19a–G19e verticals. **Amended 2026-07-27** with the enterprise-readiness addendum below —
a pre-implementation audit turned implicit expectations into binding acceptance criteria.

## Context

An **integration sandbox** is a stand-in for a real system's API that external developers or
internal teams integrate against without touching production: a bank's open-banking sandbox, a
payment provider's test mode, an enterprise QA environment for a dependency that is unavailable,
rate-limited or expensive. The commercial category next to it is *service virtualization*.

Mockifyr already contains the parts of that product that are expensive to retrofit:

- **First-class multi-tenancy** (ADR 0003) — one isolated sandbox per consumer is a tenant.
- **Scenarios** (G5) and **delay/fault** (G4) — realistic flows and failure modes, which are the
  actual value of a sandbox (the unhappy path, not the happy one).
- **Protocol breadth** — HTTP, gRPC, GraphQL, WebSocket (G13–G15).
- **Message capture** (G18) — OTP/e-mail/SMS flows without real messages leaving the building.
- **Proxy + record & playback** (G8/G9) — bootstrap a sandbox from real traffic.
- **Environments, persistence, admin API, dashboard** (G16/G17, G7, post-phase UI).

Three gaps separate "a very good mock server" from "a sandbox you can hand to an integrator":

1. **Dynamic CRUD state.** In a sandbox, `POST /orders` creates something that `GET /orders/{id}`
   returns afterwards. Stubs are stateless by design; scenarios emulate state machines but not
   data. This is also the most-requested capability class for stub servers generally.
2. **Spec-driven bootstrap.** "Here is our OpenAPI document — give me a working sandbox" is the
   expected on-ramp; hand-writing stubs per operation is the adoption tax.
3. **Access.** Handing a sandbox to someone means giving them a credential that scopes them to
   their tenant and optionally limits their traffic — without a human creating stubs for them.

Constraints from the golden rules (CLAUDE.md), all of which G18 already proved workable for a
no-oracle vertical:

- **The engine stays pure.** State manipulation and quota enforcement are facade concerns
  expressed as *directives*, exactly like delay/fault. `Mockifyr.Core` gains only pure models and
  store contracts.
- **Tenant-first.** Every new store entry point takes `TenantId`; no tenant-less overload.
- **Opt-in everything.** No directive, no flag, no import → zero behavior change for existing
  users.
- **No oracle exists** for sandbox semantics (WireMock OSS has none of this). Validation follows
  the G18/WebSocket honesty rule: real-client self-tests + unit/integration/mutation coverage,
  stated per vertical in `docs/parity/`.
- **The parity surface does not move.** This vertical never extends the mapping-JSON dialect the
  oracle validates: imported stubs compile to ordinary mappings, and state is a Mockifyr-side
  response directive (like delay/fault, which the adapter already handles as WireMock-compatible
  fields where they exist and Mockifyr-only fields where they do not). The differential suites
  keep running unchanged and must stay green — that is the regression proof that "sandbox" did
  not fork "mock engine".
- **No platform creep.** Developer portal, self-registration, billing, OAuth server, hosted SaaS
  are explicit non-goals. The dashboard remains the single admin surface.

## Decision

### A resource is a domain value; state is a directive, not engine logic

`Mockifyr.Core` gains a pure model:

- `ResourceDocument` — `Id`, `Collection`, `Body` (JSON text; Core never parses it), `CreatedAt`,
  `UpdatedAt`, `Version`.
- `IResourceStore` — tenant- and collection-scoped `Create`/`Get`/`Put`/`Delete`/`List`/`Reset`
  with bounded capacity (ring-buffer eviction, oldest first — same semantics as `IMessageStore`).
  No tenant-less overload.
- An id-generation seam (`IResourceIdGenerator`) so tests can be deterministic (sequential ids)
  while the default is UUID.

Stub responses gain an optional **`state` directive** — a sibling of delay/fault, applied by the
facade after matching, never seen by the engine:

- Operations: `create`, `read`, `update`, `delete`, `list` (and `reset` for test hygiene) on a
  named collection. Inputs come from the request through the existing template context (body,
  path variables, query).
- The operation result is exposed to templating as `{{state.*}}` (the stored document, the list,
  the generated id), so the response body renders live data with the machinery G2 already built.
- Misses behave like a real API: `read`/`update`/`delete` on an unknown id short-circuits to a
  configurable status (default 404) instead of requiring a hand-written fallback stub.

This is the smallest design that turns "stub server" into "mini backend": one directive, one
store, zero engine changes.

### OpenAPI import is an adapter at the edge

`Mockifyr.Adapters.OpenApi` mirrors `Mockifyr.Adapters.MappingJson`: an OpenAPI 3.0/3.1 document
in, ordinary domain stubs out. Paths become `urlPathTemplate` matchers, operations become method
matchers, response examples become response bodies, and example-less schemas synthesize bodies
with the existing Faker/templating helpers. Optionally, resource-shaped path pairs
(`POST /things` + `GET|PUT|DELETE /things/{id}`) emit a full stateful CRUD set wired to the
`state` directive — *spec in, working sandbox out*.

The OpenAPI dependency (`Microsoft.OpenApi`, MIT — Apache-2.0-compatible) lives only in the
adapter. Import runs through `/__admin/openapi/import` (CQRS in `Mockifyr.Application`, REST in
`Mockifyr.Facade.Admin`) and through the dashboard. Imported stubs are ordinary mappings —
listable, editable, exportable, oracle-compatible.

### Sandbox access = API keys as a tenant-resolution source, plus quotas

Opt-in (`--sandbox-auth`): per-tenant API keys, stored hashed at rest behind `IApiKeyStore` in
Core, managed via `/__admin/apikeys`. The HTTP facade resolves the tenant from the presented key
(`X-Api-Key` or `Authorization: Bearer`) **ahead of** the existing host/header resolution — an
extension of the ADR 0003 chain, not a parallel mechanism. Optionally a per-key request quota
(requests per window) is enforced at the facade with a realistic `429` and rate headers; usage
counters are queryable through the admin API. gRPC/GraphQL/WS inherit this for free because they
ride the same HTTP facade; SMTP keeps its own AUTH-as-tenant rule (ADR 0009).

Non-goals restated: no user accounts, no self-registration, no billing, no OAuth issuance. A key
is an operator-issued credential that scopes traffic to a tenant — nothing more.

### The dashboard gets a Sandbox section

A new sidebar group **Sandbox** between *Mocking* and *Platform*:

- **Resources** — browse collections and documents per tenant, inspect/edit/delete, reset a
  collection, import seed data (JSON array → collection).
- **Access** — issue/revoke API keys, set quotas, read usage counters.

The Add-stub channel chooser (ADR 0010) gains an **Import OpenAPI** entry, and the dashboard
gets a quick-start path ("spin up a sandbox": import spec → seed data → issue key → copy base
URL). Existing screens are unaffected.

### Validation without an oracle

Per vertical, recorded in `docs/parity/g19-sandbox.md`:

- **State**: a real `HttpClient` drives POST → GET → PUT → LIST → DELETE end-to-end against
  imported/authored stubs; unit tests for tenant isolation, eviction and id generation; Stryker
  mutation testing on the directive and quota logic (the G18 bar: no surviving mutants in the
  new logic).
- **OpenAPI import**: golden-file round-trips against curated public specs (e.g. petstore and a
  real-world-sized spec), then *serve* the imported stubs and assert responses — import claims
  are proven by serving, not by inspection.
- **Access**: two keys → two tenants → provably isolated stubs/resources; quota-window unit
  tests.
- **Parity regression**: the existing differential suites stay green throughout — the proof that
  the sandbox vertical did not move the WireMock-parity surface.

## Consequences

- Mockifyr becomes usable as a **self-hosted integration sandbox platform** (banking, enterprise
  service virtualization, e-commerce payment/shipping/OTP flows) while remaining, byte-for-byte,
  a WireMock-compatible mock engine. Every G19 surface is opt-in; existing users see no change.
- New projects at the edges only: `Mockifyr.Adapters.OpenApi`; state/access live behind Core
  contracts with facade-applied directives (no new facade-to-facade dependency, arrows still
  point inward).
- One new edge dependency: `Microsoft.OpenApi` (MIT), never referenced by Core.
- The resource store starts in-memory and bounded; durable persistence reuses the G16 seam later
  if demanded — explicitly deferred and recorded, same as the message store.
- Branding: shipped under the Mockifyr name; a product-line label ("Mockifyr Sandbox") is a
  docs/marketing choice for later, not a code concern.
- Deliberate ordering: state (G19a/b) before import (G19c) before access (G19d) before UI
  positioning (G19e) — each vertical is independently valuable to today's mock users even if the
  sandbox positioning never sells. Portal-style features stay out until real demand exists.
- Deferred edges tracked from day one: durable resource persistence, GraphQL SDL / AsyncAPI
  import, per-key scenario isolation, OpenAPI *export* of authored stubs.

## Enterprise-readiness addendum (binding acceptance criteria)

Audited before implementation started. These criteria are part of each vertical's definition of
done — in addition to, never instead of, the binding test contract in `docs/testing.md`. A
vertical that ships without its row here is not done.

### Security (binds G19a, G19d)

- **A sandbox key never grants admin access.** `/__admin/*` ignores `X-Api-Key` and
  `Authorization: Bearer` entirely; admin auth remains `--admin-user`/`--admin-pass`. Proven by a
  wire self-test that presents a valid sandbox key to the admin API and is refused.
- **Key material spec**: ≥256-bit CSPRNG value with a recognizable prefix (`mfk_`); shown exactly
  once at issue time; stored only as a salted hash; compared in constant time; never written to
  logs, the journal, or error messages. After issuance only the key id/prefix appears anywhere.
- **Keys survive restarts.** `IApiKeyStore` rides the G16 persistence seam from day one (G19d) —
  an operator-issued credential that vanishes on redeploy is not a credential. (Resources stay
  in-memory-first as decided; *that* deferral is about test data, not credentials.)
- **Quota enforcement is race-free**: N parallel requests across the limit boundary never admit
  more than the budget (parallel wire test). Window semantics: fixed window first, stated in
  `X-RateLimit-Limit`/`X-RateLimit-Remaining`/`X-RateLimit-Reset` and `Retry-After` on 429.

### Data-plane robustness (binds G19a, G19b)

- **Per-document size cap** (default 1 MiB, flag-tunable) answered with an honest 413; collection
  capacity via ring-buffer eviction as designed. Both stated in docs and surfaced in the UI.
- **`Version` semantics decided**: last-write-wins by default; conditional update (`If-Match`
  style) is a tracked deferred edge, not a silent absence.
- **`/__admin/resources` lists are paginated from day one** (the journal's pagination pattern) —
  a 10k-document collection must not melt the admin API or the dashboard.
- **Resource bodies are opaque text**: Core never parses them, they re-serve verbatim, and the
  dashboard renders them only in the sandboxed viewers (XSS posture identical to the journal).
- **Concurrent CRUD on one document is safe** (no torn state, no store corruption) — covered by a
  parallel unit test against the store contract.

### Import safety (binds G19c)

- **No SSRF surface**: the OpenAPI importer never fetches remote `$ref`s. An external reference
  fails the import with a typed error naming the offending pointer; local (in-document) refs
  resolve normally.
- **Spec-bomb guards**: document size and schema-recursion depth limits produce a 422 — an import
  can be rejected, it can never hang the host or exhaust memory.
- **Imports are transactional**: on any error, nothing is partially created.

### Compliance & operability (binds all verticals)

- **Test-data-only contract stated where it matters**: like environments, resources are plaintext
  by design; the docs and the seed-import UI both say "no production personal data". (Sector
  compliance — banking/health — is satisfied by *not putting regulated data in*, and saying so.)
- **Config convention holds**: every new flag is also readable as an environment variable.
- **Export/import round-trip**: bundles containing `state`-directive stubs export and re-import
  losslessly (the #198 bundle machinery), and the differential suites prove the dialect surface
  did not move.
- **UI DoD**: all six locales, both themes, keyboard-reachable controls, confirm-dialogs on
  destructive actions (reset collection, revoke key), in-browser verification.

### Binding test matrix (per vertical)

| Vertical | Unit | Wire/integration | End-to-end | Mutation (Stryker) | Edge sweep |
|---|---|---|---|---|---|
| G19a | store: tenant isolation, eviction, caps, deterministic ids, concurrent CRUD | `/__admin/resources` CRUD + pagination + seed import + 413 | — | store logic, 100 % | hostile ids/unicode, empty/missing, volume |
| G19b | directive parsing, miss statuses | directive applied only when present (zero-change proof) | real `HttpClient` drives POST→GET→PUT→LIST→DELETE | directive logic, 100 % | unknown id, empty body, size cap, concurrency |
| G19c | generator golden files (petstore + real-world spec) | import endpoint: typed 422s | **serve** the imported stubs and assert responses | generator logic, 100 % | spec bombs, external `$ref`, empty/huge specs |
| G19d | key hash/compare, quota window math | resolution chain order, admin-API refusal, persistence across restart | two keys → two tenants provably isolated; parallel quota boundary | key + quota logic, 100 % | missing/garbled/revoked key, header casing, clock edges |
| G19e | — | — | in-browser: the full quick-start driven end-to-end | — | UI checklist (`docs/testing.md`) |
| all | — | — | — | — | **differential suites stay green, untouched** |
