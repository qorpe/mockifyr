# Parity notes — G7 Admin API + stub metadata

WireMock admin behaviors probed against the oracle (`wiremock/wiremock:3.10.0`) plus notes on how the
management path is built. See [README](README.md) for the format.

## Admin CRUD shapes (probed from the oracle)

- **`POST /__admin/mappings`** creates a stub and returns it with an **`id`** and a duplicate
  **`uuid`** (the same GUID), the request/response, and any **`metadata`** — status **201**. The stub
  serves immediately.
- **`GET /__admin/mappings`** → `{"mappings":[ … ]}`, each entry carrying `id`/`uuid`/`metadata`.
- **`GET /__admin/mappings/{id}`** → 200 with the stub.
- **`DELETE /__admin/mappings/{id}`** → **200**; the stub stops serving afterwards. Idempotent.
- **`POST /__admin/mappings/reset`** clears every mapping.
- **Invalid stub JSON** → **422**.

## G7a — management path (CQRS via Mediant), validated in-process

- **Group / item:** G7a. The admin JSON is volatile-field-heavy (`id`/`uuid` differ per engine), so
  the HTTP surface is validated **semantically** in G7b; G7a validates the underlying **CQRS handlers
  in-process** (`Mockifyr.Application.Tests`).
- **CQRS.** `Mockifyr.Application` (Mediant 1.0.0, `ISender`, `Result<T>`): `CreateStubCommand`,
  `DeleteStubCommand`, `ImportMappingsCommand`, `ResetMappingsCommand`, and the queries
  `GetStubsQuery`/`GetStubQuery`/`CountRequestsQuery`/`FindUnmatchedRequestsQuery`. Handlers depend
  only on Core contracts (`IStubStore`) and the engine's read-only verify methods. Mediant lives
  **only** here (decision 0005).
- **Shared state.** `AddMockifyr` (composition root, `Mockifyr.Server`) registers the in-memory
  stores + engine + Mediant handlers as singletons, so a stub created through the **management path**
  is immediately served by the **hot path** — verified (`CreatedStub_IsServedByTheEngine_AndCounted`).
- **Stub id / metadata.** The adapter now honours an explicit `id`/`uuid` (else mints one) and parses
  the arbitrary `metadata` object onto `StubMapping.Metadata` — verified round-tripping
  `metadata.team`.
- **Regression cases:** `Mockifyr.Application.Tests.AdminCqrsTests` (5 cases).

## G7b — admin HTTP facade, validated semantically against the oracle

- **Group / item:** G7b. `Mockifyr.Facade.Admin` maps the WireMock-compatible `/__admin/*` routes to
  Mediant commands/queries (`AdminEndpoints.MapAdminEndpoints`); `Mockifyr.Server`'s host wires
  `AddMockifyr` + the endpoints. Thin: HTTP → `ISender.Send` → Application → Core.
- **How it's validated.** The **same** admin scenario is driven over HTTP against both the oracle and
  Mockifyr's in-memory admin host (`WebApplicationFactory<Program>`), and the **observation sequence**
  — status codes + mapping counts — must match. Per-engine stub ids differ, so the comparison is
  semantic (effects), not byte-for-byte. Verified identical on both:
  `reset→200`, `count 0`, `create→201`, `count 1`, `get→200`, `getMissing→404`, `delete→200`,
  `count 0`, `import(bundle of 2)→200`, `count 2`, `malformed create→422`, `reset→200`, `count 0`.
- **Status codes matched to the oracle:** create **201**, get/delete/import/reset/list **200**, a
  missing id **404**, malformed stub JSON **422** (the handler catches the parse error and returns a
  validation `Result`, which the endpoint maps to 422 — no exceptions for control flow).
- **Deferred:** mock-serving over HTTP (a catch-all → engine → wire response) belongs to the
  transport facade (**G12**); `/__admin/scenarios*` listing and the rich admin response JSON export
  shape (only ids/counts are surfaced now); tenant resolution (default tenant until G12).
- **Regression case:** `G7bAdminHttpTests.Admin_Crud_MatchesTheOracle`.

