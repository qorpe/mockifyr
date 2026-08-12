# CLAUDE.md — Operating Manual for Mockifyr

This repository is **AI-driven**: most work is done by an AI agent (Claude) under human
review. This file is the contract for how that work happens. Read it before touching
anything. Keep it up to date — when a rule changes, change it here.

---

## 0. What Mockifyr is

An independent, .NET-based API mock **engine + platform** with an integration sandbox —
an entirely independent codebase (no third-party mock engine is a dependency). The core engine is transport-agnostic; thin facades (Library / HTTP server /
Admin REST) sit on top. Correctness is proven by **differential testing against real
WireMock**, never by self-assessment.

- **Design & rationale:** [ARCHITECTURE.md](ARCHITECTURE.md)
- **Roadmap (living checklist):** [docs/roadmap.md](docs/roadmap.md)
- **Architecture decisions:** [docs/decisions/](docs/decisions/)
- **Learned WireMock behavior (parity knowledge):** [docs/parity/](docs/parity/)

---

## 0a. Licensing & trademark (non-negotiable)

- Mockifyr is licensed **Apache-2.0** ([LICENSE](LICENSE) + [NOTICE](NOTICE)). Keep new files
  compatible; do not add dependencies under incompatible licenses (no GPL/AGPL in shipped code).
- **WireMock is a trademark of WireMock Inc.; Mockifyr is independent and unaffiliated.** Only ever
  reference "WireMock" **nominatively** — to name the JSON stub format we import (interoperability) or
  the reference oracle we differential-test against. Never imply endorsement/affiliation, never use it
  in a product/package name or a logo. The oracle code stays in `harness/`+`tests/`, never shipped.
- **Positioning rule (decided 2026-07-28):** on **marketing surfaces** — the docs website, README,
  release notes, decks — the name does **not** appear at all, with exactly one exception: the
  website's migration guide (`/migration/`), which names it nominatively and carries a trademark
  disclaimer. Everywhere else public-facing, write "the reference engine" / "mapping JSON format".
  Engineering artifacts (`docs/parity/`, ADRs, `harness/`, `tests/`, code comments, commit history)
  keep naming it — they are the QA record, and the NOTICE disclaimer stays for that reason.
  Mockifyr is positioned as an independent **enterprise API mock + integration sandbox platform**,
  not as an alternative to any named product.

---

## 1. Language policy (non-negotiable)

- **Everything committed to this repo is in English** — source code, comments, XML doc
  comments, identifiers, commit messages, PR descriptions, all markdown docs, ADRs, and this
  file.
- Conversation with the maintainer may be in Turkish. **Never** let Turkish leak into repo
  artifacts.

---

## 2. Golden rules (guardrails)

1. **Narrow-vertical discipline.** Build only the current roadmap item. No "while I'm here"
   scope creep. Feature parity arrives gradually and validated, never all at once.
2. **Green differential diff is the only definition of done.** "Looks like it works" is not
   done. The oracle is running Java WireMock — not the model's memory, not a self-written
   assertion of what "should" happen.
3. **No self-validated tests.** Never approve your own output against your own assumption of
   correctness. The behavioral truth is the oracle.
4. **The engine stays pure.** `Mockifyr.Core` has zero external dependencies, does no I/O,
   and never references a transport, a mediator, or a persistence library. Delay/fault/proxy
   are **directives** the facade applies; outbound calls go through `IServeEventListener`.
5. **Transport never leaks into the engine.** Matching/templating logic lives behind Core
   contracts, never inside an HTTP handler.
6. **Multi-tenancy is first-class.** Every store/engine entry point takes an explicit
   `TenantId`. There is no tenant-less overload. Forgetting scope must be a compile error.
7. **No over-engineering.** Only the abstraction this vertical needs. The exceptions
   (multi-tenancy, persistence seam, extension seams) are deliberate because retrofitting
   them is the expensive path.
8. **Stop at every checkpoint.** Commit + short progress summary + wait for approval. No
   autonomous drift across roadmap items.

---

## 3. The development loop (per roadmap item)

