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