> **G7 is complete** with G7b: the management path is a full Mediant CQRS layer behind a
> WireMock-compatible admin HTTP surface, validated in-process (handlers) and over HTTP (semantic
> differential).

## Backfill — `PUT /__admin/mappings/{id}` (stub update)

WireMock supports **replacing** a mapping in place via `PUT /__admin/mappings/{id}`; the URL id is
authoritative. Mockifyr originally shipped only create (`POST`), read, delete, import and reset, so an
edit from the dashboard hit a non-existent route (`404`) and the change was silently dropped. Added
`UpdateStubCommand` + `UpdateStubHandler` (forces the parsed stub's id to the route id so the store
upserts in place rather than appending a duplicate) behind `admin.MapPut("/mappings/{id:guid}")`,
returning `200 { id, uuid }` on success and `422` for malformed/empty JSON — the same shape as create.

- **Non-obvious:** the WireMock JSON reader throws `InvalidOperationException` (not `JsonException`)
  when a field is well-formed JSON but the wrong type — e.g. a string-encoded `"status"`. The create
  and update handlers now treat that as a client input error (`422`) rather than letting it surface as
  a `500`. (The dashboard was the trigger: number-input form fields serialize as strings; the editor's
  `toWireMock` now coerces `status`/`priority` to JSON numbers.)
- **Regression case:** `AdminCqrsTests.Update_ReplacesInPlace_AndIsServed` (the update reaches the
  serving path — a follow-up request returns the new status) and `Update_MalformedJson_ReturnsValidationError`.


## Bounded journal (#220, post-G hardening)

The journal is bounded per tenant (default 1000; `--journal-limit`, reference alias
`--max-request-journal-entries`; `<=0` = unbounded; `--journal-disabled` / `--no-request-journal`
records nothing). **Eviction semantics proven differentially**: both engines started with
`--max-request-journal-entries 3`, five requests driven through each — both retain exactly the
NEWEST three, oldest evicted first (`JournalLimitTests`). Consequences carried on purpose:
`/__admin/requests/count` and verify only see retained events — identical to the reference engine.
The detail route (`/__admin/requests/{id}`) now resolves through an id index instead of
materializing the whole journal (O(1) instead of O(n)); the index is tenant-gated, covered by a
unit test the oracle cannot express. Stryker on the journal store: **100 %** (9/9 killed + 1 timeout).
Default change note: before #220 the journal was unbounded — a long-running host accumulated every
request (and its Authorization headers) forever; 1000 mirrors `--message-limit`.


## Journal masking (#227, post-G hardening)

`--mask-headers` / `--mask-body-fields` replace named values with `***` **before the serve event is
stored** — the choke point is `IRequestJournal.Record`, reached through a `MaskingRequestJournal`
decorator, so the value never exists in memory and cannot be read back through
`/__admin/requests/{id}` or the dashboard. Header names match case-insensitively and multi-valued
headers keep their arity; body fields are masked structurally (JSON walked at any depth, arrays
included), and a body that is not JSON is returned byte-for-byte — masking must never corrupt a
recorded payload.

**Opt-in on purpose — the design decision worth remembering.** Masking is off by default because the
journal is also the data source for `verify` and near-miss diagnostics: a masked `Authorization`
header is invisible to a verification that asserts on it. Making it default-on would have silently
broken a legitimate test pattern ("assert the client sent the right token"). Documented here and in
the CLI reference; the trade is the operator's to make.

Engine untouched: `StubEngine` knows nothing about masking (the decorator sits at the store seam),
Core keeps zero external dependencies (System.Text.Json is BCL), and the whole differential suite
passes unchanged — the parity surface did not move. **Stryker: 93.3 %** (28/30). The two survivors
are analyzed equivalents, both fast-path guards: removing the `IsEmpty` early return in `Mask`, and
flipping `fields.Count == 0 || body.Length == 0` to `&&`, both fall through to code that computes
the same result with empty inputs — a performance difference with no observable behavior change.


## Unauthenticated admin surface (#225, post-G hardening)

The admin API is open by default (the documented quick start depends on it), but it is no longer
silent about it: an unauthenticated host now prints a startup line naming what is reachable —
journal, captured messages, and the routes that act on the network — mirroring how the
outbound-trust flags already announce themselves.