Every item in [docs/roadmap.md](docs/roadmap.md) is developed the same way:

1. **Pick** the next unchecked item. Do not start the next one until the current is green.
2. **Failing test first (TDD).** Author the scenario as **WireMock JSON** (the single source
   of truth), load it raw into Java WireMock and via the import adapter into Mockifyr, drive
   the same request through both, and assert the diff. It must fail first.
3. **Implement minimally** — just enough to satisfy this item.
4. **Green diff.** The differential harness reports byte/semantic parity after
   canonicalization + volatile-field masking.
5. **Feed the docs (the learning step — do not skip):**
   - Record every non-obvious WireMock behavior discovered from the diff in
     `docs/parity/<group>.md` (e.g. how `equalToJson` treats nested array order). This is how
     the repo *learns*: assumptions become verified, durable facts.
   - Tick the checkbox in `docs/roadmap.md`.
   - If a design decision was made or changed, add/update an ADR in `docs/decisions/`.
6. **Commit** (see §5) and post a short summary. **Stop for approval.**

> The parity knowledge base (`docs/parity/`) is the memory of this project. Anything that
> surprised us about WireMock goes there so the next item builds on evidence, not guesswork.

---

## 3a. Definition of Done — tests (non-negotiable, applies to EVERY change)

The full contract lives in [docs/testing.md](docs/testing.md). The short form the agent must obey:

1. **Write the tests yourself, run them yourself.** Every change ships with the layers it touches:
   unit (pure logic + tenant isolation), wire/integration (real Kestrel host), differential (Docker
   oracle) for WireMock-dialect behavior, real-client self-tests where no oracle exists, and
   in-browser verification for UI. Never declare done on a build alone; never ask the human to
   verify what you can verify.
2. **Mutation-test new pure logic** with Stryker.NET (local tool). Target 100%; analyze and
   document any survivor as an equivalent mutant in `docs/parity/`.
3. **Run the edge-case checklist** (docs/testing.md) before any merge of a user-facing surface —
   hostile input, empty/missing, volume, unicode, boundaries, tenant isolation.
4. **All suites green before a PR**: `dotnet build` (0 warnings) + all four test projects + UI
   `tsc`/`lint`/`build`. CI repeats them, but green CI is confirmation, not discovery.

## 3b. Definition of Done — documentation (every issue fix and feature)

Documentation is part of the change, not a follow-up. A change is not done until, **in the same
branch/PR**:

- `docs/roadmap.md` — checkbox ticked / new item recorded.
- `docs/parity/<group>.md` — learned behavior, validation story, deferred edges (never silent).
- `docs/parity/deferred-edges.md` — the register: add a row when a gap is created, delete it when one
  is closed. A deferral recorded only in a group file is how the register came to be needed.
