<picture>
  <source media="(prefers-color-scheme: dark)" srcset="brand/mark/mockifyr-mark-white.svg">
  <img src="brand/mark/mockifyr-mark-black.svg" alt="" width="148">
</picture>

# Mockifyr

[![CI](https://github.com/qorpe/mockifyr/actions/workflows/ci.yml/badge.svg)](https://github.com/qorpe/mockifyr/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/qorpe/mockifyr?sort=semver)](https://github.com/qorpe/mockifyr/releases)
[![Image](https://img.shields.io/badge/ghcr.io-mockifyr-2496ED?logo=docker&logoColor=white)](https://github.com/qorpe/mockifyr/pkgs/container/mockifyr)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

**Enterprise API mocking and integration sandbox platform — self-hosted, multi-protocol, one container.**

Mock any API your systems depend on (HTTP · gRPC · GraphQL · WebSocket · email/SMS · Kafka/AMQP) and hand your
partners a sandbox: per-tenant test data, API keys with quotas, and stateful CRUD generated straight
from an OpenAPI spec. Under the hood: a transport-agnostic request-matching and response engine with
first-class multi-tenancy, pluggable persistence, and thin facades — in-process library · HTTP
server · admin REST — plus message mocking with a tenant-scoped inbox and one-call OTP verification,
and a broker channel where a stub can answer *and* emit an event, or reply to one with another.
Clean-room codebase with its own IP and no third-party mock-engine dependencies.

📖 **[Documentation → mockifyr.qorpe.com](https://mockifyr.qorpe.com)** — guides, the full CLI
and admin API reference, and [known limitations](https://mockifyr.qorpe.com/limitations/).

## Quick start

### Docker — one image (engine + admin API + dashboard)

Just run it — in-memory, zero config, **the same one line on macOS, Linux and Windows**:

```bash
docker run -p 8080:8080 ghcr.io/qorpe/mockifyr
```

- Mock surface — `http://localhost:8080`
- Admin API — `http://localhost:8080/__admin`
- Dashboard — `http://localhost:8080/__mockifyr`

Create stubs in the dashboard, or import a mapping JSON bundle. Runs on `linux/amd64` and `linux/arm64`
(Apple Silicon included).

**Keep your data across restarts** — `docker compose up`, or a named volume (both identical on every OS):

```bash
docker compose up                                # stubs live in ./mappings, next to you
docker run -p 8080:8080 -v mockifyr-data:/work ghcr.io/qorpe/mockifyr   # named volume
```

Mount **`/work`**, not just `/work/mappings` — the file store also keeps environment configuration
(`/work/environments`), response body files (`/work/__files`) and gRPC descriptors (`/work/grpc`)
there, and a mappings-only mount silently loses those when the container is recreated.

**Preload / edit stub files on your host** (advanced) — bind-mount a folder of mapping `*.json`. Only
the path syntax differs per shell; nothing else changes:

```bash
docker run -p 8080:8080 -v "$PWD/mappings:/work/mappings" ghcr.io/qorpe/mockifyr   # macOS / Linux
#   PowerShell:  -v "${PWD}/mappings:/work/mappings"       CMD:  -v "%cd%/mappings:/work/mappings"
```

Files load into the **default tenant**; for a named tenant (e.g. `maestro`) use the dashboard **Import**,
or POST to `/__admin/mappings/import` with an `X-Mockifyr-Tenant` header. Durable datastores:

```bash
docker compose -f docker-compose.postgres.yml up    # PostgreSQL persistence
docker compose -f docker-compose.redis.yml up       # Redis persistence
```

### .NET Aspire

Aspire recreates containers on every app-host run, so without a volume the file store — stubs *and*
environment configuration — resets each time. Mount a named volume at `/work` (and optionally keep
the container alive between runs):

```csharp
var mockifyr = builder.AddContainer("mockifyr", "ghcr.io/qorpe/mockifyr")
    .WithHttpEndpoint(port: 8080, targetPort: 8080)
    .WithVolume("mockifyr-data", "/work")            // survives restarts and recreation
    .WithLifetime(ContainerLifetime.Persistent);     // optional: reuse the container across runs
```

Or point it at a durable datastore instead: `.WithArgs("--postgres", connectionString)`.

### Local (.NET 10 SDK)

```bash
dotnet run --project src/Mockifyr.Server -- --port 8080 --root-dir .   # stubs load from ./mappings
```

### Engine only (no dashboard)

The dashboard is opt-in via `--dashboard`; omit it to serve just the mock surface + admin API.

```bash
# Local
dotnet run --project src/Mockifyr.Server -- --port 8080 --root-dir .   # stubs load from ./mappings

# From the image (override the entrypoint to drop the built-in --dashboard)
docker run -p 8080:8080 -v "$PWD/mappings:/work/mappings" --entrypoint dotnet \
  ghcr.io/qorpe/mockifyr:latest Mockifyr.Server.dll --port 8080 --root-dir /work
```

Or embed the engine directly in-process with `Mockifyr.Facade.Library` — no HTTP at all. It is not
published to NuGet yet; reference the project from a checkout of this repository.

## Configuration

Everything is a CLI flag — there is no config file. Because the host builds its configuration with the
standard .NET builder, **every flag is also readable as an environment variable of the same name**,
which is why `-e admin-user=alice` works on `docker run`; arguments win when both are present.

The common flags, with the [full reference](https://mockifyr.qorpe.com/cli/) on the docs site:

| Flag | Effect |
|------|--------|
| `--port <n>` | mock-serving HTTP port (default 8080) |
| `--https-port <n>` | enable HTTPS / HTTP2 |
| `--root-dir <dir>` | load and persist stubs as JSON files |
| `--smtp-port <n>` | capture real SMTP mail into the tenant-scoped message inbox (`/__admin/messages`); the AUTH username names the tenant |
| `--sms-profile twilio` | emulate Twilio's send-message API: realistic responses the official SDK accepts, every SMS captured into the message inbox |
| `--message-limit <n>` | per-tenant message inbox bound (default 1000, oldest evicted first) |
| `--kafka-bootstrap <servers>` | connect to Kafka, so stubs can publish events and broker mappings can serve them |
| `--kafka-subscribe <topics>` | comma-separated topics to capture into the message inbox and match broker mappings against |
| `--kafka-group <id>` | consumer group for capture (default `mockifyr`) — two replicas share a subscription |
| `--amqp-uri <uri>` | the same, over AMQP / RabbitMQ |
| `--amqp-subscribe <queues>` | comma-separated queues to consume |
| `--api-key-prefix <marker>` | the marker new sandbox tokens start with (default `mfk_`; 1–12 characters of letters, digits, `-` or `_`). Only newly issued tokens change — verification never inspects the marker, so keys already in a partner's hands keep working |
| `--dashboard-path <prefix>` | where the dashboard is mounted (default `/__mockifyr`). One leading-slash segment; `/__admin` and `/__sandbox` are refused. The served shell's asset URLs are rewritten to match, so the same build serves from any prefix |
| `--brand-name <name>` · `--brand-subtitle <text>` | what the dashboard calls itself — the sidebar, the browser tab, the status line and `/__admin/health`. Unset keeps the product's own |
| `--brand-logo <path>` | an image file served in place of the built-in mark; a missing file is refused at startup |
| `--support-url <url>` | where the dashboard's "report an issue" item points (absolute http/https only) |
| `--telemetry-name <name>` | the OpenTelemetry service name, and the instrument prefix (its lowercase form). Default `Mockifyr` → `mockifyr.*`, so existing dashboards and alert rules are untouched |
| `--tenant-header <name>` | the header a request names its tenant in (default `X-Mockifyr-Tenant`). Read by every transport — HTTP, admin, gRPC, WebSocket, broker mappings, the SMS profile — and by the dashboard, which learns it from the host rather than assuming it. A malformed name is refused at startup |
| `--tenant-credential <tenant>:<user>:<pass>` | repeatable — an admin credential scoped to ONE tenant; it cannot address another by renaming `X-Mockifyr-Tenant` (403). `--admin-user` stays the system scope |
| `--partner-credential <tenant>:<user>:<pass>` | repeatable — as above, plus refused on every route and every stub field through which the host would act on the network (recordings, outbound trust, Git; `proxyBaseUrl`, post-serve actions) |
| `--allow-outbound-host <host\|host:port\|*.domain>` | repeatable — restrict the hosts this instance may call (webhooks, proxy stubs). Unrestricted by default; a refusal is journaled |
| `--max-request-body-bytes <n>` | host-wide ceiling on request bodies; larger is refused with **413** naming the limit |
| `--tenant-max-request-body <tenant>:<bytes>` | repeatable — hold one tenant below the ceiling (never above it) |
| `--allow-origin <origin>` | repeatable — browser origins allowed to call the mock and `/__sandbox`. Off by default; the admin API stays same-origin |
| `--tenant-allow-origin <tenant>=<origin>` | repeatable — a tenant's own origin list, replacing the host-wide one |
| `--block-outbound-routes` | while the admin API is unauthenticated, refuse the routes that act on the network (start recording, outbound trust, Git) with **403** — an open host cannot be turned into a forward proxy |
| `--decrypt-key <base64>` | 256-bit key enabling payload cryptography: a stub's `"decrypt"` block makes encrypted request fields matchable/templatable (the journal keeps the ciphertext), and its `"protect"` block encrypts named response fields — or the whole body — on the way out |
| `--metrics` | expose Prometheus metrics at `/__admin/metrics` (no credentials needed — a scraper cannot carry them) |
| `--otel-endpoint <url>` | export traces and metrics via OTLP to a collector |
| `--log-json` | structured JSON logs for a log pipeline or SIEM |
| `--audit` | record every administrative change at `/__admin/audit` — principal, tenant, action, target, outcome — and emit each as an `admin.audit` log line |
| `--audit-limit <n>` | per-tenant audit-trail bound (default 1000, oldest evicted first; `<=0` = unbounded) |
| `--sign-key <base64>` | 256-bit secret enabling signing: a stub's `"signature"` block requires a validly signed request (unsigned → non-match), and its `"sign"` block adds `Digest` + HMAC headers to the response |
| `--decrypt-key-file <path>` · `--sign-key-file <path>` | read keys from a file instead of the command line — one key per line, newest first, optionally `id: base64`. Re-read on change, so **rotation needs no restart**: new tokens use the newest key while every key in the file still decrypts and verifies |
| `--key-reload-seconds <n>` | how often a key file is re-read (default 10) |
| `--admin-pass-file <path>` | read the admin password from a file, keeping it out of the process listing |
| `--oidc-authority <url>` | authenticate the admin API with OIDC bearer tokens; keys come from the issuer's discovery document. Works alongside `--admin-user`, so people can use SSO while machines keep a credential |
| `--oidc-audience <aud>` · `--oidc-client-id <id>` | the audience tokens must carry, and the public client the dashboard signs in with (authorization code + PKCE) |
| `--oidc-tenant-claim <claim>` | the claim naming the tenant an identity may address — a principal scoped to one tenant gets **403** on another. No claim means system scope |
| `--oidc-required-role <role>` · `--oidc-role-claim <claim>` | require a role on the token (default claim `roles`) |
| `--mask-headers <names>` | keep named header values out of the journal entirely (comma-separated, case-insensitive) — e.g. `Authorization,Cookie,X-Api-Key` |
| `--mask-body-fields <names>` | keep named JSON body fields out of the journal (any depth, arrays included) — e.g. `pan,cvv,password` |
| `--journal-limit <n>` | per-tenant request-journal bound (default 1000, oldest evicted first; `<=0` = unbounded). `--max-request-journal-entries` is a kept alias |
| `--journal-disabled` | record nothing in the request journal (load tests); `--no-request-journal` is a kept alias |
| `--resource-limit <n>` | per-collection sandbox document bound (default 1000, oldest evicted first) |
| `--resource-max-body <bytes>` | per-document body cap for `/__admin/resources` (default 1 MiB; 413 beyond it) |
| `--sandbox-auth` | sandbox API keys (`/__admin/apikeys`): `mfk_…` tokens select the tenant via `X-Api-Key`/Bearer, with optional per-key hourly quotas (`429` + rate headers), an optional expiry, a read-only scope, and rotation with an overlap. With `--redis` the quota is counted in Redis, so replicas share one budget and a restart does not refund it |
| `--tenant-storage-limit <bytes>` | per-tenant ceiling on sandbox document bytes (a declared tenant may carry its own); the refusal names the limit and the current usage |
| `--idempotency` · `--idempotency-window <seconds>` | replay the first response for a retried write carrying the same `Idempotency-Key` (default window 24h); a declared tenant may keep it off |
| `--env <key>=<value>` | a host-level environment value every tenant inherits unless it defines the same key (repeatable); shared constants like a base URL or a test IBAN |
| `--usage` | keep bounded per-key request counts (total, matched, unmatched, and each refusal) plus the most-called paths nothing models, readable at `/__admin/usage` and by a partner at `/__sandbox/usage` |
| `--rate-burst <n>/<seconds>` | a host-wide burst ceiling counted beside each key's hourly quota — applies to keys with no quota too; the binding limit is the one reported in the rate headers |
| `--dashboard <dir>` | serve the built dashboard under `/__mockifyr` |
| `--admin-user <u>` · `--admin-pass <p>` | require HTTP Basic auth on the admin API (`/__admin/*`); the dashboard shows a login screen. `/__admin/health` stays open so Kubernetes probes keep working |
| `--postgres <connstr>` · `--redis <connstr>` · `--litedb <path>` | durable persistence backend |
| `--change-feed` | keep multiple instances coherent |
| `--outbound-host-fallback false` | deliver callbacks and proxies to exactly the address written, never retrying via the host gateway |
| `--trust-proxy-target <host>` | trust that host's certificate on outbound calls (repeatable) |
| `--trust-all-proxy-targets` | trust every outbound certificate |
| `--global-response-templating` | render every response through the templating engine, regardless of the per-stub `transformers` list |
| `--git-remote <url>` · `--git-branch <name>` · `--git-work-dir <dir>` | Git sync (ADR 0007): keep the mappings directory in a repository. `--git-remote` requires `--root-dir`; the branch defaults to `main` |

The hot path is always in-memory; a durable backend is opt-in and writes through.

### Kubernetes / OpenShift

The image runs unprivileged (UID 1001, GID 0 — compatible with OpenShift's arbitrary-UID model) and
declares a container health check. Two probe endpoints sit outside admin auth: `/__admin/live`
(process liveness) and `/__admin/ready` (turns off while starting or draining, so a rolling update
drains cleanly).

A Helm chart lives in [`deploy/helm/mockifyr`](deploy/helm/mockifyr) — Deployment, Service, optional
PVC, Ingress and OpenShift Route, with credentials and crypto keys injected from Secrets:

```bash
helm install mockifyr deploy/helm/mockifyr --set persistence.enabled=true
```

### Backup and restore

`GET /__admin/backup` produces one archive of everything a tenant's operator authored — stubs,
environment keys, sandbox documents, API keys and scenario states — and `POST /__admin/restore`
puts it back, replacing what the archive covers. Settings → **Backup and restore** does the same
from the dashboard.

```bash
curl -s http://localhost:8080/__admin/backup > backup.json
curl -s -X POST http://localhost:8080/__admin/restore --data-binary @backup.json
```

The request journal and message inbox are deliberately absent — they record what happened, not what
was configured. The archive carries API key verifiers so consumers' keys keep working after a
restore, which makes it a secret: store it like a key file.

### Callbacks and proxies to your own machine

Running in Docker, `localhost` inside the container means *the container* — so a callback or proxy
aimed at `http://localhost:5004` cannot reach a service on your machine, even though the same URL
works from Postman. Mockifyr handles this: a loopback target whose connection is **refused** is
retried once via `host.docker.internal` (a callback records both attempts in the journal; a proxy that
still cannot be reached answers 502 with the reason). Targeting `host.docker.internal` yourself works
too, and `--outbound-host-fallback false` turns the retry off (`--webhook-host-fallback` is a kept alias).
On Linux, `host.docker.internal` only exists if the container is started with
`--add-host=host.docker.internal:host-gateway`.

### Callbacks and proxies to an internal HTTPS endpoint

An endpoint served by your organisation's internal CA is trusted by your machine's keychain but not
by the container, so an outbound call to it fails where Postman succeeds. The journal names the
reason (`RemoteCertificateChainErrors`, a name mismatch, an expiry). To allow it, trust that endpoint
by name — the same flag surface as the reference engine, applied to callbacks and proxying alike:

```bash
docker run … mockifyr --trust-proxy-target api.dev.mycorp.intra
```

Trusting one host grants nothing to any other. `--trust-all-proxy-targets` disables verification for
every target; the host prints a warning at startup when either flag is in effect. Without them,
certificates are verified normally.

You can also manage trusted hosts from **Settings → Outbound certificate trust** in the dashboard,
which takes effect on the next call with no restart and survives one. Passing a `--trust-*` flag
pins the configuration instead and the dashboard shows it read-only — the same two-mode design as
Git sync. `--trust-all-proxy-targets` stays flag-only: the dashboard can trust individual hosts but
cannot turn verification off. Full detail:
[HTTPS, HTTP/2 and mTLS](https://mockifyr.qorpe.com/https-and-mtls/).

## Documentation

**Using Mockifyr — [mockifyr.qorpe.com](https://mockifyr.qorpe.com)**

- [Getting started](https://mockifyr.qorpe.com/getting-started/) · [the dashboard](https://mockifyr.qorpe.com/the-dashboard/)
- Stubs — [request matching](https://mockifyr.qorpe.com/request-matching/) · [responses](https://mockifyr.qorpe.com/responses/) · [templating](https://mockifyr.qorpe.com/templating/)
- Behaviour — [scenarios](https://mockifyr.qorpe.com/scenarios/) · [delays and faults](https://mockifyr.qorpe.com/delays-and-faults/) · [proxying](https://mockifyr.qorpe.com/proxying/) · [record and playback](https://mockifyr.qorpe.com/record-and-playback/) · [webhooks](https://mockifyr.qorpe.com/webhooks/)
- Platform — [multi-tenancy](https://mockifyr.qorpe.com/multi-tenancy/) · [environments](https://mockifyr.qorpe.com/environments/) · [persistence](https://mockifyr.qorpe.com/persistence/) · [HTTPS and mTLS](https://mockifyr.qorpe.com/https-and-mtls/) · [deploying in production](https://mockifyr.qorpe.com/deploying-in-production/)
- Messages — [email & SMS mocking](https://mockifyr.qorpe.com/messages/) (SMTP capture, Twilio profile, OTP verify)
- Reference — [CLI](https://mockifyr.qorpe.com/cli/) · [admin API](https://mockifyr.qorpe.com/admin-api/) · [extending](https://mockifyr.qorpe.com/extending/)
- [Migration guide](https://mockifyr.qorpe.com/migration/), and the
  [known limitations](https://mockifyr.qorpe.com/limitations/) worth reading first

**Working on Mockifyr — in this repository**

- Architecture & design — [ARCHITECTURE.md](ARCHITECTURE.md)
- What an upgrade can change — [VERSIONING.md](VERSIONING.md) · release history — [CHANGELOG.md](CHANGELOG.md)
- How to contribute, and the bar a change must clear — [CONTRIBUTING.md](CONTRIBUTING.md) · where to ask — [SUPPORT.md](SUPPORT.md)
- Roadmap — [docs/roadmap.md](docs/roadmap.md) · decisions — [docs/decisions/](docs/decisions/)
- Learned reference-engine behaviour, per feature group — [docs/parity/](docs/parity/)
- Testing strategy (the binding test contract) — [docs/testing.md](docs/testing.md)
- Measured performance and sizing guidance — [docs/parity/performance.md](docs/parity/performance.md) · harnesses — [bench/](bench/)
- Brand assets and their usage rules — [brand/](brand/)
- This is an AI-driven repository; how work is done here — [CLAUDE.md](CLAUDE.md)

## Contributing

Contributions are welcome. Read [CLAUDE.md](CLAUDE.md) for the development workflow and conventions,
then open a PR against `main`. Builds must stay green — `dotnet build` and `dotnet test`, plus the
dashboard's `pnpm build`.

## License

Licensed under the **Apache License, Version 2.0** — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