`--block-outbound-routes` refuses those acting routes (`POST/PUT/DELETE` under
`/__admin/recordings`, `/__admin/outbound-trust`, `/__admin/git`) with a typed
**403 `Admin.OutboundRoutesBlocked`** while the admin surface is unauthenticated, so an open host
on a cluster cannot be turned into a forward proxy toward internal addresses. Deliberately narrow:
GET on the same prefixes still answers (the block is about acting, not looking), serving and every
other admin route are untouched, and the flag goes **inert once `--admin-user`/`--admin-pass` are
set** — the auth middleware already gates the same routes then, and the ordinary 401 answers.

Default behavior is unchanged: without the flag the routes respond exactly as before, which the
wire test asserts explicitly (a refusal must come from the flag, never from the upgrade).


## Per-tenant admin credentials (#224, post-G hardening)

Tenant scoping was already structural — there is no tenant-less store overload — but the `TenantId`
itself arrived in a client header, so any admin caller could address any tenant by renaming it.
`--tenant-credential <tenant>:<user>:<pass>` (repeatable) turns that header from a claim into an
**authorization** decision: a principal authenticated for `acme` gets a typed
**403 `Admin.TenantForbidden`** on `X-Mockifyr-Tenant: globex`, on reads and writes alike —
including the sharpest routes, `/__admin/messages/otp` (one-time codes) and `/__admin/mappings/reset`
(destructive). Omitting the header is not an escape hatch: it addresses the default tenant, which a
tenant principal does not own either.

Design notes worth remembering:

- **The global `--admin-user` stays the privileged system scope** ARCHITECTURE §6 anticipates — it
  still reaches every tenant, so existing operator tooling and the dashboard are unaffected.
- **A wrong password is 401, not 403.** Authentication failure must not reveal that a tenant exists.
- **Credentials are read from argv, not configuration**, because .NET configuration keeps only the
  last value of a repeated key — reading it the usual way would silently drop every tenant but one.
  Comparison is constant-time, matching the global credential.
- **`/__admin/health` stays exempt** (#218), so probes keep working on a tenant-scoped host.
- **No flag, no change:** with no `--tenant-credential` the middleware never engages, which the unit
  tests assert directly.

**Stryker: 100 %** (23/23). One survivor was worth the test it produced: dropping the `continue`
that skips non-flag arguments let an unrelated option's value (a Redis URL, a connection string
containing colons) parse into a bogus admin principal — now pinned by
`Only_the_flags_own_values_are_read`.

## Admin audit trail (#247, enterprise readiness)

The request journal answers "what did this host serve"; nothing answered "who changed it". `--audit`
adds that record: every mutating call under `/__admin` is appended to a tenant-scoped, append-only
trail — principal, tenant, action (`METHOD /path`), the addressed target if the route carried one,
and the HTTP outcome — readable at `GET /__admin/audit` and on the dashboard's **Audit** screen.

Decisions worth remembering, because each one was a fork in the road:

- **One middleware, not 33 instrumented routes.** There are 33 mutating admin routes today. Recording
  at each of them would mean 33 places to forget, and 33 definitions of "a change" free to drift.
  Auditing at the pipeline means a route added tomorrow is covered by construction. The cost is that
  an entry describes the *operation* rather than a domain event — which is what a reviewer asks for
  anyway, and it never claims success for a change the handler refused.
- **Reads are not audited.** `GET` traffic is already in the journal, and mixing it in would evict the
  changes an operator came looking for. `/__admin/audit` itself is excluded for the same reason:
  reading history is not making it.
