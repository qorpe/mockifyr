# Deployment posture

Operational maturity notes, tracked by #253. Nothing here changes engine behavior; it changes what a
platform team has to figure out for themselves.

## Non-root container + HEALTHCHECK (#241)

The runtime image runs as **UID 1001 with GID 0** and declares a `HEALTHCHECK`.

- **Why GID 0 rather than a private group:** OpenShift's restricted SCC assigns an *arbitrary* UID at
  admission and always puts it in the root group. `/app` and `/work` are therefore owned by `1001:0`
  and made group-writable (`chmod g=u`), which is the one ownership shape that works under both
  `docker run` (fixed UID 1001) and OpenShift (random UID, GID 0) with no startup chown.
- **The health check runs the app, not curl.** The aspnet runtime image ships no curl or wget, and
  adding one to satisfy a health check would enlarge the attack surface for nothing. `--healthcheck`
  is a one-shot mode in `Program` that probes the host's own readiness endpoint and exits 0/1.
- **Verified on a real image, not asserted:** `docker inspect` reports `1001:0` and the health check;
  the container reaches `healthy`; a stub POST writes into `/work/mappings` as `mockifyr:root`; and
  the same image started with `--user 1000670000:0` (an OpenShift-style random UID) serves and
  writes successfully.

## Liveness / readiness split (#242)

