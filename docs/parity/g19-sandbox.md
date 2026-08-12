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

---

## Relations (ADR 0015, issue #350)

A defect, not a missing feature — and reachable through the path the quick-start recommends. A spec
containing `/customers`, `/customers/{customerId}` and `/customers/{customerId}/orders` imported to a
flat global `orders` collection with nothing recording who owned a document, so every modelled
customer listed every other customer's orders, an order could be created under a customer that did
not exist, and deleting a customer left its orders behind. Two lines produced all four:
`CollectionName` took the last path segment, and `state.list` returned `store.List(tenant,
collection)`.

**What was learned, and what changed because of it:**

- **The name was never the problem.** The obvious fix — calling the collection `customers-orders` —
  is wrong: a spec may expose the same resource both ways (`/orders` and `/customers/{id}/orders`),
  and in a real API those are one collection with one id space. The collection keeps its name; the
  relation is what was missing.
- **Where the key lives is the contract's decision, not ours.** Stamping the parent id into every
  stored document is what json-server's data looks like, but json-server's documents are authored by
  the user while ours arrive from a request against an imported spec. If the spec's `Order` schema
  declares no `customerId`, adding one means `POST /__admin/openapi/verify` (#287) reports our own
  sandbox as drifted from the document we generated it from. So: body when the contract declares the
  field, an optional metadata pointer otherwise, and one accessor answering for both.
- **`onDelete` defaults to `restrict`.** Deleting a Stripe customer cancels their subscriptions and
  leaves their charges, with the customer still retrievable as `{"deleted": true}`. Cascade as a
  default would give an imported spec destructive behaviour the API it models does not have — a *less*
  faithful sandbox.
- **Enforcement is presence-triggered.** A key that is present must resolve; an absent one is not
  checked. Mandatory relations would make two mutually referencing collections impossible to populate,
  because neither can be created first. This is also why cycles in the relation graph are legal —
  `employees.managerId → employees` is a real model — and why a cascade terminates through a visited
  set rather than through a rejected declaration.
- **A missing parent named by the route is 404; one named by the body is 422.** They are different
  failures and collapsing them misreports one of the two.

**Found by serving rather than by reading.** The generated `Location` header was composed from the
specification's template text, so a nested collection answered
`/customers/{customerId}/orders/<id>` — a Location containing a literal brace, which no client can
follow. Present since G19c and invisible to every unit test, because the two forms agree for a
top-level collection. It is now built from the request's own path.

**Where relations are kept.** As documents in the resource store, under the reserved collection
`!relations`. The alternative was a persistence surface of its own: a writer and a loader for each of
four backends, mirroring ~400 lines that already do exactly this. Riding the resource store means
relations persist, restore, export and reload through the change feed everywhere with no per-backend
code — and it is not a convenience: relations held only in memory would vanish on restart while their
documents survived, and a scoped list would quietly answer with the whole collection again. The
reserved name deliberately fails `ReservedEnvironmentKeys.IsWellFormed`, which every user-facing path
applies, so a colliding collection cannot be created rather than merely being discouraged.

**Compatible by construction, not by care.** The parent pointer is optional so documents written by
earlier versions stay valid; scoping happens only where a relation is declared; integrity applies only
to declared relations. All three are asserted, not assumed.

**Validation.** No oracle has a sandbox resource model, so a self-test throughout:
`RelationalResourceTests` (35 unit cases over the pure decisions and the reserved-collection
isolation), `RelationalStateDirectiveTests` (10 over the directive), and `RelationalSandboxTests`
(9 wire cases that import a nested spec and then **serve** it — import claims proven by serving, the
G19c rule). The differential suite stayed green at 425 untouched, which is the proof that relations
did not move the parity surface.

**Stryker 100 %** on `Relations.cs`, reached from 80 % — and the 20 % it found was not decoration:

- **A customer with no orders was untested.** Every scoping test had at least one match, so returning
  `null` instead of an empty list survived undetected. The commonest case in any sandbox, and `null`
  here is a `NullReferenceException` at serve time rather than an empty page.
- **The named relation was never proven to be the one used.** A collection can belong to two things;
  nothing asserted that scoping by one is not satisfied by the other's key, which is how a supplier's
  order would appear under a customer's route. Both the list side and the cascade side had the gap.
- **The order of a refusal was arbitrary.** Two relations can block one delete, and nothing pinned
  which is reported first — including the tie-break when both name the same collection through
  different fields (an order belonging to a customer as buyer *and* as payer).
- **`MaxCascadeDepth` had an untested boundary.** `>` versus `>=` differ by one level, so the wrong
  bound is a delete that reports success and silently leaves a level of children behind. Only an exact
  count over a chain deeper than the limit can tell the two apart, so the test builds one.

None of the five survivors turned out to be an equivalent mutant, which is the useful outcome: the
suite was measuring the paths that were easy to write rather than the ones that fail.

**The dashboard shows them (#350 follow-up).** A scoping rule you cannot see is one you debug by
guessing, and relations are usually *derived* at import rather than typed — so the first job is simply
to state them. A `BELONGS TO` strip sits under the collection header, beside the documents, because
"why is this list short" and "what is this collection scoped by" are the same question asked in the
same place. Editing is a small dialog over `/__admin/relations`, with `onDelete` spelled out as what
it does (refuse delete / delete children / leave children) rather than as the wire value.

Verified by driving the real screen: import a nested spec, watch the strip appear with the derived
relation, switch `onDelete` to cascade, save, and confirm that deleting a customer goes from **409**
to **204** and takes their order with it. Two of my own probes were wrong before that worked — a
synchronous DOM read that ran before React had rendered, and a programmatic `select` write that never
fired React's `onChange`, so a save persisted the unchanged value. Both looked exactly like a broken
feature. Recorded because the next person to automate this screen will meet the same two.

**Out of scope, deliberately** (ADR 0015): joins, cross-collection transactions, a query language,
schema migrations. A sandbox should behave like the API it stands in for, not become a database
harder to reason about than the service it replaces.

---

## The partner self-service surface (#347)

An `mfk_` key was checked **only on the serving path** and selected the tenant from the key. That is
the right design and it did not change. Its consequence was that a partner could *call* the mock and
see nothing else: not the OTP their signup flow just "sent", not the webhook they were delivered, not
why a call 404'd, and no way to reset their sandbox between runs. All of that lives on `/__admin/*`,
which the key deliberately cannot reach — and the only way to hand them any of it was a tenant admin
credential, which is precisely what #346 exists because is not partner-safe.

**A separate namespace, not a loosened one.** ADR 0011 makes it a binding criterion that *a sandbox key
never grants admin access* — `/__admin/*` ignores `X-Api-Key` and bearer tokens entirely, with a wire
test asserting it. Teaching that surface to accept a sandbox key for "just a few safe routes" would
have weakened an invariant someone may have relied on, and would have left the property true only by
inspection of a route list. `/__sandbox/*` stands beside it instead: the rule stays literally true, its
test stays green, and the boundary is something you can see rather than something you have to audit.
The test for this change re-asserts the ADR's criterion from the new side, so the two cannot drift.

**The tenant comes from the key and only from the key.** There is no `X-Mockifyr-Tenant` on this
surface — not a header that gets refused, but no header at all. Sending one naming another tenant is
inert rather than an error, which is a stronger property than refusing a forged value correctly: it is
not a check that could be wrong. Proven by asserting that the request still answers with the caller's
own documents.

**What it carries.** Reads: the journal, the inbox (including the OTP extraction that already existed,
because "the code you just sent me" as one GET is the whole reason a partner wants their inbox),
resources, and environment *keys* — never a secret literal, which is why #348 was taken first. Writes:
reset my resources, my inbox, my journal. Nothing that touches another tenant or the host.

**Absent, not open.** Without `--sandbox-auth` there is no way to tell one partner from another, so the
namespace is not mapped at all and answers 404 like any other route the host does not have. A surface
that existed but trusted everyone would be worse than none.

**Non-goals restated.** Not a developer portal and not self-registration — ADR 0011 ruled both out and
this does not reopen them. This is a scoped API for somebody who already holds a credential.

**Validation.** `SandboxSelfServiceTests` (13 wire cases) and `SandboxSurfaceWithoutAuthTests` (1).
The cross-tenant cases assert the positive first — that the caller sees their own document — so the
absence of the other tenant's means something rather than meaning the request failed.

---

## Querying a collection (#353)

`GET /__admin/resources/{collection}` took `limit` and `offset` and nothing else, and the serve-time
`list` returned the whole collection. A sandbox stands in for an API, and the APIs it stands in for
filter and sort — so `GET /orders?status=settled&_sort=-total` had to be faked with a hand-written stub
per query shape, which then drifts from the data underneath it.

**The vocabulary is the one the dialect already proves.** `?status=settled` is `equalTo`;
`?note:contains=x`, `?note:matches=^r.*h$` and `?note:absent=true` are the matcher names a stub author
already knows. Inventing a second vocabulary would make somebody learn the same idea twice and let the
two drift.

**Both surfaces, one evaluator.** The admin listing and the served `list` parse and apply the same
`ResourceQuery`. Two implementations would let the sandbox and the screen watching it disagree about
what a collection contains, which is worse than neither of them filtering.

**Decisions worth keeping:**

- **`total` means matching.** Filtering happens before paging, or the count disagrees with the pages
  under it and the paging control lies about how many there are.
- **Numbers sort numerically.** As text, `"9"` sorts after `"250"` — correct for strings and wrong for
  the column of totals people actually sort by.
- **A document missing the sort field goes last in either direction.** Absent is not "smallest", and
  surfacing it first in one direction would read as a bug in the data.
- **An unknown operator suffix is part of the field name.** `?created:at=x` is somebody filtering a
  field called `created:at`; refusing it for looking like a typo would refuse a legitimate query.
- **A regex filter cannot take the host down.** The pattern is caller-supplied, so it carries a
  100 ms timeout and an invalid pattern matches nothing rather than throwing into the serving path.
- **Field selection omits what the document lacks** rather than returning it as null — present-and-null
  is a claim the document does not make.
- **Still no query language** (ADR 0015): no joins, no cross-collection anything. Filter, sort, select.

**A trap worth stating.** Serve-time filtering only works if the stub matches on `urlPath`, not `url`:
in the mapping dialect `url` includes the query string, so `"/orders"` stops matching the moment a
caller filters — the request that most wants this feature is the one that would 404. Found by writing
the wire test with `url` and watching it return nothing.

**Validation.** `ResourceQueryTests` (24 unit cases) and `ResourceQueryWireTests` (7 wire cases that
ask the same question of both surfaces and compare the answers). **Stryker 94.87 %**, four survivors,
each confirmed equivalent:

- `All` constructed with `SortDescending: true`, and the local `descending` initialised to `true`:
  both are unreachable, because a sort direction is only read when a sort field exists, and one is
  always assigned alongside the other.
- Removing the guard that nulls an empty sort field: `SortField is not { Length: > 0 }` already treats
  `""` as no sort, so both paths sort nothing.
- Short-circuiting the empty-filter case: `Filters.All(...)` over an empty list is true, so the
  unfiltered branch and the filtered one return the same documents. The branch is an allocation
  optimisation, not a semantic.

---

## Named datasets (#351)

Seeding was per collection and literal. Two things followed. "The delinquent customer" is a customer,
three orders, two failed payments and a dunning record — across four collections and only meaningful
together, so it lived in somebody's shell script, which is the one place nobody else can find it. And
Faker had been in the box since G15, reachable only from response templates: a sandbox needing two
hundred plausible customers got two hundred hand-written ones.

**Two orderings, two different reasons.** Loading goes parent-first, because referential integrity
(ADR 0015) refuses a child whose parent does not exist yet — a dataset written child-first is loadable
only because the loader sorts it, and asking the author to sort it is asking them to know a relation
graph they did not write. Unloading goes the other way: `restrict` refuses to delete a parent while
children exist, so removing in load order would refuse on the first parent and leave the rest behind.

**Atomicity is a compensating rollback**, not a pretend transaction. Documents are written as they
render; any failure removes every one written so far. Integrity can only be checked against documents
that exist, so "validate everything then write everything" is not available — and a half-loaded dataset
leaves the sandbox in a state no scenario describes, with no way for the person who ran it to tell
which half they got.

**Unloading removes what THIS load created**, tracked by id. "Clear the collections it touched" would
take a colleague's work with it, and people would stop loading datasets.

**Loading twice leaves one copy.** It is the gesture people repeat between runs; two copies would make
the second run fail for reasons unrelated to the code under test.

**Seeded determinism, without a global.** Bogus offers `Randomizer.Seed`, and using it would have been
wrong: it is a process-wide static, so seeding it for a load would also make every concurrently served
response deterministic, and two overlapping loads would draw from each other's sequence. `FakerSeed` is
an ambient scope — the idiom `RenderClock` already uses for the tenant clock — with one generator per
scope, not one per value, or a seed would hand every document the same "random" name. Outside a scope
nothing changes, which is asserted rather than assumed.

**Two defects found by writing the tests:**

- Validation required a document template to be well-formed JSON *at declaration*. But `{"total":
  {{random 'Number.digit'}}}` is an ordinary template and is not JSON until rendered — the check
  refused every numeric helper outright. The guard belongs after rendering, where the loader applies it.
- The load handler asked for a `TimeProvider` the container does not register, so every load answered
  500. Caught by the first wire test, not by the build.

**Validation.** `DatasetTests` (24 unit cases on ordering and validation), `DatasetLoaderTests` (14 on
loading, rollback and unloading), `FakerSeedTests` (5 on determinism) and `DatasetWireTests` (9 wire
cases including load-twice and the reserved collections staying hidden). **Stryker: 97.30 % on the
model** (one equivalent survivor) and **100 % on the loader and the seed scope**.


## Quota that survives a second replica and a restart (#354)

**The claim that was not true.** A per-key hourly quota shipped with G19d, counted in a dictionary in
the serving process. Behind two replicas a partner got their number *per pod*, and a deploy in the
middle of an hour refunded whatever they had spent. A number that changes when we scale, or when we
release, is not a quota — and it is exactly the number an external partner is told they have.

**What shipped.** `IRateCounter` — increment-and-read for a key in a window — with the in-process
counter as the default and a Redis-backed one registered on top when `--redis` is set. Redis is
already one of this project's persistence providers, so a shared quota is a second use of a
connection an operator has configured rather than new infrastructure to run. The counter is a plain
`INCR` on a key naming the window's bucket: atomic across clients, which is the entire requirement.
The key expires after twice the window, because a counter is not a store and nothing here needs to
survive.

**Buckets are aligned to the epoch, not to the first request.** Two hosts that started at different
times must place the same instant in the same bucket, or the counter is shared in name only.

**A second window beside the per-key one.** `--rate-burst <n>/<seconds>` is a host-wide ceiling. Two
windows protect different things — a hundred requests in a second is a runaway loop, a hundred
thousand in a day is a consumer who should be paying — and one number cannot say both. It is
host-level rather than per key because it protects the host, and making every key restate it would
leave the one key nobody updated as the way in. It applies to a key with *no* hourly quota too:
"unlimited" is a statement about a consumer's budget, not permission to melt the host.

**Every window is counted even after one refuses.** Counting only until the first refusal would let a
caller stopped by the burst limit spend the rest of the day invisible to the sustained one.

**When several windows refuse, the reported reset is the latest.** Retrying when the burst window
reopens would still fail the daily budget; a `Retry-After` that is too short is worse than none,
because it invites a client to hammer a door that is still shut. When nothing refuses, the reported
window is the one with the least left — the limit the caller is about to meet.

**Two defects found by running it, not by reading it:**

- **The same length twice was counted twice.** A counter identifies a bucket by key *and duration*, so
  `--rate-burst 600/3600` beside an hourly quota put both windows on one bucket and charged every
  request to it twice — enforcing half of what the operator wrote. Windows of equal length now
  collapse to the tighter limit. Found by a wire test whose burst happened to be an hour, which is an
  entirely ordinary thing to configure.
- **A key issued on one replica was invalid on the others.** Keys were loaded at startup and never
  again, so behind a load balancer a partner saw intermittent 401s until every pod had restarted —
  and, worse, a *revoked* key kept working on every replica that had not. API keys are now the fourth
  kind of state the change feed reconciles (#279 established the mechanism for stubs, environments and
  sandbox documents). The prune is the half that matters: issuing late costs a retry, revoking late
  means a withdrawn credential still serves traffic. A host with `--sandbox-auth` and `--redis` but no
  `--change-feed` now says so at startup rather than leaving it to be discovered.

**A visible change to what `usedThisHour` means.** The old limiter stopped counting at the limit; the
shared counter counts attempts, so a key that made 62 requests against a quota of 40 now reports 62.
The number that stops at the limit cannot tell a partner who fitted inside their quota from one
hammering a closed door, and the second is the case an operator is looking for. Rate headers are
unchanged — `X-RateLimit-Remaining` still floors at zero.

**Not done, deliberately.** No sliding window and no token bucket: a fixed window is what the rate
headers describe, and a shared sliding window costs a sorted set per key per request to buy precision
nobody is measuring here. No cross-key or per-tenant aggregate ceiling — that is a different question
(what a tenant costs) and belongs with usage reporting.

**Validation.** `RateLimitTests` (29 unit cases: bucket alignment, multi-window composition and its
tie-breaks, stale buckets, concurrency), `RateLimitWireTests`/`RateLimitWithoutBurstTests` (4 wire
cases including an unconfigured host and a key with no quota), and `SharedQuotaTests` — four cases
against a **real Redis container with two hosts**, proving the sum is enforced rather than doubled,
that a restart does not refund, that reported usage spans replicas, and that a revocation lands on the
other host. Two hosts against real Redis is the only test that can fail here: an in-process counter
passes every single-host test perfectly and is wrong in exactly the deployment the feature exists for.
**Stryker: 98.04 %** on `RateLimits.cs`. The one survivor is equivalent — `<=` versus `<` when
collapsing two windows of equal length whose limits are also equal, where both branches produce a
`RateWindow` record with the same duration and limit, so no observation can tell them apart.
