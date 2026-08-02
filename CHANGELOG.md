# Changelog

Every released version, newest first. Each entry links to the full release notes, which carry the
detail; anything that changed a default or broke a documented behavior is called out here.

This file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Mockifyr follows
semantic versioning as described in [VERSIONING.md](VERSIONING.md).

## Unreleased

## Released

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
