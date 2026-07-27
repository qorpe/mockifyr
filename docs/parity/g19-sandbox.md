# G19 — Integration sandbox (stateful resources, OpenAPI import, access)

No WireMock counterpart exists for any G19 surface, so there is no oracle to diff against. Per the
standing rule (G18 precedent) the validation method is stated up front per vertical: unit +
handler-level self-tests, wire self-tests over a real Kestrel host, Stryker mutation testing on the
new pure logic (target 100 %), and the **existing differential suites staying green untouched** —
the proof that the sandbox vertical does not move the WireMock-parity surface. Binding acceptance
criteria live in ADR 0011's enterprise-readiness addendum.

## G19a — Resource model + store + admin API

**What shipped.** `ResourceDocument` / `IResourceStore` / `IResourceIdGenerator` /
`ResourceOptions` in Core (pure, zero deps); `InMemoryResourceStore` (tenant- and
collection-scoped, ring-buffer eviction, injected `TimeProvider`); `/__admin/resources` CQRS +
REST: collections listing, paginated list, get, put, delete, per-collection and per-tenant reset,
seed import. Flags: `--resource-limit` (per-collection bound, default 1000) and
`--resource-max-body` (per-document cap, default 1 MiB).

**Decisions worth remembering.**

- **Bodies are opaque text.** Core never parses them; the management edge validates well-formed
  JSON (422) and the byte cap (honest 413) via one shared `ResourceRules`, and documents
  round-trip byte-for-byte (proven with unicode/emoji bodies over the wire).
- **Last-write-wins replace** keeps `CreatedAt` *and the insertion position* and advances
  `Version`/`UpdatedAt` — a listing must not reshuffle on update. Conditional update
  (`If-Match`-style) is a tracked deferred edge.
- **Updates never evict.** Only a create can overflow the ring buffer, so a full collection being
  edited stays intact.
- **Empty collections disappear from the listing** (deletes can empty one) — the collections view
  reports what exists, not bookkeeping residue.
- **Ids are opaque keys** (1..256 chars, no control characters) — unicode, spaces, and
  traversal-looking strings round-trip as data, never interpreted.
- **Seed import is transactional**: every item is validated before anything lands; absent ids come
  from the `IResourceIdGenerator` seam (deterministic in tests, UUID by default).
- **Unknown collections list as honest empty pages**, not 404s — mirrors "no rows yet", which is
  what a sandbox operator means.

**Validation story.** `G19aResourceStoreTests` (create/replace semantics against an injected
clock, tenant + collection isolation, eviction, capacity guard, hostile ids, concurrency — 8
writers × 250 documents), `G19aResourceHandlerTests` (exact boundaries: collection name 64, id
256, body cap at-and-over in UTF-8 bytes incl. multi-byte, seed transactionality, pagination
clamps), `G19aResourcesAdminTests` (wire: CRUD round-trips, pagination, seed with explicit and
generated ids, tenant scoping via header, 404/413/422 surface, reset scopes). **Stryker: 100 %**
on both new pure-logic files (`ResourceRules` 24/24, `InMemoryResourceStore` 24/24). Full
differential suite green throughout (201 tests, none touched).

**Deferred (tracked).** Durable resource persistence via the G16 seam (in-memory-first is the ADR
0011 decision — resources are test data; the G19d key store is the one that must persist);
conditional updates; per-key scenario isolation (G19d+).

## G19b — State directive + templating

**What shipped.** The opt-in `state` response directive: `{"operation":"create|read|update|delete|
list","collection":…,"id":…,"document":…,"missStatus":…}` on a stub response turns a match into a
sandbox CRUD operation, with the result exposed to templating as `{{state.id}}`, `{{state.body}}`,
`{{state.version}}`, `{{state.count}}`, `{{state.list}}`. `id`/`document` are template expressions
rendered against the request; an absent create id comes from `IResourceIdGenerator`, an absent
document is the request body verbatim.

**Decisions worth remembering.**

- **The engine never sees state.** `StateDirective` is pure data on `ResponseDefinition` (a sibling
  of delay/fault); the *templating renderer* applies it — the engine keeps calling the same
  `IResponseRenderer` seam. `StaticResponseRenderer` ignores it by design (state needs templating).
- **Declaring the directive IS the templating opt-in** — no separate `response-template`
  transformer needed; without it `{{state.*}}` could never render.
- **Misses short-circuit like a real API**: read/update/delete on an unknown (or unrendered) id
  answers the configurable `missStatus` (default 404) with an empty body — no template renders
  over nothing, and no store lookup happens for a blank id.
- **The serve-time guards reuse `ResourceGuards`** (one definition with the admin path): an over-cap
  document is 413, non-JSON is 422, and an unknown operation or malformed collection name is 422 —
  nothing half-lands.
- **Handlebars syntax edge**: `{{state.body}}}` (a JSON object closing right after the expression)
  breaks the Handlebars parser (triple-stache). Put a space before the closing brace —
  `{{state.body}} }` — same as any Handlebars-in-JSON template.
- **The admin surface and the serve path share the store** — a document created by a stub is
  immediately visible under `/__admin/resources`, and vice versa.

**Validation story.** `G19bStateDirectiveTests` (wire, real `HttpClient`): the full loop
POST→GET→PUT→LIST→DELETE against authored stubs, generated-id capture, admin-surface agreement,
configurable miss status, tenant isolation of state, serve-time 413/422, and a state-free stub
proving zero behavior change. `G19bStateApplierTests` (12 unit tests): the per-operation semantics
table, boundaries, dispatch case-insensitivity, and the no-id-no-lookup contract via a probe
store. **Stryker: 100 %** on `StateDirectiveApplier` (44/44); `ResourceRules` re-verified at 100 %
after adopting the shared `ResourceGuards`. The pre-existing differential suites pass untouched —
the parity surface did not move.

**Deferred (tracked).** Query/filter parameters on `list`; `{{state.*}}` in webhook templates;
per-key scenario isolation (G19d+).
