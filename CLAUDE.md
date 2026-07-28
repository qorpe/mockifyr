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
- `docs/decisions/` — new/updated ADR whenever a design decision was made or changed (+ index row).
- `README.md` — flags table and feature claims still true.
- `CLAUDE.md` — repo map (§4) and status (§7) still true.
- **The docs website** ([mockifyr.omercelik.dev](https://mockifyr.omercelik.dev), repo
  `omercelikdev/mockifyr.omercelik.dev`) — every user-facing change (new flag, endpoint, screen,
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
enterprise-readiness addendum (`docs/parity/g19-sandbox.md`) — **G19 is done**. Remaining
work is documented **deferred edges** (per group in `docs/parity/`). Builds clean (0 warnings).
