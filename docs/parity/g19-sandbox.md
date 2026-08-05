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

## G19c — OpenAPI import

**What shipped.** `Mockifyr.Adapters.OpenApi` (Microsoft.OpenApi.Readers, MIT, edge-only — never
referenced by Core): OpenAPI 3.0/3.1 (JSON or YAML) in, ordinary mapping JSON out, imported
through `POST /__admin/openapi/import` and the Add-stub **OpenAPI** channel in the dashboard.
Paths become `urlPathTemplate`/`urlPath` matchers, declared examples serve as-is, example-less
schemas synthesize samples (`SchemaSample`), and with `?stateful=true` resource-shaped path pairs
(`/things` + `/things/{id}`) emit a G19b state-wired CRUD set — spec in, working sandbox out.

**Decisions worth remembering.**

- **Dialect compliance by construction**: the generator emits mapping JSON strings and the import
  handler feeds them through the SAME `MappingJsonReader` as any bundle — an imported stub cannot
  exist outside the dialect, and the differential suites keep proving that dialect.
- **SSRF is impossible by construction** (addendum): external `$ref`s (URL or file) are refused
  before parsing with the offending pointer named, and the reader only ever resolves local
  references — nothing is fetched.
- **Spec bombs bounce**: a 5 MiB size guard before parsing, a 32-level schema-recursion guard
  during synthesis (cyclic `$ref`s hit it) — typed 422s, never a hang.
- **Import is transactional**: every generated mapping parses before anything is stored.
- **Response selection**: lowest 2xx, then `default` (as 200), then the lowest declared;
  `application/json` wins among content types; the chosen content type rides into the stub.
- **Faker-backed synthesis**: string formats map to existing helpers (`uuid`, `email`,
  `uri`/`url` → `{{randomValue}}`/`{{random}}` expressions; the stub opts into templating), dates
  stay deterministic ISO stamps, other primitives are fixed samples, enums take their first value.
- **Golden files are the contract**: the committed `*.golden.jsonl` fixtures pin every literal of
  the generated output byte-for-byte (petstore + a real-world-shaped orders YAML, stateful
  included).

**Validation story.** `G19cOpenApiGeneratorTests` (goldens + generation table + all five typed
refusals), `G19cSchemaSampleTests` (format map, exact depth boundary, allOf, corners),
`G19cOpenApiImportTests` (wire: import then SERVE — declared examples verbatim, synthesized
Faker/uuid values live, the stateful CRUD loop end-to-end from YAML, typed 422s, transactionality,
imported stubs listable as ordinary mappings). **Stryker: 97.3 %** on the generator pair (178/183);
the five survivors are analyzed equivalents, recorded here per the contract:

- `content.First()` → `FirstOrDefault()`: unreachable difference — guarded by `Count > 0`.
- `lastSlash <= 0` → `< 0` and the second `||`→`&&` on the pair-detection guard: an empty
  collection path can never exist in `Paths` (OpenAPI paths start with `/`), so every observable
  outcome is identical.
- `StringBuilder(capacity ± 1)`: a capacity hint, no behavior.
- `name.Length > 64` → `>= 64`: slicing a 64-char string to 64 is the identity.

The pre-existing differential suites pass untouched — the parity surface did not move.

**Deferred (tracked).** GraphQL SDL / AsyncAPI import; OpenAPI *export* of authored stubs;
`examples` (multi-example) rotation; request-body-aware matchers from `requestBody` schemas.

---

## G19d — Sandbox access: API keys + quotas

**What shipped.** Opt-in `--sandbox-auth`: `mfk_`-prefixed 256-bit CSPRNG tokens issued once via
`POST /__admin/apikeys` (the response is the only time the token exists — only a 12-char display
prefix survives), stored as salted SHA-256 (`base64(SHA256(salt + "\n" + token))`, verified with
`CryptographicOperations.FixedTimeEquals`), tenant-scoped listing (`usedThisHour` joined from the
limiter) and tenant-checked revocation. A presented key (`X-Api-Key` or `Bearer`) resolves the
tenant AHEAD of the ADR 0003 host/header chain; no credential falls through to the legacy chain,
an invalid credential is an honest 401 — never a silent cross-tenant fall-through. Optional
per-key hourly quota: fixed-window, exact under a lock, `X-RateLimit-Limit/Remaining/Reset` on
counted responses and `Retry-After` on the realistic 429. Keys persist through the G16 seam
(FileSystem `<root-dir>/apikeys/`, LiteDB `apikeys`, Postgres `apikeys` table, Redis
`mockifyr:apikeys` hash) and rehydrate at startup.

**Decisions worth remembering.**

