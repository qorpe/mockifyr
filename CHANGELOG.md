# Changelog

Every released version, newest first. Each entry links to the full release notes, which carry the
detail; anything that changed a default or broke a documented behavior is called out here.

This file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Mockifyr follows
semantic versioning as described in [VERSIONING.md](VERSIONING.md).

## Unreleased

### Changed

- **The dashboard shell runs on the shared kit** (ADR 0014 M4). The sidebar, app frame and command
  palette are the family kit's now; what stays mockifyr's is what only mockifyr knows — the brand mark,
  the routes, the live per-tenant counts, and the tenant switcher and preferences in the rail foot.
  Two things an operator gains: **nav items are real links**, so ⌘-click opens the journal in a second
  tab while stubs stay open here, and the **tenant switcher is pinned** rather than scrolling away as
  the nav grew. Everything else is deliberately identical — verified screen by screen, in light and
  dark, expanded and collapsed.

## Released

### [v1.14.0](https://github.com/qorpe/mockifyr/releases/tag/v1.14.0) — 2026-08-09

#### Changed

- **The dashboard now runs on the shared `@qorpe/ui` kit** (#315, #316, #317, #318 — ADR 0014).
  Most of it is invisible on purpose: facets, search, tooltips, sheets, the JSON editor, buttons,
  switches and empty states behave as they did, but come from one tested source instead of a copy
  per project. Two things are visibly better:
  - **Every form select is the application's own listbox**, not the operating system's popup that
    never matched the surrounding menus. Each one carries an accessible name (the kit's type refuses
    a nameless select), keyboard walking is announced properly, options can be disabled, and the list
    flips when it would fall off the viewport.
  - **The preferences menu's toggles are real checkbox menu items** rather than divs that looked like
    them, so a screen reader reports their state.

  The theme contract moved with them: the dashboard's own stylesheet went from 159 lines of tokens to
  21, with only `--brand` still local. Domain visuals — method and status chips, illustrations,
  branding — stayed local by decision; adopting a kit is not a restyle.

  **Nothing about the mock engine, the admin API or the mapping dialect changed.**

#### Fixed

- **CI now keeps the name of a failing test.** A run reported "1 failed" out of 416 and the name went
  with it, so an intermittent failure could be neither fixed nor honestly dismissed. Test results are
  written as TRX and uploaded on success and failure alike. The first flake it identified —
  `G15eWebSocketBroadcastTests` racing the server's channel registration — is fixed with it (#320,
  #325). Neither change affects the shipped product.
- **The dashboard could load from a stale browser cache.** `index.html` carried no cache directive, so
  a browser could run a bundle older than the host it was talking to — one predating a capability such
  as the OIDC login gate — and fail in ways that read as a server bug. The shell and every unhashed
  file now revalidate (`no-cache`); Vite's content-hashed `assets/` output is marked `immutable`,
  which it earns because its name changes whenever its content does.

### [v1.13.0](https://github.com/qorpe/mockifyr/releases/tag/v1.13.0) — 2026-08-06

#### Fixed

- **`?channel=broker` filtered nothing.** `GET /__admin/messages` and `/count` returned every message
  in the inbox for the broker channel, because the filter recognised only `email` and `sms` and
  treated anything else as "no filter". Present since 1.10.0.

#### Added

- **The broker channel on the dashboard.** The Messages screen gains a **Broker** filter beside Email
  and SMS, a channel label on each row, and a detail panel that names a broker message by its topic
  and shows its partition key — a message addressed to a topic has no recipients, so the sender→
  recipient line it used to render described a delivery that never happened. Six locales.

### [v1.12.0](https://github.com/qorpe/mockifyr/releases/tag/v1.12.0) — 2026-08-06

#### Added

- **AMQP / RabbitMQ** (#291, ADR 0013 slice 4 — the broker channel is complete). `--amqp-uri` and
  `--amqp-subscribe` give the second transport behind the same seam: publishing from a stub, capture
  into the message inbox, and serve on consume, all with the mapping shape and admin routes slice 3
  shipped and no transport-specific matching. `"topic": "exchange/routing.key"` addresses an exchange;
  a topic with no slash uses the default exchange, so `{"topic":"orders.events"}` means the same thing
  on both transports. A partition key becomes the message's `MessageId`, since AMQP has no
  counterpart. A host may configure both brokers, and a `kafka:` or `amqp:` topic prefix then names
  one — an unprefixed topic goes to Kafka.

### [v1.11.0](https://github.com/qorpe/mockifyr/releases/tag/v1.11.0) — 2026-08-06

#### Added

- **Serve on consume** (#291, ADR 0013 slice 3). A `brokerMappings` stub matches an inbound broker
  message and publishes the messages it declares, so Mockifyr can stand in for an event-driven
  component rather than only emit alongside an HTTP response. Registered at
  `POST /__admin/broker-mappings`, with list, delete and reset beside it. `whenTopic`, `whenHeaders`
  and `whenMessage` reuse the existing value and body matchers unchanged; replies template against
  `message.body`, `message.topic`, `message.key` and `message.headers.<name>`, and resolve the tenant's
  environment keys and clock. Every matching mapping contributes — a fan-out is a real broker pattern —
  and an unmatched message is captured and acknowledged rather than parked.

### [v1.10.1](https://github.com/qorpe/mockifyr/releases/tag/v1.10.1) — 2026-08-06

#### Changed

- **A `publish` action on a host with no broker is now reported instead of being silent.** Such a stub
  served its response and emitted nothing, which is indistinguishable from a broker outage — and the
  missing `--kafka-bootstrap` flag is the last place anybody would look. The gap is now reported on
  `POST /__admin/mappings`, on `/__admin/mappings/import`, and at startup for mappings loaded from
  disk, through the same warning surface `bodyFileName` and `delayDistribution` use. The stub is still
  created; the goal is to be loud, not strict.
- **A failed publish now records the message it was carrying.** The journal's `publishes` entry
  reported `"key": null, "body": null` on any failure, so a template mistake and an unreachable broker
  looked identical after the fact. Both are recorded now. Nulls remain for the one case where they are
  a fact: rendering itself failed, so there was never a message.

### [v1.10.0](https://github.com/qorpe/mockifyr/releases/tag/v1.10.0) — 2026-08-06

#### Added

- **Broker capture** (#291, ADR 0013 slice 2). `--kafka-subscribe orders.events` lands what your system
  publishes in the tenant's message inbox, so `/__admin/messages` and its count/verify surface answer
  for broker messages with no new API. Topic, partition, offset and key are recorded; an
  `X-Mockifyr-Tenant` message header addresses a tenant. Offsets commit after the inbox write, so a
  crash redelivers rather than loses.
- **The broker channel, first slice** (#291, ADR 0013). A stub can answer a request *and* publish a
  message: `postServeActions: [{"name":"publish","parameters":{"topic":…,"key":…,"body":…}}]`, with
  every field templated against the triggering request. Opt in with `--kafka-bootstrap`; a host
  without it is unchanged. The journal shows what went out and what failed, and an unreachable broker
  never takes the served response down with it.

### [v1.9.0](https://github.com/qorpe/mockifyr/releases/tag/v1.9.0) — 2026-08-05

#### Added

- **Traffic conformance** (#287, third slice). `POST /__admin/requests/verify` checks what clients
  actually sent against an OpenAPI document: calls to operations the contract never declared, missing
  required query parameters and headers, and request bodies the schema forbids. Reads the journal and
  changes nothing. With the stub check and the recording check, all three questions #287 asked are now
  answerable.
- **Drift against reality** (#287, second slice). With a recording session live,
  `POST /__admin/recordings/verify` compares what the real upstream just returned against what your
  stubs would have answered — reporting a field the upstream grew, one only the stub has, a changed
  type, a changed status, or a request no stub matches at all. Structural, not literal: ids,
  timestamps and totals differ between environments and are never reported. It serves nothing while
  it looks — no journal entry, no scenario advances.

### [v1.8.0](https://github.com/qorpe/mockifyr/releases/tag/v1.8.0) — 2026-08-05

#### Added

- **Contract conformance** (#287). `POST /__admin/openapi/verify` checks a tenant's stubs against an
  OpenAPI document and reports what disagrees: stubs answering operations the specification no longer
  declares, operations no stub answers, undeclared statuses, and response bodies that violate the
  declared schema — with coverage counts. A report, never a mutation. Templated bodies, regular-
  expression stubs and schemaless operations are deliberately left alone.
- **Tenant degradation profiles** (#289). `PUT /__admin/degradation` degrades a whole dependency for
  one tenant — added latency with jitter, a share of responses answered with an error status, a share
  of connections broken outright — composing with whatever each stub already declares instead of
  replacing it. `DELETE` restores full health in one call. Deterministic: every profile carries a seed
  (generated and reported when you do not supply one), so a run that found something can be replayed.
  The admin API is never degraded, and one tenant's outage leaves the others healthy.
- **Near-miss diagnostics** (#288). `GET /__admin/requests/{id}/near-misses` explains why a journaled
  request matched nothing — per attribute, in the mapping JSON's own vocabulary (`urlPath`,
  `headers['X-Api-Key']`, `bodyPatterns[0]`), with what the request actually carried there and the
  stub's own request block beside it. `POST /__admin/near-misses/request` answers the same question for
  a request you have not sent yet. The served 404 is unchanged.
- **Tenant clock control** (#290). `PUT /__admin/clock` freezes a tenant at an instant
  (`{"frozenAt": "2027-01-01T00:00:00Z"}`) or shifts it (`{"offsetSeconds": 86400}`); `DELETE` returns
  it to real time. Response templating, the date helpers, minted JWTs and webhook templates all read
  the tenant's clock, so a token that expires in an hour is testable without waiting an hour. The
  request journal, the audit trail and the message inbox keep real time — they record what actually
  happened. In-memory and per tenant; a host that sets no clock is unaffected.

### [v1.7.0](https://github.com/qorpe/mockifyr/releases/tag/v1.7.0) — 2026-08-04

#### Added

- The dashboard's Journal screen has a **Clear journal** action, behind a confirmation, in all six
  locales.

#### Fixed

- The Messages screen's *Clear all* confirmation stayed open after confirming, over a table that had
  already emptied.

### [v1.6.0](https://github.com/qorpe/mockifyr/releases/tag/v1.6.0) — 2026-08-04

#### Added

- `DELETE /__admin/requests` discards the tenant's request journal, so a suite sharing one host can
  clear it between tests instead of restarting. The reference engine spells it the same way; its
  `POST /__admin/requests/reset` answers 404 there and here.

### [v1.5.0](https://github.com/qorpe/mockifyr/releases/tag/v1.5.0) — 2026-08-03

#### Fixed

- Environment keys and sandbox documents now travel on the change feed, like stubs. With several
  replicas behind one PostgreSQL or Redis and `--change-feed`, a key's active value changed on one
  replica was honoured there and nowhere else until the others restarted; the same held for sandbox
  documents. Deletes propagate too. (#279)
- A host no longer reloads because of its own write. Every announcement now carries the writer's
  identity, so a host skips its own — previously a reload triggered by a host's own change could read
  the backend before its next write landed and hand an operator their change back at the previous
  version.

### [v1.4.0](https://github.com/qorpe/mockifyr/releases/tag/v1.4.0) — 2026-08-03

#### Added

- OIDC on the admin API and the dashboard (`--oidc-authority` and friends): bearer tokens validated
  against the issuer's published keys, an optional claim that scopes an identity to one tenant, an
  optional required role, and dashboard sign-in via authorization code + PKCE. Basic credentials keep
  working alongside it. The audit trail records `oidc:<user>`, never the token. (#251)

### [v1.3.0](https://github.com/qorpe/mockifyr/releases/tag/v1.3.0) — 2026-08-03

#### Added

- Recording is tenant-scoped. Two tenants can record at once against their own upstreams; one
  tenant's session no longer discards another's captures or proxies their traffic.
- `math` supports `%`, which the reference engine has always supported.

#### Fixed

- `math` integer division rounded half *up* instead of half *away from zero*, so `-9/2` answered
  `-4` where the reference engine answers `-5`. Positive operands were unaffected.

### [v1.2.0](https://github.com/qorpe/mockifyr/releases/tag/v1.2.0) — 2026-08-03

#### Added

- Sandbox resources are durable. With any persistence backend (file system, LiteDB, PostgreSQL,
  Redis) documents seeded into a sandbox survive a restart, and deletes and resets survive it too.
  In-memory hosts are unchanged.

### [v1.1.0](https://github.com/qorpe/mockifyr/releases/tag/v1.1.0) — 2026-08-03

#### Added

- `bodyFileName`: a response body can be a file under `<root-dir>/__files` instead of an inline
  string. Templated when `response-template` is declared, inline `body` wins when a stub has both,
  and a missing file answers **500** naming the file — all three verified against the reference
  engine. File names are resolved inside the store only; a name that escapes it is refused.

#### Removed

- The import warning for `bodyFileName`, which is implemented now. The `delayDistribution` warning
  stays.

### [v1.0.0](https://github.com/qorpe/mockifyr/releases/tag/v1.0.0) — 2026-08-03

The compatibility promises in [VERSIONING.md](VERSIONING.md) become binding: from here, a breaking
change means a major version.

#### Added

- Import warnings. A mapping that uses a field this engine accepts but does not act on now says so —
  as a `warnings` array on `POST /__admin/mappings` and `/__admin/mappings/import`, and as a console
  line for mappings loaded from disk. The stub is still created; the point is to be loud, not strict.
  Covers `bodyFileName` (matches, empty body) and non-`uniform` `delayDistribution` (no delay), the
  two gaps that were previously silent.

#### Changed

- Versioning policy: the pre-1.0 clause that let a minor release break you is gone.

### [v0.25.0](https://github.com/qorpe/mockifyr/releases/tag/v0.25.0) — 2026-08-02

Matching stops caring how many stubs you have.

#### Changed

- Stubs are indexed by method and path, so matching no longer evaluates every stub in the tenant.
  Finding the last of 1000 went from **29.1 µs / 94.8 KB to 392 ns / 1.33 KB** — the same cost as
  matching a single stub. Behaviour is unchanged, proven by the differential suite. Stubs whose URL
  is a pattern rather than a literal path are still evaluated on every request. (#265)
- `/__admin/health` reports the running build's version instead of a hard-coded `"1.0"`. (#265)

### [v0.24.0](https://github.com/qorpe/mockifyr/releases/tag/v0.24.0) — 2026-08-02

Key rotation without a restart, and the last of the enterprise-readiness track.

#### Added

- Key files: `--decrypt-key-file`, `--sign-key-file` and `--admin-pass-file` read secrets from disk
  instead of the command line. A key file holds a ring — new payloads use the newest key while every
  key in the file is still accepted — so a rollover is add, drain, remove with no restart at any
  step. `/__admin/health` reports how many keys are active per capability. (#250)
- `cryptography.mountAsFiles` in the Helm chart: the Secret is mounted read-only and no key reaches
  the container's arguments or environment. (#250)

#### Changed

- `VERSIONING.md` now records what is **deliberately deferred**, starting with OIDC login for the
  dashboard (#251) — deferred past 1.0, with the reasoning and the practical alternative.

### [v0.23.0](https://github.com/qorpe/mockifyr/releases/tag/v0.23.0) — 2026-08-02

Operations release: the admin audit trail, tenant backup and restore, a published performance
envelope, and the template-recompilation fix the benchmarks found.

### Added

- Backup and restore for a whole tenant: `GET /__admin/backup` produces one archive of stubs,
  environment keys, sandbox documents, API keys and scenario states, and `POST /__admin/restore`
  puts it back. Also in the dashboard under Settings. (#252)
- Admin audit trail (`--audit`): every administrative change is recorded with principal, tenant,
  action, target and outcome, readable at `/__admin/audit`, on the dashboard's Audit screen, and as
  a structured `admin.audit` log line. (#247)
- Observability: OpenTelemetry traces and metrics, a credential-free Prometheus scrape at
  `/__admin/metrics` (`--metrics`), OTLP export (`--otel-endpoint`) and JSON logs (`--log-json`). (#246)
- Deployment posture: an unprivileged container image with a self-probe (`--healthcheck`), split
  liveness and readiness endpoints with drain-on-shutdown, and a Helm chart whose security posture is
  asserted in CI. (#241, #242, #243)
- Supply-chain evidence on every release: SBOM, build provenance, keyless cosign signatures and
  container scanning. (#244, #245)
- This changelog, plus [VERSIONING.md](VERSIONING.md), [SUPPORT.md](SUPPORT.md) and
  [CONTRIBUTING.md](CONTRIBUTING.md). (#248)
- A published performance envelope and sizing guidance, with a BenchmarkDotNet project for the
  engine and a k6 harness for the HTTP facade. A short benchmark run now guards every pull
  request. (#249)

### Fixed

- Templated responses recompiled their Handlebars template on **every request**. Compiled templates
  are now cached, taking a templated response from 699 µs to 1.21 µs — the defect the new benchmarks
  found on their first run. (#266)



### [v0.22.0](https://github.com/qorpe/mockifyr/releases/tag/v0.22.0) — 2026-07-30

Cryptography, visible in the dashboard.

### [v0.21.0](https://github.com/qorpe/mockifyr/releases/tag/v0.21.0) — 2026-07-30

G20 complete: encrypted and signed payloads end to end.

### [v0.20.0](https://github.com/qorpe/mockifyr/releases/tag/v0.20.0) — 2026-07-30

G20a: encrypted payloads become matchable.

### [v0.19.0](https://github.com/qorpe/mockifyr/releases/tag/v0.19.0) — 2026-07-29

Hardening: journal masking, tenant credentials, open-admin guardrails.

### [v0.18.0](https://github.com/qorpe/mockifyr/releases/tag/v0.18.0) — 2026-07-28

Hardening: bounded journal, probe-safe auth, private disclosure.

**Changed default:** the request journal became bounded at 1000 entries per tenant (was unbounded). `--journal-limit 0` restores the old behavior.

### [v0.17.0](https://github.com/qorpe/mockifyr/releases/tag/v0.17.0) — 2026-07-27

Sandbox UI (G19e): the integration sandbox is complete.

### [v0.16.0](https://github.com/qorpe/mockifyr/releases/tag/v0.16.0) — 2026-07-27

Sandbox access: API keys + quotas (G19d).

### [v0.15.0](https://github.com/qorpe/mockifyr/releases/tag/v0.15.0) — 2026-07-27

Spec in, working sandbox out: OpenAPI import.

### [v0.14.0](https://github.com/qorpe/mockifyr/releases/tag/v0.14.0) — 2026-07-27

Stateful stubs: POST creates what GET returns.

### [v0.13.0](https://github.com/qorpe/mockifyr/releases/tag/v0.13.0) — 2026-07-27

Sandbox foundations: tenant-scoped resource collections.

### [v0.12.3](https://github.com/qorpe/mockifyr/releases/tag/v0.12.3) — 2026-07-27

Recorded repeats replay in order.

### [v0.12.2](https://github.com/qorpe/mockifyr/releases/tag/v0.12.2) — 2026-07-27

Faithful recording of compressed APIs.

### [v0.12.1](https://github.com/qorpe/mockifyr/releases/tag/v0.12.1) — 2026-07-27

Recordings flow, completed.

### [v0.12.0](https://github.com/qorpe/mockifyr/releases/tag/v0.12.0) — 2026-07-27

Test a stub without leaving the dashboard.

### [v0.11.0](https://github.com/qorpe/mockifyr/releases/tag/v0.11.0) — 2026-07-27

Environments travel with your stubs.

### [v0.10.0](https://github.com/qorpe/mockifyr/releases/tag/v0.10.0) — 2026-07-26

Message mocking: email + SMS capture channels.

### [v0.9.2](https://github.com/qorpe/mockifyr/releases/tag/v0.9.2) — 2026-07-21

Mockifyr brand identity.

### [v0.9.1](https://github.com/qorpe/mockifyr/releases/tag/v0.9.1) — 2026-07-20

Proxy-to-localhost fix for containers.

### [v0.9.0](https://github.com/qorpe/mockifyr/releases/tag/v0.9.0) — 2026-07-20

Outbound certificate trust in the dashboard.

### [v0.8.1](https://github.com/qorpe/mockifyr/releases/tag/v0.8.1) — 2026-07-20

Callback delivery fixes (container localhost, TLS).

### [v0.8.0](https://github.com/qorpe/mockifyr/releases/tag/v0.8.0) — 2026-07-20

Environments + Faker helpers.

### [v0.7.0](https://github.com/qorpe/mockifyr/releases/tag/v0.7.0) — 2026-07-14

Environments, Git credentials, and journal quality-of-life.

### [v0.6.0](https://github.com/qorpe/mockifyr/releases/tag/v0.6.0) — 2026-07-13

Callback deliveries in the request journal.

### [v0.5.0](https://github.com/qorpe/mockifyr/releases/tag/v0.5.0) — 2026-07-10

Connect Git sync from the dashboard.

### [v0.4.0](https://github.com/qorpe/mockifyr/releases/tag/v0.4.0) — 2026-07-10

Git-synced stub sets + v3 import parity fixes.

### [v0.3.0](https://github.com/qorpe/mockifyr/releases/tag/v0.3.0) — 2026-07-10

Stubs workspace UX overhaul + matcher fix.

### [v0.2.2](https://github.com/qorpe/mockifyr/releases/tag/v0.2.2) — 2026-07-09

one-line, OS-agnostic Docker run.

### [v0.2.1](https://github.com/qorpe/mockifyr/releases/tag/v0.2.1) — 2026-07-09

templated jsonBody responses + Docker docs.

### [v0.2.0](https://github.com/qorpe/mockifyr/releases/tag/v0.2.0) — 2026-07-09

Stubs redesign + journal detail + templating docs.

### [v0.1.3](https://github.com/qorpe/mockifyr/releases/tag/v0.1.3) — 2026-07-08

dashboard stub create/edit fix.

### [v0.1.1](https://github.com/qorpe/mockifyr/releases/tag/v0.1.1) — 2026-07-08

multi-arch container image.
