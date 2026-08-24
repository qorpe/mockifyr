# Mockifyr — Architecture Design Document

> Status: **draft / awaiting approval.** This is a pre-code alignment document. The decisions
> below were discussed and settled based on the Mock Engine Kickoff Brief. After approval we
> scaffold the solution skeleton + differential harness skeleton; implementation begins only
> after that, via TDD.

---

## 1. Vision and scope discipline

- A **.NET-based, independent-codebase** API mock **engine + platform** with an integration
  sandbox. No third-party mock engine is taken as a dependency. Own IP.
- **The goal is not "all of WireMock at once."** First a narrow vertical validated against a
  reference; feature parity comes later, controlled, every step validated.
- **Finish line:** full WireMock feature parity — but in ~40 validated steps, each diffed
  against real running WireMock (the oracle).
- Target environment: on-prem **OpenShift**, container-first. Uses: in-process library
  (tests), standalone container, runtime management via admin API.
- Stack: **.NET 10** (latest LTS), C#.
- Product name / root namespace: **`Mockifyr`**.

---

## 2. Architectural principles

1. **Transport-agnostic core engine.** The core knows nothing about HTTP/port/transport. It
   answers one question: "Given the loaded stubs, which matches this request and what should
   it return?"
2. **Facades are thin adapters.** One engine; Library / HTTP server / Admin REST on top.
   Matching/templating logic **never** leaks into a transport handler.
3. **Multi-tenancy is first-class.** Not by separate ports but by logical isolation inside the
   engine. One process/port, N tenants. In the model from the start.
4. **The engine is pure and deterministic.** No I/O; delay/fault/proxy are directives,
   outbound is a listener. This is the precondition for differential testability.
5. **Extension points are the same seams the built-in features use** (dogfooding).
6. **Only the abstraction this vertical needs.** No over-engineering; but multi-tenancy, the
   persistence seam, and the extension seams — the "expensive to retrofit" dimensions — are in
   the model from day one.
7. **Proof = green differential diff.** Happy-path code that "looks like it works" is not
   enough. The oracle is always running WireMock; AI self-validation is forbidden.

---

## 3. Solution topology

Clean / onion architecture. **All dependency arrows point inward to Core.** No facade depends
on another facade. All external libraries (Handlebars.Net, JSONPath, XML, Kestrel, Mediant,
Testcontainers) are collected at the edges.

```
                     ┌──────────────────────────────┐
   Composition →     │   Mockifyr.Server (host)      │  standalone/container entrypoint
                     └───────────────┬──────────────┘
                      ┌──────────────┼───────────────┐
              ┌───────▼──────┐ ┌─────▼──────┐ ┌──────▼────────┐
   Facades →  │ Facade.Http  │ │Facade.Admin│ │Facade.Library │
              │ (HOT PATH)   │ └─────┬──────┘ └──────┬────────┘
              └───────┬──────┘ ┌─────▼───────────┐   │
   Application →      │        │ Mockifyr.       │   │   CQRS / Mediant:
                      │        │ Application     │   │   management path only
                      │        │ (Mediant, CQRS) │   │   (hot path bypasses it)
                      │        └─────┬───────────┘   │
                      └──────────────┼───────────────┘
     ┌───────────┬───────────┬───────┼────────┬──────────────┬───────────┐
 ┌───▼────┐ ┌────▼─────┐ ┌───▼────┐ ┌▼─────────┐ ┌──────────▼─┐ ┌────────▼──────┐
 │Matching│ │Templating│ │ Stores │ │ Adapters │ │ ServeEvents│ │ (Scenario/    │
 │        │ │(Hbars.Net│ │.InMemory│ │.MappingJson││ .Webhook   │ │  Journal…)    │
 └───┬────┘ └────┬─────┘ └───┬────┘ └────┬─────┘ └──────┬─────┘ └────┬──────────┘
     └───────────┴───────────┼───────────┴──────────────┴────────────┘
                    ┌─────────▼─────────┐
   Core →           │   Mockifyr.Core   │  domain model + contracts + pure StubEngine
                    └───────────────────┘  (zero external deps, transport-agnostic)

   Harness (separate, not shipped):
     Differential.Harness  ─drives→  Facade.Http  +  Java WireMock (Testcontainers)
     Differential.Generator ─feeds→  Harness
```