- **Salted SHA-256, not a KDF** — deliberate: the secret is a 256-bit random token (not a human
  password), so brute-force is information-theoretically hopeless and a KDF would only tax the
  serve hot path. The salt still provides per-key domain separation; the `"\n"` separator in the
  preimage is load-bearing (without it `("ab","c")` and `("a","bc")` would collide) and is pinned
  by a format-contract test because persisted hashes must verify across versions.
- **A sandbox key never reaches `/__admin/*`** (addendum): the admin surface only ever accepts its
  own Basic auth; both `Bearer mfk_…` and `X-Api-Key` are refused with 401. Data-plane and
  control-plane credentials never blur.
- **Quota ≤ 0 means unlimited**, never an instantly-exhausted key; unlimited keys emit no rate
  headers (`Limit 0` suppresses them).
- **Usage counters are in-memory by design** — the credential persists, the hourly counter resets
  on restart (documented, not accidental).
- **Stryker survivor (equivalent, 28/29 killed):** `DisplayPrefix`'s `token.Length <=
  DisplayPrefixLength` mutated to `<` — when the length is exactly 12, `token[..12]` IS the token,
  so both branches return the same string; no observable difference exists. The 6 CompileErrors
  are impossible mutants (string `-` operator; tuple member `.Count` "mutated" to LINQ `.Sum`).
  The out-var-in-compound-condition restructure (the EnvironmentJsonReader lesson) was applied to
  the limiter so its condition mutants compile and are genuinely tested.

**Validation story.** No oracle exists (WireMock has no sandbox-key concept), so per the G18
precedent: `G19dApiKeyTests` (10 unit tests — token shape/uniqueness, constant-time verify incl.
wrong-salt, the pinned hash format, exact window boundaries with a `TimeProvider` test clock,
window rollover invisible to `Used()` before the next request, 8×50 parallel requests against a
budget of 100 admitting exactly 100, FileSystem + LiteDB round-trips incl. garbage skip) and
`G19dSandboxAccessTests` (5 wire self-tests against a REAL `MockifyrHost.Build` host with
`--sandbox-auth` + admin Basic + `--root-dir`: key-ahead-of-chain with provable tenant isolation
and key-wins-over-contradicting-header, legacy chain untouched without credentials + garbled-key
401 + tenant-checked revoke (cross-tenant delete is 404, no existence oracle) + revoked-key 401,
admin refusing sandbox keys on both carriers + listing exposing prefix but never token/salt/hash,
sequential rate headers then 60 parallel requests across a quota of 40 yielding exactly 39 more
200s / 21 429s + `Retry-After` + `usedThisHour=40`, and issued keys surviving a full host
restart). Discovered while testing: per-tenant stubs on the FILE backend are not rehydrated on
restart (`DirectoryMappingsLoader` only reads the top level for the default tenant) — a
pre-existing G16 edge, tracked separately, out of this vertical's scope.

**Deferred (tracked).** Key expiry (`expiresAt`) and rotation; per-key scopes (read-only keys);
quota windows other than hourly; usage counters surviving restarts; per-key scenario isolation.

---

## G19e — Sandbox UI + positioning

**What shipped.** The dashboard grew a **Sandbox** sidebar group between Mocking and Platform:
**Resources** (collections rail + paged document table, JSON document editor with client-side
validation mirroring the server's guards, seed-from-array dialog, per-collection and global reset
with confirmations) and **Access** (issue/revoke keys, per-key quota entry, a one-time token
reveal dialog with a copy affordance and an explicit "shown only once" warning, usage bars
`used/quota` that warn at 80% and turn danger at 100%). The dashboard gained a "Spin up a sandbox"
quick-start strip (import spec → seed data → issue key → copy base URL). Both pages are
tenant-scoped through the same admin header as every other screen, close all edit/confirm state
on a tenant switch (the #199 lesson), render RTL for Arabic, and shipped in all six locales.

**Decisions worth remembering.**

- **The token is UI-honest about show-once**: the reveal dialog is the only place the token ever
  exists client-side; the listing renders `prefix…` and nothing else, matching the server, which
  cannot re-reveal it.
- **Client-side validation mirrors, never replaces, the server guards**: collection/id pattern
  (`[A-Za-z0-9_-]{1,64}`), well-formed-JSON body, array-shaped seed — typed server errors still
  surface as toasts when they disagree.
- **Tables scroll inside their card** (`overflow-x-auto`) so narrow panes clip nothing — the
  width-optimization rule from #200 applied from day one.

**Validation story.** UI-only vertical — no engine change, so no oracle question arises. Verified
in-browser against a real `--sandbox-auth` host end-to-end: seeded `orders` via the admin API and
browsed/edited it in the UI (a dialog save advanced the server-side document to `version: 2`);
issued a 50/hour key through the dialog and used the revealed token on the wire — three live
requests answered 200 with `X-RateLimit-Remaining` counting down, after which the Access table
showed `3 / 50`; confirmed the Turkish locale renders the full Access screen. `tsc`, `oxlint`
(no new warnings), and the production build are clean.

**Deferred (tracked).** Copy-as-curl on the token reveal; per-key usage history (needs
server-side counters that survive restarts, deferred with G19d's); a collection-level document
search box.

## Durable sandbox resources (post-1.0)

The deferred edge that sat worst with calling this an integration sandbox: resources lived in memory
only. A partner seeds their fixtures, the pod restarts, and their data is gone. Backup and restore
(#252) covered the deliberate case; this covers the one nobody plans for.

`IResourcePersistence` + `IResourcesLoader` mirror the stub and environment seams exactly, with all
four G16 providers implemented — file system (`<root-dir>/resources/`), LiteDB, PostgreSQL and Redis —
and rehydration at startup. No `--root-dir` or backend still means in-memory only, so a laptop run
writes nothing: durability follows the persistence choice rather than becoming a new default.

Decisions worth remembering:

- **Persisted after the store accepts the write**, so what survives is exactly what the store holds,
  including the `CreatedAt`/version bookkeeping a replace works out. And a delete is persisted only
  once the store agrees the document existed — persisting a delete for something that was not there
  is a write nobody asked for.
- **Deletes and resets persist too.** Saving creates but not removals is the classic half-implementation:
  the document rises from the dead on the next deploy, and a reset "un-resets" itself. Each has its own
  restart test, on every backend.
- **Rehydration goes through the store's own `Put`**, not a bulk restore, so a reloaded document is
  indistinguishable from a freshly created one — including the per-collection bound, which a bulk load
  could walk straight past.
- **Ids are caller-chosen and are not path segments.** Whoever seeds a sandbox picks the ids, so the
  file provider percent-escapes anything outside `[A-Za-z0-9._-]` — and the escaping is injective, so
  `a/b` and a literal `a%2Fb` stay separate documents rather than one silently replacing the other.
  A tenant, collection or id of `..` cannot produce a path component that leaves the store. The tenant
  and collection are read back from the stored document, never un-escaped from a directory name, so
  there is one encoding to keep correct rather than two.
- **Postgres keys on `(tenant, collection, id)`** — tenant isolation expressed in the schema, not only
  in the code above it. A restart test puts the same document id in two tenants and checks each comes
  back where it belongs, because that damage would only ever show up after a restart.

Validation: 18 unit tests on the file provider (most of them hostile names), 6 wire tests on a real
host covering seed/delete/reset/tenant-isolation/odd-ids/no-persistence, and one restart test per
backend added to the existing G16b/c/d provider suites. **Stryker on the file provider: the LiteDB,
Postgres and Redis paths are covered by their provider tests rather than by unit mutation, matching how
G16 has always validated them — a backend cannot be mutation-tested without the backend.**

Still deferred: change-feed reload for resources (a second replica does not learn about another's
writes until it restarts), and per-key scenario isolation.


## Contract conformance — `POST /__admin/openapi/verify` (#287, post-1.0)

- **Group / item:** post-roadmap platform feature, the first slice of #287 — **self-tested**; the
  reference engine has no conformance surface, so there is no oracle.
- **The problem.** The deepest failure mode of mocking is not a bug in the mock — it is the mock being
  *confidently out of date*. The upstream adds a required field, tightens a status, drops an endpoint;
  the stubs do not move; every test stays green; production breaks. Mockifyr already had both halves of
  the answer — it can read an OpenAPI document (G19c) and it holds the stubs — and did not join them.
- **What it reports.** Four kinds: a stub answering an operation the specification no longer declares;
  an operation no stub answers; a status the specification does not declare for that operation; and a
  response body that does not satisfy the declared schema, named by JSON pointer. Plus coverage counts,
  because "conforms" on an empty stub set is true and useless.
- **It reports, it never mutates.** Asserted on the wire (the stub set is byte-identical afterwards).
  Which side is wrong is a judgement about the caller's system; a tool that "fixed" the drift itself
  would be making that judgement for them.
- **The same validator as `matchesJsonSchema`.** JsonSchema.Net, so a body that a stub would accept and
  a body the report calls conformant are judged by one implementation rather than two.
- **A templated body is left alone.** A template is not JSON until a request renders it; validating the
  template text would report drift on every templated stub, which is the fastest way to make a report
  people stop reading. The same instinct governs the other silences: a stub matching by regular
  expression, a message-channel stub, an operation whose schema the document omits.
- **Two ambiguities were made explicit rather than left to chance**, both found by mutation testing:
  - *Which field names the path.* The check now mirrors the engine's own precedence — `url`, then
    `urlPath`, then `urlPathTemplate`. It had them in the opposite order, so a mapping carrying more
    than one would have been reported confidently against an endpoint it was not serving.
  - *Which operation a stub belongs to* when several agree. A specification may declare both
    `/orders/new` and `/orders/{id}`; the literal wins, by counting wildcards rather than characters.
    Equal wildcard counts fall back to ordinal path order — arbitrary, but *stable*, which is what a
    report wired into CI needs.
- **The round trip is asserted**: stubs the importer generated from a document must verify clean
  against that same document. If the two halves disagreed, a user would have no way to tell which one
  was lying.
- **Validation.** `ContractConformanceTests` (35 unit cases) and `ContractVerifyTests` (8 wire cases,
  including the round trip, tenant isolation, both refusals, and the no-mutation assertion).
  **Stryker 90.3 %**, 7 survivors, all analysed as equivalent:
  - `OrderBy` → `OrderByDescending` on the specification's paths: enumeration order no longer decides
    anything now that operation selection is explicit, and findings are sorted before they are returned.
  - Two tie-break reorderings in the findings sort: reachable only for two findings sharing a path *and*
    a method and differing by kind, which the control flow cannot produce (an undeclared status stops
    before a schema check).
  - `Count <= 1` → `< 1` and its ternary: a fast path; ordering a single-element list is the identity.
  - `character == '{'` → `!=`: counts non-wildcards instead of wildcards, which orders identically for
    every pair the check can be handed — the two paths must already agree with the same stub, so they
    have the same segment count.
  - The `Flush()` before reading a `StringWriter`: the reader flushes.
- **Deferred, and recorded on the issue rather than here as done:** journal-vs-spec (what clients
  actually sent, against what the contract allows) and recording-vs-stubs (drift against reality rather
  than against a document). Both reuse this conformance engine; neither is written yet.


## Traffic conformance — `POST /__admin/requests/verify` (#287, third slice)

- **Group / item:** post-roadmap platform feature, the last slice of #287 — **self-tested**.
- **The mirror of the stub check.** That one asks whether the mock still describes the API; this asks
  whether the **consumer** is staying inside it. A client calling an endpoint the contract never
  promised, omitting a required parameter, or sending a body the schema forbids works perfectly
  against a mock that is more permissive than the real service — and fails the first time it meets the
  real one. Three findings: `undeclaredOperation`, `missingParameter`, `requestSchemaViolation`.
- **Reads the journal; changes nothing.** Verifying twice reports the same numbers, asserted — a check
  that journaled its own probing would grow its own input.
- **One engine, two directions.** `ParseSpec`, `OperationsOf`, `FindOperation` and the schema-failure
  walk are shared with the stub check, so both credit a request to the *same* operation under the same
  ambiguity rules. Two reports about one document that disagreed on which operation a path belongs to
  would be worse than either alone. A wire test drives conforming traffic through an imported spec and
  asserts both checks answer clean.
- **Silences, again on purpose.** A body on an operation that declares none is odd, not a violation. A
  header name is matched case-insensitively, because HTTP says so and reporting `x-tenant` as missing
  when the contract spells it `X-Tenant` would be a false finding on correct traffic. A percent-encoded
  parameter name is decoded before the comparison.
- **Path parameters are never "missing"** — the URL matching the template at all is what satisfies
  them. Cookies cannot be checked at all: the journal keeps header *names*, and individual cookies live
  inside one `Cookie` header. Stated rather than silently approximated.
- **Counts, not just a verdict.** "Conforms: false" says nothing about scale; two of three requests
  passing is a different morning from none of three.
- **Validation.** `TrafficConformanceTests` (23 unit cases) and `TrafficVerifyTests` (8 wire cases,
  including tenant isolation and the both-checks-agree round trip). **Stryker 79.2 %**, 4 survivors,
  each analysed as equivalent:
  - The `"cookie"` and `"path parameter"` labels: unreachable, because neither location can be reported
    missing — the switch above them answers `true` for both.
  - `index < 0` → `<= 0` when looking for the query separator: differs only for a URL that *starts* with
    `?`, which a journaled request cannot be.
  - Emptying the `return []` branch: cannot compile (no return path).
- **Discovered while testing:** the schema validator reports absent required properties as **one**
  error naming them all, not one per property. The first version of the cap test therefore asserted
  nothing; it now uses several separately wrong types, which is what actually reaches the cap.