- `docs/decisions/` — new/updated ADR whenever a design decision was made or changed (+ index row).
- `README.md` — flags table and feature claims still true.
- `CLAUDE.md` — repo map (§4) and status (§7) still true.
- **The docs website** ([mockifyr.qorpe.com](https://mockifyr.qorpe.com), repo
  `qorpe/mockifyr.qorpe.com`) — every user-facing change (new flag, endpoint, screen,
  behavior) gets its guide/reference pages updated via a PR to that repo. Shipping a feature the
  website does not document is an unfinished change.

Everything written into either repository — code, comments, commits, PRs, issues, docs, website —
is **English only** (§1); conversation language never leaks into artifacts.

## 4. Where things go (repo map)

```
src/
  Mockifyr.Core/                 domain model + contracts + pure StubEngine (zero deps)
  Mockifyr.Matching/             IMatcher implementations
  Mockifyr.Templating/           Handlebars.Net renderer + ITemplateHelper set
  Mockifyr.Stores.InMemory/      tenant-scoped in-memory stores
  Mockifyr.Adapters.MappingJson/ mapping JSON <-> domain model import adapter
  Mockifyr.Adapters.OpenApi/     OpenAPI 3.x -> mapping JSON generator (G19c; Microsoft.OpenApi)
  Mockifyr.Crypto/               payload decryption at the edge (G20a; JWE dir+A256GCM, BCL only)
  Mockifyr.ServeEvents.Webhook/  IServeEventListener impl (outbound I/O)
  Mockifyr.Application/          CQRS handlers (Mediant) — MANAGEMENT PATH ONLY
  Mockifyr.Facade.Library/       in-process API
  Mockifyr.Facade.Http/          Kestrel mock server, tenant resolution, wire delivery
  Mockifyr.Facade.Admin/         /__admin/* REST (thin: HTTP -> CQRS dispatch)
  Mockifyr.Facade.Grpc/          gRPC serving (protobuf <-> JSON codec -> engine)
  Mockifyr.Facade.WebSocket/     WebSocket message serving (message-mappings -> matcher/templating)
  Mockifyr.Facade.Smtp/          opt-in ESMTP capture listener (MIME -> MessageEnvelope -> inbox)
  Mockifyr.Providers.Sms/        opt-in SMS provider profiles (Twilio-shaped API -> capture + realistic replies)
  Mockifyr.Server/               composition root (host, config, CLI)
harness/
  Mockifyr.Differential.Harness/   Java WireMock (Testcontainers) + canonical diff
  Mockifyr.Differential.Generator/ deterministic property-based/fuzzing generator
tests/                            unit + differential suites
docs/                             roadmap, decisions (ADR), parity knowledge
```

Dependency rule: **all arrows point inward to Core.** No facade depends on another facade.
External libraries (Handlebars.Net, JSONPath, XML, Kestrel, Mediant, Testcontainers) live
only at the edges. Mediant appears **only** in `Mockifyr.Application`.

---

## 5. Conventions

- **.NET 10** (LTS), C#. `Nullable` enabled, implicit usings off unless justified,
  file-scoped namespaces, `var` when the type is apparent.
- **Naming:** interfaces `I`-prefixed; async members return `Task`/`ValueTask` and end in
  `Async` except pipeline hooks that mirror Mediant's `Handle`.
- **Application layer** uses Mediant's `ICommand<T>`/`IQuery<T>` and the `Result<T>` pattern —
  no exceptions for control flow.
- **Commits:** Conventional Commits, English, imperative mood, e.g.
  `feat(matching): add urlPathTemplate named path variables`. Reference the roadmap item id
  (G1b) in the body. End co-authored commits with the required trailer.
- **Tests:** name by behavior and by oracle expectation. Differential tests are the primary
  safety net; unit tests cover pure logic and tenant-isolation invariants (which the oracle
  cannot check).

---

## 6. Build & test

Requires the .NET 10 SDK (pinned in `global.json`). NuGet restores from nuget.org only
(`nuget.config`); versions are centralized in `Directory.Packages.props`; shared MSBuild
settings live in `Directory.Build.props` (net10.0, nullable, warnings-as-errors).

```bash
dotnet build Mockifyr.sln -c Debug          # build all 16 projects
dotnet test  Mockifyr.sln -c Debug          # run unit tests
dotnet run   --project src/Mockifyr.Server  # run the standalone host (placeholder)
```

Differential tests (`tests/Mockifyr.Differential.Tests`) require Docker to run the Java
WireMock oracle; they are added from G0/G1a. CI (`.github/workflows/ci.yml`) builds and runs
the unit tests on every PR.

---

## 7. Current status

All roadmap groups **G1–G17 are complete** (see [docs/roadmap.md](docs/roadmap.md)), including the
post-phase **UI / dashboard**. Delivered across 19 projects: matching (G1 — URL/method,
header/query/cookie/body value matchers, `equalToJson`/`matchesJsonPath`/`matchesJsonSchema` incl.
`format` + networknt `typeLoose` + `$ref`, `equalToXml`/`matchesXPath` incl. XMLUnit placeholders +
namespaces + XPath functions, date/time, logic/basicAuth/multipart/priority); response + templating
(G2 — static, the Handlebars helper families, `parseJson` inline+block); webhooks (G3); delay/fault
(G4); scenarios (G5); verify/near-miss (G6); admin API + CQRS (G7); proxy (G8); record & playback
(G9); extensibility (G10); HTTPS + HTTP/2 (G11); the transport HTTP facade + standalone/config (G12);
gRPC (G13 — unary, codec incl. enum/map/repeated/oneof/wrappers, error status, admin-managed stubs);
GraphQL (G14 — query/variables/operationName matching + response templating); message-based extras
(G15 — Faker, JWT, multi-domain host/port/scheme, WebSocket message serving); persistence
(G16 — FileBased/LiteDB/Postgres/Redis + change-feed reload); and environments (G17 — tenant-scoped
`{{key}}` config resolved at serve time, across all four persistence providers; since #198 they also
ride along in export/import bundles). Correctness is proven **differentially
against the Java WireMock oracle** except where no stable oracle exists — racy helpers (Faker/JWT/
random/`now`) use structural/content validation, and WebSocket (WireMock beta) uses a self-test —
each such case is stated in `docs/parity/`. **G18 — message mocking (ADR 0009/0010)** is complete:
protocol-aware stub UX (computed `protocol`, channel-aware editors, descriptor admin + hot reload),
the tenant-scoped message inbox (`/__admin/messages` + dashboard Messages page), SMTP capture
(`--smtp-port`, AUTH-as-tenant), the Twilio SMS profile (`--sms-profile twilio`, stub-wins peek),
behaviors (SMTP faults/delay, simulated provider errors, capture webhook, `--message-limit`), and
verify/OTP (`/__admin/messages/otp`, `matches` regex filter) — validated by real-client self-tests
(MailKit, official Twilio SDK) plus Stryker mutation testing (100% on the message logic), no oracle
existing for message channels (`docs/parity/g18-messages.md`). **G19 — integration sandbox (ADR
0011)** is underway: G19a (tenant/collection-scoped resource store + `/__admin/resources`), G19b
(the `state` response directive — sandbox CRUD with `{{state.*}}` templating), and G19c (OpenAPI
3.x import — `/__admin/openapi/import` + the Add-stub OpenAPI channel, optional stateful CRUD
wiring), and G19d (sandbox access — `--sandbox-auth` with `mfk_` API keys via `/__admin/apikeys`,
salted-SHA-256 + constant-time verify, key→tenant resolution ahead of the ADR 0003 chain, per-key
hourly quotas with honest 429 + rate headers, keys persisted via the G16 seam) and G19e (the Sandbox UI —
sidebar **Sandbox** group with Resources + Access screens, one-time token reveal, dashboard
"spin up a sandbox" quick-start, all six locales) are complete under the ADR's
enterprise-readiness addendum (`docs/parity/g19-sandbox.md`) — **G19 is done**.

**Post-G hardening (v0.18.0–v0.19.0)**, each opt-in and backward compatible, recorded in
`docs/parity/g7-admin.md`: a bounded request journal with an indexed detail lookup
(`--journal-limit`/`--journal-disabled`, eviction semantics proven differentially, #220);
`/__admin/health` exempt from admin auth so probes never restart-loop a pod (#218); journal masking
that keeps named headers and JSON fields out of storage entirely (`--mask-headers`/
`--mask-body-fields`, #227 — opt-in because the journal also backs verify); per-tenant admin
credentials that turn the tenant header from a claim into an authorization decision
(`--tenant-credential`, #224); a startup warning plus `--block-outbound-routes` for an
unauthenticated admin surface (#225); and `SECURITY.md` with private disclosure (#217).

**G20 — payload cryptography (ADR 0012, issue #226) is complete:** field-level and whole-body
request decryption (`request.decrypt`), response protection (`response.protect`), and signing
(`request.signature` / `response.sign`, PSD2 / Berlin Group shape) — four pure Core seams
(`IPayloadDecryptor`/`IPayloadProtector`/`ISignatureVerifier`/`IResponseSigner`) with every key at
the host edge in `Mockifyr.Crypto`; Core still has zero external dependencies. No oracle exists, so
validation is round-trips against independent implementations of the same RFCs
(`docs/parity/g20-cryptography.md`). Deferred: asymmetric signatures, the full Berlin Group signing
string, key rotation, wrapped-key JWE.

**Enterprise readiness (epic #253)** is running as its own track of hardening issues rather than a
roadmap group, recorded in `docs/parity/deployment.md` and `docs/parity/g7-admin.md`: deployment
posture (non-root image, `/__admin/live` + `/__admin/ready` with drain, a Helm chart whose posture is
asserted by `deploy/helm/verify-chart.py` in CI — #241–#243); supply-chain evidence (SBOM, keyless
cosign signing, provenance, Trivy, Dependabot — #244/#245); observability (OpenTelemetry traces and
metrics, a credential-free Prometheus scrape at `/__admin/metrics`, JSON logs, deliberately bounded
label cardinality — #246); and the admin audit trail (`--audit`: every admin change recorded with
principal/tenant/action/target/outcome at `/__admin/audit`, on the dashboard, and as an `admin.audit`
log line for a SIEM — #247). Remaining: documentation and support policy (#248), benchmarks (#249),
Key sources and rotation (#250) are done: `--decrypt-key-file`/`--sign-key-file`/`--admin-pass-file`,
a key **ring** (produce with the newest, accept every active key) re-read on change so rotation needs
no restart, an `IKeySource` seam for Vault/KMS, and a Helm mount mode that keeps keys out of the
process listing. **OIDC (#251) shipped in 1.4.0** after being deferred and then asked for: bearer validation against
the issuer's discovery keys, a claim that scopes an identity to one tenant exactly as
`--tenant-credential` does, an optional required role, `oidc:<user>` in the audit trail, and dashboard
sign-in via authorization code + PKCE. It is a third principal source in the existing host-edge
middleware chain — Basic keeps working beside it, and Core is untouched. The measured performance
envelope and sizing guidance are in `docs/parity/performance.md`, with the harnesses in `bench/`
(BenchmarkDotNet for the engine, k6 for the transport); measuring immediately paid for itself: #266 (templates
were recompiled on every request) is fixed — a templated response went from 699 µs to 1.21 µs — and
#265 is fixed too — candidate indexing took matching the last of 1000 stubs from 29.1 µs / 94.8 KB to
392 ns / 1.33 KB, with the differential suite proving the semantics did not move. Backup and restore
(#252) and the policy documents (#248 — `CHANGELOG.md`, `VERSIONING.md`, `SUPPORT.md`,
`CONTRIBUTING.md`) are done.

**1.0 (2026-08-03)** makes the compatibility promises in `VERSIONING.md` binding: a breaking change
now means a major version. The last silent gaps became loud first — a mapping using `bodyFileName` or
a non-`uniform` `delayDistribution` is imported *and reported*, on the admin API and at startup, since
a documented gap you can only discover from behaviour is not really documented.

The deferred edges worth doing are closed since 1.0: `bodyFileName` (file-backed response bodies,
oracle-verified), **durable sandbox resources** (seeded documents survive a restart on every
persistence backend, deletes and resets included), and **tenant-scoped recording** (a global session
used to proxy every tenant's traffic to one target). Probing the oracle for the "missing" arithmetic
helpers found the documentation had it backwards — the reference engine rejects `add`/`subtract`/etc.
too — and turned up two real `math` defects instead: `%` was rejected though the oracle supports it,
and integer division rounded the wrong way for negatives.

The **deferred-edge register**, `docs/parity/deferred-edges.md`, is the single answer to "is anything
open?", with a verdict per item (out of scope / tracked / accepted). The per-group parity files still
hold the narrative, but they record deferrals as of the group that wrote them, so they are not a count.
Its Tracked section is currently **empty**: the one entry it held — change-feed reload for environments
and sandbox resources (#279) — shipped in 1.5.0, and testing it turned up a second defect the analysis
had not predicted: a host reloaded on its own announcement and could hand an operator their own write
back at the previous version, so every announcement now carries the writer's identity.

Auditing the **documentation website** against the shipped surface (every CLI flag, every admin route)
found the reference complete — 53 of 54 flags, all 32 routes — and the teaching layer missing in two
places: the integration sandbox had no page at all, only rows in the admin tables, and verification was
documented as routes rather than as the two questions people arrive with. Both now have guides. Writing
the second one turned up a missing endpoint rather than a missing paragraph: the request journal could
not be cleared at all, and the reference engine's spelling for it (`DELETE /__admin/requests`, not the
intuitive `POST …/reset`, which 404s on both) shipped in 1.6.0 with a dashboard action beside it.
Auditing the site also produced the first of a set of filed **capability ideas** (#287–#291), of which
**tenant clock control (#290)** is delivered: `PUT /__admin/clock` freezes or shifts the instant a
tenant's templates see, so a token that expires in an hour is testable without waiting an hour. It is
the change that made `now` — a helper recorded as racy since G2 because no oracle can agree on a moving
clock — deterministic enough to assert exactly. The journal, audit trail and inbox keep real time by
design.

**Near-miss diagnostics (#288)** followed: ranking by distance had existed since G6 but only on the
in-process API and only as a number, which does not answer the question anybody has — *which part of my
stub disagreed*. It is now an admin query (never the 404 body, so the served response the differential
suite pins stays byte-identical), naming each attribute in the mapping JSON's own vocabulary so a
reader can search their own file for the string we printed.

**Tenant degradation profiles (#289)** followed the same reasoning one level up: `delay` and `fault`
describe one stub, and the question teams actually ask is what happens when a whole dependency degrades.
A profile composes with each stub rather than replacing it, is deterministic from a seed the host always
reports (so a chaos run becomes a regression test), and never touches the admin API — a profile an
operator could not undo would be a trap, not an instrument.

**Contract conformance (#287)** is the first slice of the largest idea: a mock that has silently drifted
from the API it models is worse than no mock, because it manufactures confidence. `POST
/__admin/openapi/verify` joins two things the repo already had — an OpenAPI reader and the stub set —
and reports what disagrees without ever changing it. Mutation testing paid for itself twice here: it
found that the check read the path fields in the opposite precedence to the engine, and that which
operation an ambiguous stub belonged to was being decided by enumeration order.

Its second slice — **drift against reality** — followed: with a recording session live,
`POST /__admin/recordings/verify` compares what the upstream just returned against what the stubs
would have answered. Structural, never literal, so ids and timestamps do not drown the findings; and it
serves nothing while it looks, which is why `StubEngine.FindMatch` was extracted from `Handle` rather
than reimplemented — a diagnostic matching by different rules than the server would describe a host
that does not exist.

The third slice — **traffic conformance** — completes it: `POST /__admin/requests/verify` asks whether
the *consumer* stayed inside the contract, which is the failure a permissive mock hides completely. All
three checks share one engine and one set of ambiguity rules, so two reports about the same document
cannot disagree about which operation a path belongs to.

**G21 — the broker channel (ADR 0013)** has begun. The integration sandbox was HTTP-shaped: a team could
mock the call that starts a payment and not the event that reports it settled. Slice 1 ships
`publish` as a post-serve action beside `webhook`, so a stub answers 201 *and* emits — Core still never
learns what a broker is, because the I/O is an `IServeEventListener` in `Mockifyr.Facade.Broker`.
Validated against a real Kafka container with the official client, since a fake broker proving a mock
works would prove nothing. The ADR's own image-size trigger fired (+20 MB) and the recorded judgement
is that the split it prescribed is not worth taking yet — with the number, and the condition to
revisit, written down. Slice 2 adds **capture**: `--kafka-subscribe` lands what the system under test
publishes in the tenant's message inbox, so `/__admin/messages` and its verify surface answer for broker
messages with no new API — one inbox, as the ADR decided. Writing it found a bug that would have
shipped: the admin API projected a message's channel with a two-way ternary, so every broker message
would have been labelled "sms" the moment a third channel existed.

**G22 — the sandbox as a partner-facing platform (epic #345, ADR 0015)** has begun. Asking what the
sandbox would need before it could be handed to an external partner produced thirteen issues in three
phases, and the analysis found something sharper than a gap: a spec containing
`/customers/{customerId}/orders` imported to a flat global `orders` collection, so every modelled
customer listed every other customer's orders. **#350 — relations** is complete and is a defect fix, not
an enhancement. Relations are declared once per collection and derived from the path shape at import,
because that path already *is* the sentence "orders belong to customers, keyed by customerId". Where the
key lives is the contract's decision: in the body when the modelled document declares the field, and in
an optional metadata pointer otherwise — so the body still round-trips byte-for-byte and `POST
/__admin/openapi/verify` cannot report our own sandbox as drifted from the document we generated it
from. `onDelete` defaults to `restrict` (deleting a Stripe customer does not delete their charges), and
enforcement is presence-triggered, which keeps mutually referencing collections creatable and makes
cycles in the relation graph legal. Serving the imported spec — rather than reading the generator —
found a second bug that had been there since G19c: the created-resource `Location` header was composed
from the specification's template text, so a nested collection answered a Location containing a literal
`{customerId}`. Relations are stored as documents under a reserved collection, so they persist, restore
and reload on all four backends with no per-backend code; holding them in memory would have let them
vanish on restart while their documents survived, quietly restoring the very defect.

**G22 Phase 1** is under way. **#346 — a partner-safe principal** shipped: `--partner-credential` is
`--tenant-credential`'s scoping plus a refusal on every way this host acts on the network. The issue
listed three admin routes; half the capability turned out to live in the data plane, because
`POST /__admin/mappings` accepts `proxyBaseUrl` and post-serve actions — so blocking routes alone would
have shipped a control that looks like it holds and does not. Proving the "refusals are audited"
criterion rather than asserting it found that an operator and a partner scoped to the same tenant both
read as `tenant:<name>`, in the very entries the class exists to produce; a partner is now
`partner:<name>`. **#348 — secret environment values** followed, taken ahead of #347 because that
issue's checklist promises a partner may read environment values but never secrets, which was
unenforceable while every value was plaintext. A secret is withheld from the admin API, the dashboard
and export bundles, and still resolves at serve time; a redacted read handed back on save means
*unchanged* rather than empty. Driving the real dashboard found it would have **deleted** secrets on an
untouched save. **#347 — the partner self-service surface** completes the pair: `/__sandbox/*` is
reachable with the `mfk_` key a partner already holds and answers only for that key's tenant. It is a
separate namespace rather than a loosened `/__admin`, because ADR 0011 binds that surface to ignore
sandbox keys entirely and a test asserts it — standing beside it keeps the rule literally true instead
of true-by-audit. There is no tenant header on this surface at all, so cross-tenant access is not a
check that could be wrong. **#349 — edge hardening** closes Phase 1: `--allow-outbound-host` bounds
the hosts this instance may call (checked against the *rendered* webhook URL, because a template may be
`{{request.headers.X-Callback}}`), `--max-request-body-bytes` replaces Kestrel's blanket ~30 MB with a
ceiling and a per-tenant value clamped beneath it, and `--allow-origin` lets a browser application in
while the admin API stays same-origin. All three are off by default and each ships beside a test of the
*unconfigured* host. Building the first found a defect it was not looking for: a proxy refusal was
thrown where only container-diagnosis failures were caught, so the one proxy outcome the host can
explain completely would have reached the caller as an opaque 500.

**G22 Phase 2** follows: **#353 — resource querying** (filter, sort and field selection over a
collection, on both the admin listing and the served `list`, sharing one evaluator so the sandbox and
the screen watching it cannot disagree; the filter vocabulary is the dialect's own) and **#351 — named
datasets** (a scenario across collections, loaded and reset in one call, with Faker reachable from a
seed and a fixed seed making it reproducible). The dataset work needed two orderings for two different
reasons — parents first on load because integrity refuses an orphan child, children first on unload
because `restrict` refuses a parent with children — and a compensating rollback rather than a pretend
transaction, since integrity can only be checked against documents that exist. Seeding uses an ambient
scope rather than Bogus's process-wide static, so a load cannot make concurrently served responses
deterministic.

Builds clean (0 warnings); 1122 tests green across the four suites.