**The one rule:** arrows only point inward. We can later enforce it with an architecture test
(NetArchTest): `Core` may not depend on any Mockifyr project nor any I/O/transport library.

### 3.1 Project responsibilities

**Core**
- **`Mockifyr.Core`** — Domain model, all engine contracts, and the pure orchestration
  (`StubEngine`). Zero external dependencies. Extension authors compile against this.

**Capability implementations** (depend on Core; encapsulate external libraries)
- **`Mockifyr.Matching`** — `IMatcher` impls (equalTo, equalToJson, matchesJsonPath,
  equalToXml, matchesXPath, date/number/logic…). JSON/XML/JSONPath dependencies live here.
- **`Mockifyr.Templating`** — Handlebars.Net renderer + the `ITemplateHelper` set.
- **`Mockifyr.Stores.InMemory`** — tenant-scoped `IStubStore`/`IScenarioStateStore`/
  `IRequestJournal` in-memory impl (default, narrow vertical).
- **`Mockifyr.Adapters.MappingJson`** — mapping JSON ↔ domain model import adapter. Used by
  the harness and the admin API; itself under differential test.
- **`Mockifyr.ServeEvents.Webhook`** — `IServeEventListener` impl (outbound I/O + templating).
- **`Mockifyr.Adapters.OpenApi`** — OpenAPI 3.x document → stub set import (spec in, working
  sandbox out; `?stateful=true` wires resource-shaped path pairs to the state directive).
- **`Mockifyr.Outbound`** — the outbound HTTP seam every callback/proxy/webhook shares:
  trust policy (allow-listed hosts), TLS options, redaction. Facades call out ONLY through it.
- **`Mockifyr.Crypto`** — payload decryption, response signing/protection, signature
  verification (JWS/JWE key material handling); off unless keys are configured.
- **`Mockifyr.Providers.Sms`** — provider-shaped HTTP profiles (Twilio first): parses the
  provider's wire format into message envelopes and answers what the official SDK accepts.

**Facades** (depend on Core, not on each other)
- **`Mockifyr.Facade.Library`** — in-process public API (use in tests = WireMock's
  "no HTTP server / direct-call" mode).
- **`Mockifyr.Facade.Http`** — Kestrel-based mock HTTP server. Tenant resolution,
  transport→`CanonicalRequest` mapping, and wire-affecting delivery behaviors (CORS, gzip,
  chunked encoding, host-header). Applies the delay/fault/proxy **directives**.
- **`Mockifyr.Facade.Admin`** — `/__admin/*` REST management. Runs in the same process/port as
  the HTTP host but is a separate facade project. **Thin**: HTTP → CQRS dispatch → result.
- **`Mockifyr.Facade.Grpc`** — gRPC serving over uploaded descriptors; inherits tenant
  resolution and the engine untouched (transport → canonical mapping only).
- **`Mockifyr.Facade.WebSocket`** — WebSocket scenarios (connect/message/push scripts) on the
  same host; the engine stays request/response-pure, the facade owns the socket lifetime.
- **`Mockifyr.Facade.Smtp`** — the SMTP capture listener (`--smtp-port`, ADR 0009): real
  ESMTP for real clients, AUTH username names the tenant, envelopes land in the message inbox.
- **`Mockifyr.Facade.Broker`** — broker-shaped capture/publish (queue mocking) behind the
  same canonical envelope; opt-in like every listener.