`/__admin/live` and `/__admin/ready` join `/__admin/health`, and all three stay outside admin auth —
an orchestrator cannot attach credentials, and a 401 on liveness restart-loops the pod (#218).

The distinction is behavioural, not cosmetic:

- **Liveness** answers only "the process is up". It performs no dependency checks, so a slow
  datastore can never get a healthy pod killed.
- **Readiness** turns on after startup mappings, environments and API keys are loaded, and turns
  **off** when `ApplicationStopping` fires. A rolling update therefore takes the pod out of rotation
  *before* it finishes in-flight work, instead of dropping requests.
- `/__admin/health` keeps its shape for humans and the dashboard.

Pinned by `ProbeEndpointTests`: a started host is alive and ready; a draining host fails readiness
while liveness still answers **and serving still works**; and all probes stay open under both
`--admin-user` and `--tenant-credential`.

## Helm chart and manifests (#243)

`deploy/helm/mockifyr` ships an opinionated chart: Deployment, Service, optional PVC, Ingress and
OpenShift Route, with Secrets for admin credentials and crypto keys.

Decisions worth remembering:

- **Credentials and keys never appear in `args` as literals** — they are injected as environment
  variables from a Secret and referenced as `$(VAR)`, so they do not show up in a pod spec dump or a
  process listing.
- **A generated admin password survives `helm upgrade`.** Regenerating it on every upgrade would
  silently lock out every client that stored the old one, so the template reads the existing Secret
  first (`lookup`) and only generates when there is nothing to keep.
- **`ingress.enabled` with `adminAuth.enabled=false` fails the render** with an explicit message.
  Publishing an unauthenticated admin API to the internet is never the intent, and a chart is the
  right place to make that impossible rather than merely discouraged.
- **The chart's promises are verified in CI.** `deploy/helm/verify-chart.py` renders the chart and
  asserts the posture (non-root, dropped capabilities, probe paths, secret-only credentials, bounded
  journal, the Ingress guard, crypto flags only when keys exist, every optional resource rendering).
  Configuration cannot be unit-tested like the engine, but a template edit that weakens a default now
  fails the build. `helm lint` and `kubeconform` schema validation run alongside it.

**Deferred:** a NetworkPolicy example, PodDisruptionBudget and HPA guidance, and a
ServiceMonitor — the last one waits for the metrics endpoint (#246), since there is nothing to scrape
until then.


## Supply-chain evidence (#244, #245)

**What ships with a release.** The release workflow now produces the three artifacts an enterprise
review asks for, alongside the image itself:

- **SBOM** — a CycloneDX document generated from the published image, attached to the GitHub release
  as a file *and* attested to the image with cosign. Procurement usually wants an artifact it can
  archive, not only something attached to a registry manifest, so both exist.
- **Signature and provenance** — keyless cosign signing (Sigstore) plus `provenance: mode=max` from
  buildx. The signature is bound to this repository and workflow rather than to a private key we
  would otherwise have to store, rotate and protect.
- **A scan of what was actually published** — Trivy runs against the pushed digest, so a HIGH/CRITICAL
  finding is visible in the release run rather than discovered later by a customer's scanner. It
  reports without blocking: a tagged release that is already built and pushed should not be left in a
  half-published state by a scan result.

**What runs on every pull request.** A `Security scans` job audits NuGet (including transitive
packages) and the dashboard's npm tree, then builds the image and scans it with Trivy — this one
**fails** the build, because a PR is exactly where a vulnerable dependency should be stopped.
`ignore-unfixed` is on: a base-image CVE with no available fix cannot be actioned in a PR, and
failing on it would only teach the team to ignore the job. CodeQL runs on both C# and TypeScript, and
`dependabot.yml` groups routine updates weekly (one PR per ecosystem) while security advisories still
arrive on their own.

**A real finding, fixed rather than suppressed.** Enabling the audit immediately surfaced two HIGH
advisories in the dashboard tree: `postcss` (path traversal, via vite) and `react-router`. The postcss
one was a lockfile refresh. The router one had no patched release on the `react-router-dom` line at
all — the framework moved its entry point to the `react-router` package at v8 — so the dashboard was
migrated to `react-router` 8 (an import rename; `react-router-dom` re-exported the same API) and the
old package removed. Verified in the browser afterwards, because a routing library swap is exactly
the change that type-checks and builds while breaking navigation: routes render, sidebar links
navigate, and the tree/badges are intact.


## Observability (#246)

**What ships.** Three switches, all off by default — a mock on a laptop should not open a metrics
port or ship spans anywhere:

| Flag | Effect |
|---|---|
| `--metrics` | Prometheus scrape endpoint at `/__admin/metrics` |
| `--otel-endpoint <url>` | OTLP exporter for traces **and** metrics (collector, gRPC) |
| `--log-json` | JSON console logs with scopes, for a log pipeline or SIEM |

**Decisions worth remembering.**

- **Metrics come from the `IServeEventListener` seam, not from inside the engine.** Every serve event
  already flows through that choke point (the journal and webhooks use it), so nothing can be served
  without being counted — and `Mockifyr.Core` keeps its zero dependencies. Instrumenting the engine
  directly would have put a metrics library behind the purity rule.
- **The scrape endpoint rides on the existing port** rather than opening a second listener: one port
  to expose, one Service, one probe surface. It also stays **outside admin auth**, for the same reason
  the probes do (#242) — a Prometheus scraper cannot carry credentials, and what it reads are counts
  and latencies, never payloads.
- **Cardinality is a design decision, not an accident.** Labels are `tenant` (bounded — an operator
  names them), `matched` (boolean) and `method` (closed set). Stub id and URL are deliberately *not*
  labels: a mock host can hold thousands of stubs, and a metrics backend would fall over. The wire
  test asserts their absence, so a well-meaning future addition fails the suite.
- **Probes and the scrape endpoint are filtered out of tracing**, or they would dominate the span
  volume with data nobody reads.
- **Instrument names are contract.** `mockifyr.requests.served` and `mockifyr.response.status` are
  referenced by dashboards and alert rules; renaming them is a breaking change, which is why they live
  in one place with a comment saying so.
- **The Prometheus ASP.NET exporter is still pre-release upstream.** It is pinned and confined to
  `Mockifyr.Server`, so nothing in the engine or the facades depends on a beta package.

**Validation story.** `ObservabilityTests` (3 wire tests): metrics exposed with the intended labels
after a match and a miss — **and the cardinality-exploding labels asserted absent**; the scrape
endpoint reachable without credentials on a host where every other admin route answers 401; and a
host without the flag exposing no scrape output at all while serving normally. The chart gained a
`ServiceMonitor` (refused unless `metrics.enabled`, asserted in the posture verifier) and the flags
are wired through values.

**Deferred.** Spans for the individual engine phases (matching, templating, crypto) — the ASP.NET and
HttpClient spans already cover request→response and outbound calls, and per-phase spans are worth
adding once someone needs them rather than on speculation.

## Edge hardening for an externally reachable host (#349)

Three things a host on the public internet needs that a host on a laptop does not. All three were
verified absent before being built, and all three are **off by default** — an unconfigured host
behaves exactly as it always has, which is what makes them safe to ship into 1.x.

### The hosts this instance may call — `--allow-outbound-host`

The second half of "the sandbox cannot be used as a way into the network it runs in"; the first was
the partner principal (#346), which stops a partner from *configuring* outbound reach. This stops the
host from *making* the call, whoever configured it. Enforced at the webhook and proxy edges.

- **Checked against the rendered URL, not the template.** A webhook URL may be
  `{{request.headers.X-Callback}}`; a policy that inspected the text rather than the target would
  allow exactly the case it exists to stop.
- **A wildcard covers subdomains, not the apex.** Somebody allowing `*.internal.example` almost never
  means `internal.example` itself, and for a control like this the permissive guess is the wrong one.
- **A portless entry allows any port on that host.** Making an operator enumerate ports produces
  allowlists that block something legitimate, which is how a control comes to be switched off.
- **An unparseable target is refused** once a restriction is in force: "we could not tell, so we
  allowed it" is the failure an allowlist exists to remove.
- **Refusals are visible.** A webhook refusal is appended to the serve event beside the request that
  caused it, the way a failed delivery already is. A proxy refusal needed a fix on the way: it was
  thrown where only container-diagnosis failures were caught, so it would have propagated as an opaque
  500 — the one proxy outcome the host can explain completely turned into the one that explains
  nothing. It now answers **502** naming the host and the allowlist.
- **Scope, stated rather than skipped.** `publish` (ADR 0013) names a *topic* on a broker the host was
  started with, so a stub cannot choose an outbound host there and there is nothing for an allowlist to
  decide. The issue listed it; this is why it is not enforced.

### How large a body this host reads — `--max-request-body-bytes`

Kestrel's ~30 MB default applied to every caller equally. The host value is a **ceiling**: a
`--tenant-max-request-body` above it is clamped, or the one number an operator sets to bound the
machine could be raised by configuration written later.

Two stops, because one is not enough. The explanatory one reads `Content-Length` and answers **413**
naming *which* limit was hit — a tenant held below the ceiling and the ceiling itself are different
problems with different fixes. The hard one sets Kestrel's per-request limit, which is all that stands
behind a chunked body declaring no length; it refuses without our message, and a bare refusal beats no
refusal. An entry naming a non-positive size is dropped rather than read as zero, since a limit of
zero refuses every request with a body and looks like the host being broken rather than misconfigured.

The tenant is resolved key-first, then header, then default — the same order the serving facade uses
(ADR 0003). A limit that applied to a different tenant than the request did would be worse than none.

### Which browsers may call — `--allow-origin`

The first wall anybody hits integrating a web front end, and it looks like our bug. Off by default,
because turning it on for everyone would hand every browser on the internet a credentialed path into
somebody's tenant.

- **The origin is echoed, never `*`** — `*` is incompatible with credentials, and a sandbox key
  travels as one. `Vary: Origin` goes with it, or a shared cache serves one origin's response to
  another.
- **A disallowed origin gets no headers, not a refusal.** The browser is what enforces CORS; answering
  403 would break every non-browser client for a rule that does not apply to them.
- **Preflight is answered here or not at all** — the serving catch-all would 404 an `OPTIONS`, and a
  404 preflight is indistinguishable from "CORS is broken" to the developer on the other side.
- **A tenant's own list replaces the host-wide one** rather than adding to it: a tenant naming its
  origins is stating the whole set.
- **`/__admin` is excluded and `/__sandbox` is not.** An operator's browser reaches the admin API from
  the dashboard that served it; a partner's browser needs the surface that answers "did my OTP arrive"
  (#347), and leaving it out would reopen that gap at the edge.
- **The separator for a tenant entry is `=`, not `:`** — every origin contains a colon, and a rule that
  has to explain which colon it means is a rule people get wrong.

**Validation.** `OutboundHostPolicyTests` (27), `RequestBodyLimitTests` (17), `CorsOriginTests` (21)
over the pure decisions; `OutboundAllowlistTests`, `RequestBodyLimitWireTests` and `CorsWireTests`
(19 wire cases) over the edges, each paired with a class that runs the *unconfigured* host and asserts
nothing changed.

**Stryker 95.77 %** across the three new files, from 77.46 %. What the first 18 points bought:

- `IsConfigured` on both the CORS and body-limit policies was untested with only *one* of its two
  sources set — and that flag is what decides whether the middleware is installed at all, so the
  mutant was "a limit nobody enforces".
- The tenant/host boundary in a body-limit refusal: at *exactly* the ceiling the tenant's own number is
  the one in force, and saying otherwise sends an operator to change a setting that would not have
  helped.
- Port 65535 — the top of the valid range — was silently dropped by an off-by-one nobody would notice
  until a callback stopped arriving.
- A junk entry still appearing in what the host *reports* (`Entries` backs the startup line), which is
  the "looks configured, is not" failure in its purest form.

One test had to be corrected rather than added. It asserted that `partner.example:0` would fall back to
matching any port on that host. It does not: the unusable port stays part of the host name, no URI host
contains a colon, and the entry therefore matches nothing. That is the right direction to fail in — a
typo that grants *more* than was written is the one an allowlist can never reveal from outside, while a
typo that grants less announces itself in the journal and in the startup line. The test now says so.

Three survivors remain, each analysed as an equivalent mutant:

- Removing the `text.Length == 0` early return in entry parsing: the same case is caught by the
  `text.Length == 0 ? null` at the end of the method, so both paths still drop the entry.
- `colon > 0` → `colon >= 0`: a leading colon then parses a port and leaves an empty host, which the
  final guard drops — the entry is discarded either way.
- `parsed is > 0` → `>= 0`: port 0 becomes a pinned port, and no URI ever reports port 0, so the entry
  matches nothing either way.