- **Unauthenticated attempts (401) are not audited.** They are not administrative changes, they have
  no principal to name, and recording them would hand any anonymous caller a lever to evict the whole
  bounded trail by repetition. They surface as metrics (#246) and access logs instead. A **403**
  cross-tenant refusal (#224) *is* recorded — there the principal is known, and the attempt is exactly
  what a reviewer wants to see.
- **The principal is a label, never a credential.** `system`, `tenant:acme`, `anonymous`. A near-miss
  password resolves to `anonymous`, not `system`: the label is an attribution claim someone will rely
  on, so it must not be able to name the wrong actor. A wire test serializes the whole trail and
  asserts no part of any configured secret appears in it.
- **The trail is read-only through the API.** Entries are written by the host as a side effect of the
  change they describe; there is no route that appends, edits or clears one, so the surface being
  audited cannot rewrite its own history. It is deliberately **not** persisted through the G16 seam
  either — that would make the audit log a tenant-writable store.
- **Bounded like everything else** (`--audit-limit`, default 1000, oldest first — #220's model, so an
  operator learns one retention rule). The in-memory trail dies with the pod on purpose: each entry is
  also emitted as a structured `admin.audit` log line, so with `--log-json` (#246) the durable copy
  lives wherever the SIEM's retention policy says, not in a mock host's heap.
- **`/__admin/health` reports `audit: true|false`.** An empty trail is ambiguous on its own — "nothing
  changed" and "nobody is recording" look identical — so the dashboard is told which it is instead of
  leaving an operator to guess.

Validation: no oracle exists for this (it is ours, not a dialect behavior), so it is pinned by 7 wire
tests on a real Kestrel host — one entry per change and none for reads, the refused-change outcome,
principal labelling with a whole-trail secret sweep, 403-recorded vs 401-skipped, cross-tenant read
isolation, nothing recorded without the flag, and the bound evicting oldest-first — plus 21 unit tests
on the pure logic. **Stryker: 100 %** on both new pure-logic files (`InMemoryAuditLog` 11/11 killed +
1 timeout; `AuditPrincipal` 24/24). The one survivor worth recording: dropping the `?? []` fallback on
a null `PathString.Value` survived until a test covered the empty-path case — no real request produces
one, but auditing must never be able to throw inside the operation it is describing.

Deferred edges, stated rather than hidden:

- The trail records the operation, not a before/after diff of the changed stub. A diff would mean
  materializing every prior state — the export bundle already covers "what does it look like now".
- Entries are not signed or chained, so an operator with process access could in principle drop the
  in-memory copy. The SIEM line is the tamper-evident copy; hash-chaining the entries is only worth
  doing if the trail itself ever becomes the system of record.

## Backup and restore (#252, enterprise readiness)

Stubs could be exported and imported; nothing captured a whole tenant. `GET /__admin/backup` produces
one archive — stubs (as their authored source), environment keys, sandbox documents, API keys and
scenario states — and `POST /__admin/restore` puts it back. The dashboard's Settings screen wraps
both.

The decisions, and what each one rules out:

- **Replace, not merge.** Each section the archive carries is cleared before it is written. Merging
  would leave stubs the backup knows nothing about still serving, which is the opposite of what a
  restore is for. A section the archive omits is left alone, so a partial archive is still usable.
- **Everything is parsed before anything is written.** A restore that fails halfway would leave a
  tenant in a state neither the archive nor the operator can describe.
- **The caller's tenant header decides the destination, not the tenant name inside the file.**
  Restoring production's archive into a staging tenant is a normal drill; an archive that could
  re-target itself would be a cross-tenant write driven by a file's contents.
- **API keys travel with their salted verifier.** Otherwise every consumer's key stops working the
  moment you restore — the one thing a restore exists to prevent. The token itself was never stored
  and cannot appear. This is what makes the archive a secret, and it is stated in the README, the
  dashboard card and the website.
- **Journal, message inbox and quota counters are excluded.** They are observations of what happened,
  not configuration; restoring them would fabricate a history the target host never served. They are
  bounded and disposable by design (#220, ADR 0009).
- **Host configuration is excluded** — outbound trust, TLS, CLI flags. That belongs with the Helm
  values, and a tenant-scoped archive that carried host trust would be a hole in the tenant boundary.
- **A non-archive is refused outright.** A mapping bundle is the file an operator is most likely to
  reach for by mistake; treating it as an archive with every section missing would silently wipe the
  tenant. The reader also refuses a `mockifyrBackup` version it does not know rather than dropping the
  sections it could not parse.

Validation: 5 wire tests (fresh-host restore reproducing all five sections including a consumer key
that still authenticates, replace-not-merge, cross-tenant restore, refusal leaving state intact,
downloadable archive carrying no observations) plus 13 unit tests on the format. **Stryker: 100 %**
(34/34). Two survivors were worth the tests they produced: dropping the `"O"` timestamp format
survived until the fixtures carried sub-second precision (a rounded `createdAt` makes a backup's age
untrustworthy), and dropping the API key's `prefix` survived until it was asserted (every restored key
would show up anonymous in the Access screen).

Deferred edges: no host-wide "every tenant" archive (each tenant is backed up on its own, which keeps
the tenant boundary intact); no incremental or scheduled backups; the archive is not encrypted at rest
— it is a file the operator stores wherever their secrets already live.


## Import warnings for deferred fields (1.0)

Mockifyr implements a validated subset of the mapping dialect, and the gaps were documented. Two of
them were also **silent**, which is the part that matters: a `bodyFileName` stub matched and returned
an empty body — reading exactly like a matching bug, and not being one — and a non-`uniform`
`delayDistribution` produced no delay at all. Both had to be discovered from behaviour.

`UnsupportedFieldWarnings` inspects an imported mapping for fields this engine accepts but does not act
on, and the result is reported: as a `warnings` array on `POST /__admin/mappings` and
`/__admin/mappings/import`, and as a console line for mappings loaded from disk at startup — the only
place an operator would otherwise hear nothing at all.

- **Warn, do not refuse.** The stub is still created. Refusing it would break importing a mapping set
  written for the reference engine, which is the whole point of accepting the dialect. The goal is to
  be loud, not strict.
- **One line per kind of gap, with a count.** A 200-stub bundle whose responses each name a different
  file would otherwise produce 200 near-identical lines — a wall nobody reads, which buries anything
  else in the list. The fix is the same for every one of them, so the file name is not what makes the
  message useful. The count is appended only when it says something: `(1 stubs)` is noise.
- **The field is absent when there is nothing to say.** Existing clients parse the create response; a
  `warnings` key on every successful create is a field they would learn to ignore.
- **It can never fail an import.** Malformed input returns no warnings rather than throwing — the
  importer reports malformed JSON itself, with a better message.

Validation: 18 unit tests (each gap, each ordinary shape producing nothing, bundles, bare arrays,
grouping with a count, and every malformed shape) plus 3 wire tests — including one that asserts the
warned-about behaviour really is what happens, so the warning cannot drift away from the truth.
**Stryker: 100 %** (13/13). One survivor earned its test: a single-stub gap rendering as "(1 stubs)".

## OIDC on the admin surface (#251, post-1.0)

Deferred past 1.0 with written reasoning, then asked for. What made it a bounded change rather than
the large one the deferral assumed: authentication has always lived in one middleware chain at the
host edge, so OIDC is a **third principal source** beside the system credential and per-tenant
credentials — not a replacement for either.

```
--oidc-authority https://login.example.com --oidc-audience mockifyr \
--oidc-client-id mockifyr-dashboard --oidc-tenant-claim mockifyr_tenant --oidc-required-role mockifyr-admin
```

Decisions worth remembering:

- **Core is untouched.** No auth type crosses into the engine; it never knew who a caller was and
  still does not.
- **Basic keeps working alongside it.** A host can run OIDC for people and `--admin-user` for
  machines, which is what makes adoption incremental instead of a flag day — asserted by a test, since
  it is exactly the property a refactor would break.
- **A tenant claim scopes an identity the way `--tenant-credential` does** (#224): a principal scoped
  to `acme` gets **403** on `globex`, and omitting the header does not help because that addresses the
  default tenant it also does not own. An identity with no claim keeps system scope — the OIDC
  equivalent of `--admin-user`, so an operator's own account still reaches every tenant.
- **Keys come from discovery**, so a provider rotating its signing key needs nothing here — the same
  reasoning as the key ring in #250, applied to somebody else's keys.
- **An unreachable provider is a 401, not a 500.** Every failure — malformed, expired, wrong audience,
  unverifiable, provider down — resolves to "this caller is not authenticated", because the header is
  attacker-controlled input on the request path.
- **The audit trail records the person**: `oidc:jane@example.com`, preferring a human-recognisable
  claim over an opaque subject id, since an entry naming `a3f9…` tells a reviewer nothing they can act
  on. The token never appears, for the same reason a password never did.
- **`/__admin/health` reports the auth mode** (`none` / `basic` / `oidc`) plus the authority and public
  client id. Necessarily unauthenticated: a login screen cannot authenticate before it knows where to
  send the user. Only public parameters of a public client are exposed — never a secret.
- **The open-admin warning (#225) now accounts for OIDC.** It used to fire regardless, which would
  have told operators of a properly authenticated host that it was wide open — and a warning that
  cries wolf is worse than none.
- **Probes stay open** (#218, #242): a kubelet cannot carry a token.

The dashboard signs in with **authorization code + PKCE and no client secret**, because anything
shipped to a browser is readable; the flow has to stay safe when its parameters are. The access token
lives in `sessionStorage`, so closing the tab ends the session — a shared machine should not keep
someone signed in to a mock platform indefinitely. The authorization code is stripped from the URL
after exchange, since a code left in the address bar ends up in history and in pasted links.

Validation: 10 wire tests against a **real in-process issuer** — the test publishes a discovery
document and a JWKS and signs RS256 tokens with the matching key, so the discovery fetch, key lookup
and signature check are all the production path. There is no test-only shortcut that could hide a hole
in it. The forged-token case uses the same issuer, audience and `kid` and differs only in the key: it
passes only if the signature is genuinely checked against the published JWKS.

Deferred: token refresh (an expired session returns the user to sign-in), back-channel logout, and
mapping claims to anything finer than a tenant.

## Journal reset — `DELETE /__admin/requests` (post-1.5.0)

- **Group / item:** post-roadmap admin parity, oracle-verified.
- **The gap.** The journal could not be cleared at all. `IRequestJournal` had `Record` and `Query` and
  nothing else, so a suite sharing one host either restarted it between tests or accepted that every
  count answered about the whole run. Found while writing the site's verification guide: the endpoint
  the guide needed to document did not exist.
- **The oracle chose the spelling, and corrected the guess.** `POST /__admin/requests/reset` — the
  intuitive form, and the one this repo was about to document from memory — answers **404** on the
  reference engine. `DELETE /__admin/requests` answers **200** and empties the journal. Mockifyr
  implements the second and deliberately keeps answering 404 for the first: adding the friendlier alias
  would be a divergence, and a script that works here has to work there.
- **Clearing prunes the id index, not just the tenant's list.** The journal keeps a cross-tenant index
  for O(1) detail lookups (#220). A reset that dropped only the list would leave `GET
  /__admin/requests/{id}` still answering — a "cleared" journal handing back the very request body
  somebody asked to be rid of. The index is pruned entry by entry so other tenants' lookups survive.
- **Reset is per tenant**, which the oracle cannot judge: parallel suites share a host by taking a
  tenant each, so a reset that reached across them would silently corrupt a neighbour's counts.
- **A disabled journal accepts a reset** (`--journal-disabled`) and a masking-decorated journal passes
  it through. Teardown runs whatever the host was started with; neither configuration may turn cleanup
  into a failure.
- **Validation.** `JournalResetTests` — four differential cases against the oracle (count before/after,
  the listing agreeing with the count, an empty reset accepted, and the rejected spelling rejected on
  both) plus one self-test for tenant scoping. `JournalResetUnitTests` — seven cases for the store
  logic, including the id index, the neighbour tenant, and the bound applying fresh after a reset.
  **Stryker: 100 %** on `InMemoryRequestJournal`.
- **In the dashboard.** The Journal screen has a *Clear journal* action behind a confirmation that says
  what it does — every recorded request, counts included, this tenant only. Verified in-browser in
  English and Turkish. Doing so surfaced a pre-existing defect on the Messages screen: `ConfirmDialog`
  leaves closing to its caller (deliberately — the resource editor keeps it open when the edit is
  invalid) and *Clear all* never closed it, so the dialog sat over a table that had already emptied.
  Both callers now close on confirm.


## The partner principal (#346)

`--tenant-credential` scopes a principal to one tenant's **data** correctly: renaming `X-Mockifyr-Tenant`
does not let it address another tenant, and the refusal is audited. What it never scoped is **host-level
outbound capability**, and `--block-outbound-routes` does not cover the gap — it is gated on the admin
surface being *unauthenticated*, on the reasoning that credentials already gate the same routes. That
reasoning holds for a single trusted operator and collapses for a credential handed to a partner, who is
authenticated by definition.

`--partner-credential <tenant>:<user>:<pass>` is the same tenant scoping plus a refusal on the ways this
host acts on the network.

**The part the original analysis missed.** The three outbound admin routes (`/__admin/recordings`,
`/__admin/outbound-trust`, `/__admin/git`) are only *one* way to reach outward. `POST /__admin/mappings`
goes through the same `MappingJsonReader` that accepts `proxyBaseUrl` (G8) and `postServeActions` /
`serveEventListeners` (G3) — so a principal with stub-write access on its own tenant reaches the network
through the **data plane**. Blocking the routes alone would have produced a control that looks like it
holds and does not, which is worse than no control because an operator stops looking. The refusal
therefore covers both, and names the field so a partner who legitimately needs a proxy stub gets
something they can act on.

**Decisions worth keeping:**

- **A separate flag, not a fourth field.** The existing value is split exactly twice because a password
  may contain `:`; adding a field would make some passwords unspellable. A second flag also makes
  "today's `--tenant-credential` is unchanged" true by construction rather than by careful reading.
- **Every method is refused on an outward route,** not only the mutating ones. Reading which upstream a
  recording points at is not a partner's business either, and "these routes are not yours" is a rule an
  operator can hold in their head.
- **The check sits where mappings are admitted,** so `POST /mappings`, bundle import and the edit path
  are covered by one rule rather than three that can drift apart. The body is buffered and rewound —
  a check that consumed it would turn every allowed request into an empty one.
- **OpenAPI import is unaffected:** the generator emits no proxy, webhook or publish directive, so a
  partner can still turn a specification into a working sandbox.
- **A payload that does not parse declares nothing.** It is refused moments later by the reader that owns
  the dialect, with a better message; answering "denied" to malformed JSON would tell a caller their
  permissions are wrong when their syntax is.

**A silence removed on the way.** `--block-outbound-routes` on an authenticated host previously did
nothing and *said* nothing. It now says so at startup and names what actually scopes a credential. The
behaviour is unchanged — the flag is still scoped to the unauthenticated case by design — but a flag that
silently no-ops is how an operator comes to believe in a control they do not have.

**An attribution weakness found while proving the audit criterion.** The issue asked for refusals to be
audited "with the principal and the route". They are — `IsAuditable` excludes reads, so a refused *change*
is recorded and a refused *read* is not, which is exactly the existing behaviour for a cross-tenant attempt
and therefore what "same as" meant. But the label was wrong: `AuditPrincipalResolver` mapped every
per-tenant credential to `tenant:<name>`, so an operator and a partner scoped to the same tenant read as
the same actor — in the very entries this class exists to produce. A partner is now `partner:<name>`. The
weakness was invisible until the criterion was tested rather than asserted, which is the argument for
testing it.

**Validation.** `PartnerPrincipalTests` (14 wire cases) and `OutboundReachTests` (16 unit cases). The
operator principal is asserted beside the partner in nearly every case on purpose: a test that only showed
the partner refused would pass just as well if the route were broken for everybody, which is a different
bug wearing the same 403. **Stryker 96.55 %** on `OutboundReach.cs`, one survivor, analysed and confirmed
equivalent:

- `mapping.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object`
  with the `&&` mutated to `||`. Stryker mutates the syntax tree, so the mutant is
  `(A || B) && C && …` rather than the textually-different `A || (B && C && …)`. When `TryGetProperty`
  returns false, `out var response` is `default`, and `default(JsonElement).ValueKind` is `Undefined` —
  never `Object`. So `A` false implies `B` false, and at that node `||` and `&&` cannot be told apart.
  Confirmed by applying it, which is also how the textual-versus-tree distinction surfaced: editing the
  source by hand produces a genuinely different expression that six tests kill.