- **`Mockifyr.Facade.Sandbox`** — `/__sandbox/*`, the PARTNER-scoped self-service surface
  (#347): a sandbox key reads its OWN tenant's requests, messages, usage and resources —
  and never `/__admin` (a sandbox key is not an operator).

**Application layer (CQRS / Mediant — management path only, §10)**
- **`Mockifyr.Application`** — Admin/runtime management use cases as `ICommand<T>`/`IQuery<T>` +
  handlers (CreateStub, UpdateStub, DeleteStub, ImportMappings, ResetJournal/Scenarios,
  GetStub, ListStubs, FindRequests, GetNearMisses…). Depends on `Mockifyr.Core` + **Mediant**.
  Returns `Result<T>`. **The hot path (mock serving) does NOT go through here.**

**Composition & validation**
- **`Mockifyr.Server`** — standalone/container entrypoint (`Program.cs`), config/CLI flags,
  the single composition root that wires all facades.
- **`Mockifyr.Differential.Harness`** + **`.Generator`** — brings up Java WireMock in a
  container, loads WireMock JSON into both sides, produces a canonical diff + a deterministic
  fuzzing generator.

---

## 4. Domain model and engine contracts

```csharp
readonly record struct TenantId(string Value);

record CanonicalRequest(
    string Method, string Url, string Path,
    IReadOnlyList<string> PathSegments,
    IReadOnlyDictionary<string,string> PathVariables,   // urlPathTemplate → {contactId}
    ILookup<string,string> Query, ILookup<string,string> Headers,
    IReadOnlyDictionary<string,string> Cookies,
    byte[] Body, IReadOnlyList<MultipartPart> Parts,
    string? ClientIp);

record CanonicalResponse(
    int Status, string? StatusMessage,
    ILookup<string,string> Headers, byte[] Body,
    DelayDirective? Delay, FaultDirective? Fault, ProxyDirective? Proxy);

record MatchResult(bool IsExactMatch, double Distance);   // Distance carried from day one for near-miss
```

Five modules, six contracts (all in Core):

```csharp
interface IStubStore {                                    // tenant-scoped store (persistence seam)
    IReadOnlyList<StubMapping> GetStubs(TenantId t);      // no tenant-less overload
    void Put(StubMapping s); void Remove(TenantId t, Guid id);
}
interface IMatcher { MatchResult Match(MatchInput input); }        // And/Or/Not implement this too
interface IResponseRenderer { CanonicalResponse Render(ResponseDefinition d, RenderContext c); }
interface IScenarioStateStore { string GetState(TenantId t, string scenario); void SetState(TenantId t, string s, string state); }
interface IRequestJournal { void Record(ServeEvent e); IReadOnlyList<ServeEvent> Query(TenantId t, ServeEventQuery q); }
interface IServeEventListener { Task OnServeEvent(ServeEvent e, CancellationToken ct); }  // webhook here
```

---

## 5. Request lifecycle (data flow)

```
Facade → (TenantId, CanonicalRequest)
         ▼
┌─────────────────────────────────────────────────────────────┐
│  StubEngine.Handle(tenantId, request)                        │
│  1. stubs   = IStubStore.GetStubs(tenantId)   ← ISOLATION    │
│  2. eligible= those whose scenario state matches (Store R)   │
│  3. scored  = eligible.Select(Pattern.Evaluate) → MatchResult│
│  4. exact   = scored.Where(IsExactMatch)                     │
│     winner  = exact.OrderBy(Priority).ThenByDesc(Recency)    │
│  5. none    → NoMatch(nearMisses = scored.OrderBy(Distance)) │
│  6. transition → IScenarioStateStore.SetState (W)            │
│  7. response= IResponseRenderer.Render(def, ctx)             │
│  8. event   = ServeEvent(req, winner, response, timing)      │
│  9. IRequestJournal.Record(event) (W)                        │
│ 10. IServeEventListener[] fire (async) → webhook outbound    │
└─────────────────────────────────────────────────────────────┘
         ▼
Facade ← CanonicalResponse (+ Delay/Fault/Proxy directives)
         └─ apply delay · inject fault · proxy outbound · write wire with CORS/gzip/chunked
```

Decisions:
- **The engine does no I/O.** Delay/fault/proxy are directives (the facade applies them).
  Webhook is an `IServeEventListener` (impl outside Core, uses `HttpClient`).
- **Two scenario hooks:** eligibility before match, transition after match. May be passive in
  the first phase; the hooks are in the pipeline from the start.
- **Near-miss is free:** `Distance` is already computed in step 3; G6 just exposes it.
- **One renderer, two contexts:** `{request}` for responses, `{originalRequest}` for webhooks —
  correlation (transactionId/callback URL) works exactly here.
- **`StubEngine` is a pure coordinator:** no matching/templating logic inside it; it calls
  everything through contracts. New matcher/helper = StubEngine unchanged.

---

## 6. Tenant isolation

- **Single point of origin:** `ITenantResolver` in `Facade.Http` (strategy: subdomain /
  path-prefix / header) → `TenantId`. A raw transport concept becomes a `TenantId` only here.
  The engine does not know what a "Host header" is.
- **Multi-domain = a resolver strategy** (subdomain). Not a separate concept; it dissolves
  into the tenant model.
- **Enforced by the type system, not ambient:** `tenantId` is a required parameter at every
  store/engine entry point. Ambient AsyncLocal is **not** used (it leaks when forgotten). A
  tenant-less method like `GetAllStubs()` does not exist → forgetting the scope is a compile
  error.
- **Every carrier is tenant-scoped:** stub store, scenario state, journal, persistence
  partition, extension context. Cross-tenant visibility exists only in a separate, privileged
  **"system" scope** (the UI's tenant selector) — fully isolated from the normal data path.
- **Harness relationship:** WireMock has no tenant concept → we test a single tenant against
  the oracle; isolation is verified with separate invariant tests (not against the oracle).

---

## 7. Facade boundary

The engine returns a pure `CanonicalResponse` + directives; **materializing the wire is the
facade's job.** Wire-affecting delivery behaviors (they directly affect the differential diff,
which is why the harness catches them too) live **in `Facade.Http`, not the engine**:
- CORS (`stubCorsEnabled`), gzip (global + stub), chunked transfer encoding
  (`NEVER/BODY_FILE/ALWAYS`), preserve-host-header, strict-header behavior.

These are tested at the facade from G2a onward and tuned in G12.

---

## 8. Persistence strategy

**Mental model:** the in-memory **compiled index** is the single source of truth for the hot
path. A request never reads the DB (perf + matchers are compiled: regex/JSON/JSONPath). A
persistence provider = startup load + write-through + feeding external changes back via a
**reload** seam.

```
IStubStore (+ IScenarioStateStore, IRequestJournal) + IStubChangeSource   ← Core contracts
   ├─ InMemory      → default, ephemeral (narrow vertical)
   ├─ FileBased     → WireMock-style JSON mappings/ dir + file watcher  ← "edit and reflect"
   ├─ LiteDB        → embedded single file, single node, no external dep
   └─ Postgres/Redis→ shared, OpenShift multi-replica HA
```

**"Update directly from the DB and have the engine reflect it":** wiring the hot path to the
DB is the wrong solution. The right one is an explicit **`IStubChangeSource` reload contract** —
file-watch for FileBased, `LISTEN/NOTIFY` for Postgres, pub/sub for Redis, or version-polling;
plus a manual `POST /__admin/reload` escape hatch.

**LiteDB:** good for single node/dev (stubs are documents anyway). Under multi-replica it hits
file-lock issues → a shared provider is required; that is exactly why the provider model
exists.

**Priority:** FileBased (parity + human/git-friendly, the cleanest answer to "edit directly") →
LiteDB → Postgres/Redis. Phase A ships only InMemory; because the contracts are in Core from
the start, adding a provider is a new project, not a refactor.

---

## 9. Extension seams

Built-in features are themselves implementations of these seams (dogfooding) → the mechanism
is real from G1. Contracts live in Core; impls in capability projects.

| WireMock extension type | Internal contract | Built-in impl |
|---|---|---|
| RequestMatcherExtension | `IMatcher` (+ `IMatcherRegistry`) | all G1 matchers |
| ResponseDefinitionTransformerV2 | `IResponseDefinitionTransformer` | — |
| ResponseTransformerV2 | `IResponseTransformer` | `response-template` (G2) |
| ServeEventListener | `IServeEventListener` | webhook (G3) |
| Template helper | `ITemplateHelper` (+ `ITemplateHelperProvider`) | all G2 helpers |
| Template model provider | `ITemplateModelProvider` | request model |
| Admin API extension | `IAdminApiExtension` | gRPC reset (G13) |
| (+ mappings loader / request filter / lifecycle) | `IMappingsLoader`, `IRequestFilter`, `IExtension.Start/Stop` | file/dir loader |

Registration: a name-keyed registry (for JSON references) + DI. Discovery: assembly scanning
is **optional and off by default** (WireMock's `--disable-extensions-scanning` security
behavior). Extensions are registered globally but receive a tenant-scoped context — isolation
is preserved.

---

## 10. CQRS and Mediant (application / admin layer)

**Decision:** the management/runtime path is built with **CQRS** on top of **Mediant** (our
own MediatR alternative) — but **only in the application/admin layer**; it never enters the
engine hot path.

### Two paths, two architectures
```
  HOT PATH (mock serving, ~thousands req/s):
     Facade.Http → StubEngine (direct, pure, allocation-lean)      ← NO mediator
                    └─ IMatcher/IResponseRenderer/... contracts

  MANAGEMENT PATH (admin/runtime, low frequency):
     Facade.Admin → ISender.Send(command|query)
                    → Mockifyr.Application handler (Mediant)
                    → Core (IStubStore, StubEngine config)          ← Mediant CQRS here
```

### Why this boundary (best practice)
- **Performance:** mediator dispatch (even Mediant's ~59ns / very low alloc) is ideal for
  management operations but **wrong for a mock hot path** serving thousands of req/s — that
  path must stay direct and allocation-lean.
- **Core stays pure:** `Mockifyr.Core`'s zero-dependency rule already forbids Mediant there.
  Mediant lives only in `Mockifyr.Application` → the engine (the real IP) is independent of the
  external library; if Mediant must be frozen/swapped, the engine is untouched.
- **Separation clarity:** management use cases (validation, audit, idempotency, `Result<T>`)
  vs. real-time matching (pure engine). Two responsibilities, two mechanisms.

### What we use from Mediant (dogfooding → it gets tested too)
- `ICommand<T>`/`IQuery<T>` + `ICommandHandler`/`IQueryHandler`, the `Result<T>` pattern.
- **Pipeline behaviors:** FluentValidation (`Mediant.FluentValidation`), logging/audit,
  **idempotency** (a perfect fit for admin mutations), + a custom **tenant-scope guard
  behavior** (reject a command with no `tenantId` — enforce isolation in the pipeline).
- **`[HttpEndpoint]`** attribute mapping (`Mediant.AspNetCore`) → reduces controller
  boilerplate in the Admin facade.
- **Phase B synergies:** `Mediant.EntityFrameworkCore` + `IUnitOfWork` → with the G16 Postgres
  provider; `Mediant.Behaviors/Outbox` + `IIdempotencyStore` → a natural fit for reliable
  webhook delivery (at-least-once outbound); `IPublisher`/`IDomainEvent` → internal events for
  the future UI's live updates and the reload change-feed.

### Registration (composition root)
`Mockifyr.Server`: `services.AddMediant(cfg => cfg.RegisterServicesFromAssembly(
typeof(SomeHandler).Assembly))` + behavior ordering. Handlers live in `Mockifyr.Application`.

### Risk (honest note)
Mockifyr is commercializable IP; a dependency on Mediant (currently `1.0.0-rc.3`, pre-release)
couples release stability. **Mitigation:** Core is Mediant-free; only the application layer
depends on it. Both are the maintainer's IP and dogfooding is a goal → acceptable. See
`docs/decisions/0005-cqrs-mediant-application-layer.md`.

---

## 11. Differential harness and validation discipline

**Oracle = running Java WireMock standalone** (in a container via Testcontainers). The same
stub configuration is loaded into both, the same request is sent in parallel, the responses
are canonically diffed. A difference means Mockifyr is wrong. AI self-validation is forbidden.

```
WireMock JSON scenario (SINGLE source of truth)
   ├────────── raw ──────────► Java WireMock (Testcontainers) ──┐
   └── Adapters.MappingJson ─► Mockifyr (Facade.Http/Library) ─┤
Generator (seeded) ── same request ──► both sides ─────────────┤
   (empty/long/unicode/array-order/missing-extra field, boundary) ▼
                              Canonicalize + volatile-mask (header/JSON key order;
                              mask now/randomValue on both sides)
                                       │
                              byte-equal? → PASS · diff? → structured diff → regression corpus
```

- **Input generation is a property-based/fuzzing generator** (written once), deterministic
  seed. Where possible, a seed corpus from WireMock's own examples + mutation.
- **The determinism seam (clock/RNG) is on our side** for unit tests; because Java WireMock's
  clock/seed cannot be forced, in the differential path volatile outputs are masked on both
  sides (assert format/length/presence, not the value).
- **In matching groups** we diff not just the response bytes but the **selection decision**
  (same stub selected / same behavior on no-match).
- **Loop:** generate → send in parallel → diff → record the failing case → fix the engine →
  add to the regression suite → repeat. No group advances until its **oracle diff is green**.
- **Known limit:** the harness only catches what it can compare (request/response content,
  status, headers). Timing, socket-level faults (G4), TLS handshake (G11), and concurrency do
  not appear in a byte diff — handled separately.

---

## 12. Full parity roadmap (~40 validated steps)

Gate for every step: **green oracle diff** + the regression suite grows + a commit + a short
summary. At every checkpoint: stop, show, get approval. No autonomous drift. Living checklist:
[docs/roadmap.md](docs/roadmap.md).

### Phase A — Narrow vertical (first working, proven core)
- **G0** Foundation + harness: solution layout, engine interfaces, tenant model, InMemory
  store, differential harness (Java WireMock container + generator + canonical diff).
  *Gate: the harness diffs a trivial stub across both sides.*
- **G1 Matching:**
  - G1a URL basic (`urlEqualTo`, `urlPathEqualTo`, method + `ANY`)
  - G1b URL advanced (`urlMatching`, `urlPathMatching`, `urlPathTemplate` + named path vars)
  - G1c header/query/cookie (equalTo+caseInsensitive, equalToIgnoreCase, contains,
    notContaining, matches, doesNotMatch, absent; multi-value havingExactly/including)
  - G1d body basic (equalTo, binaryEqualTo, contains, matches)
  - G1e equalToJson (ignoreArrayOrder × ignoreExtraElements)
  - G1f matchesJsonPath (+ sub-matcher)
  - G1g equalToXml (placeholders/exemptedComparisons/namespaceAwareness) + matchesXPath
  - G1h matchesJsonSchema (V4–V202012)
  - G1i date/time matchers (before/after/equalToDateTime, offset, truncation)
  - G1j number matchers (equalTo/gt/gte/lt/lte)
  - G1k logic (and/or/not) + basicAuth + form/multipart + clientIp + **stub priority & selection**
- **G2 Response + templating:**
  - G2a static response (status, multi-value headers, body/jsonBody/bodyFileName+templating/
    base64Body, gzip)
  - G2b templating engine (Handlebars.Net + request model + named path vars; regex capture
    groups are **not exposed**)
  - G2c data helpers (jsonPath, xPath, soapXPath, parseJson, regexExtract, formData;
    + measure status/delay templating against the oracle)
  - G2d date (now, parseDate, truncateDate)
  - G2e random (randomValue, randomInt, randomDecimal, pickRandom)
  - G2f json manipulation (formatJson, jsonMerge, jsonArrayAdd, jsonRemove, jsonSort, toJson)
  - G2g format/math/array (numberFormat, formatXml, base64, urlEncode, trim, math, array/range/
    size, capitalize, assign/val, contains/matches block)
  - G2h system (hostname, systemValue + permitted-system-keys)
- **G3 Webhook / correlation:**
  - G3a serve-event listener + async outbound (method/url/headers/body)
  - G3b templated webhook + `originalRequest` correlation + sub-events + delay + thread pool

### Phase B — Everything else, up to parity
- **G4** Delay + fault (fixedDelay, delayDistribution lognormal/uniform, chunkedDribbleDelay,
  fault enums) — *socket-level faults out of byte-diff, handled separately*
- **G5** Scenarios (state machine, requiredScenarioState, newScenarioState, transition, reset/set)
- **G6** Verify + near-miss (journal, count matchers, findAll, near-miss distance, unmatched, reset)
- **G7** Full admin API (mappings CRUD/import/save/**first-class metadata**, requests, scenarios,
  recordings, near-misses, files, settings, reset, shutdown, version, health, docs)
- **G8** Proxying (proxyBaseUrl + templating, additional/remove headers, url-prefix trim,
  proxyVia, per-stub priority, timeout, passthrough, browser proxying, target allow/deny)
- **G9** Record & Playback (start/stop, snapshot, RecordSpec: filters/captureHeaders/
  extractBodyCriteria/requestBodyPattern/outputFormat/repeatsAsScenarios/transformers)
- **G10** Public extensibility (the 7 extension types — §9 seams made public)
- **G11** HTTPS/TLS + HTTP/2 (keystore/truststore, mutual TLS + SAN, http2, browser-proxy CA,
  cert generation) — *handshake out of diff, tested separately*
- **G12** Standalone/deploy (CLI flags, Docker image, config, mappings/__files dirs, filename
  template, facade-behavior tuning: CORS/gzip/chunked)
- **G13** gRPC extension (proto descriptor, message→JSON→engine→JSON→protobuf, matching, admin reset)
- **G14** GraphQL extension (GraphqlBodyMatcher, query normalize, variables matching)
- **G15** Message-based + extras (WebSockets 4.x-beta, JWT ext, Faker ext, multi-domain mocking)
- **G16** Persistence providers (FileBased / LiteDB / Postgres / Redis) + `IStubChangeSource`
  change-feed/reload

### Post-phase (Brief §8, not now — architecture is ready)
- **UI / dashboard:** developer-tool aesthetics (Linear/Vercel/WireMock Cloud/Mockoon
  reference), dark mode, design system (tokens + component library), `omercelik.dev` brand
  language. Sits on top of the admin API + reload contract.

### Java-specific → .NET analog (not ported blindly)
- running-without-http-server → `Facade.Library` · Servlet/Spring Boot → ASP.NET Core host ·
  JUnit 4/5 → xUnit/NUnit + TestServer integration.

---

## 13. Folder layout

```
mockifyr/
├── Mockifyr.sln
├── ARCHITECTURE.md
├── CLAUDE.md
├── README.md
├── src/
│   ├── Mockifyr.Core/
│   ├── Mockifyr.Matching/
│   ├── Mockifyr.Templating/
│   ├── Mockifyr.Stores.InMemory/
│   ├── Mockifyr.Adapters.MappingJson/
│   ├── Mockifyr.ServeEvents.Webhook/
│   ├── Mockifyr.Application/            # CQRS handlers (Mediant) — management path only
│   ├── Mockifyr.Facade.Library/
│   ├── Mockifyr.Facade.Http/
│   ├── Mockifyr.Facade.Admin/
│   └── Mockifyr.Server/
├── harness/
│   ├── Mockifyr.Differential.Harness/
│   └── Mockifyr.Differential.Generator/
├── tests/
│   ├── Mockifyr.Core.Tests/
│   ├── Mockifyr.Matching.Tests/
│   └── Mockifyr.Differential.Tests/
└── docs/
    ├── roadmap.md
    ├── decisions/     # ADRs
    └── parity/        # learned WireMock behavior
```

---

## 14. Parity risk areas (library-semantics matching)

Parity is not just "is the matcher correct" but "does the library behavior match". WireMock's
behavior depends on these Java libraries; we verify each against the oracle:

- **Handlebars.java (jknack)** → templating syntax + helper behavior (Handlebars.Net is not
  equivalent; helpers ported by hand + differential tests).
- **Jayway JsonPath** → `matchesJsonPath` + `jsonPath` (missing-path behavior, `$..` semantics).
- **XPath 1.0 (JAXP) / Saxon** → `matchesXPath` / `xPath` (namespaces, soap).
- **XMLUnit** → `equalToXml` (placeholder/exemptedComparisons/namespaceAwareness).
- **JSON Schema validator** (V4–V202012) → `matchesJsonSchema`.
- **datafaker** → `random` / Faker extension.
- **graphql-java / protobuf** → GraphQL/gRPC semantics.
- **Jetty vs Kestrel** → chunked encoding, client-cert SAN, socket-level faults
  (SO_LINGER=0 connection reset) — low-level behavior differences.

**Open ambiguity to verify:** whether `status`/`statusMessage`/`delay` support templating was
not confirmed in the docs → measured against the oracle in G2c (not decided from memory).

**Beyond-parity opportunity (not now):** cross-request data correlation (a tenant-scoped
"state bag"). WireMock has none (a scenario only carries a state name); the architecture can
take it painlessly.

---

## 15. Next steps (after approval)

1. Finalize this document → approval.
2. Scaffold the solution skeleton (projects, boundaries, empty interfaces — no implementation).
3. Scaffold the differential harness skeleton (Java WireMock container + generator + diff report).
4. Start the first vertical from **G1a**, via **TDD**: first a failing differential test
   against the oracle, then implementation, then green.
5. At every step: stop, show, get approval; commit + a short progress summary.
