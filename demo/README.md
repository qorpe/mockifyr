# Mockifyr live-demo kit

A rehearsed, self-contained demo of Mockifyr as an enterprise API mock + integration sandbox
platform. One command brings everything up; the whole demo then runs from a single browser page.

## Quick start

```bash
./demo/start.sh      # starts engine (:8080) + upstream (:9090) + runner (:7788), seeds, opens the browser
```

Then:

- **Demo screen** → http://localhost:7788 — every step has a ▶ button; the command runs on the
  machine and its output renders inline. (KOPYALA copies the command instead, if you prefer a terminal.)
- **Dashboard** → http://localhost:8080/__mockifyr — keep it in a second tab, tenant **Acme Pay**.
  First check: the tenant list must contain **Globex** (add it via "Tenant name…" → `Globex` if missing).

Stop everything:

```bash
./demo/stop.sh
```

Reset to a clean stage at any time (safe, ~10 s): `./demo/seed.sh` — note the OpenAPI import is
deliberately NOT part of the seed; it is the demo's opening move (Act 1).

## First time on a fresh clone

1. .NET 10 SDK (pinned in `global.json`) and Docker are NOT needed for the demo itself; Docker is
   only for the differential test suite.
2. Build the dashboard once: `pnpm --dir ui install && pnpm --dir ui build:embedded`
3. Install the demo's only node dep (WebSocket client): `cd demo && pnpm install`
4. Optional but recommended: `brew install grpcurl jq` (jq is required by the scripts,
   grpcurl by the gRPC beat).
5. `./demo/start.sh`

Pre-show green check (runs all 33 beats against a fresh seed, ~2 min):

```bash
./demo/demo.sh rehearse && ./demo/seed.sh
```

## What's in here

| File | Purpose |
|---|---|
| `start.sh` / `stop.sh` | one-command bring-up / tear-down (works on a fresh clone: builds ui, installs deps) |
| `run-server.sh` | main host: :8080 + TLS :8443, dashboard, SMS profile, SMTP :2525, sandbox auth, audit |
| `run-upstream.sh` | second Mockifyr on :9090 — plays the "real API" in the recording act |
| `seed.sh` | resets + seeds the stage (8 stubs, resources, WS mappings, API key) |
| `demo.sh <step>` | every demo beat as a named step; no args = list; `rehearse` = run all |
| `runner.py` | localhost:7788; serves the demo screen + docs, executes whitelisted steps, ■ STOP endpoint |
| `demo-live.html` | THE demo screen — run buttons, inline output, DETAY panels, deep-dive links, STOP |
| `doc-viewer.html` + `docs/*.md` | English deep-dives per concept (sandbox, matching, callbacks, messages, protocols, scenarios, recording, time-chaos-contract, auth) — served at `/doc?d=<name>` |
| `oidc/` | SSO act: `start-keycloak.sh` (Docker Keycloak + imported realm) + `run-server-oidc.sh` (engine with `--oidc-*`); user demo/demo123, tenant claim → acme-pay |
| `compose/` | **Docker-only path**: pinned release image + Postgres persistence + Basic auth (admin/demo123) + upstream + a containerized runner with every tool baked in — `docker compose up --build` and the FULL demo runs at http://localhost:7789 with zero host installs (no .NET, node, jq, grpcurl; only git+Docker). Dashboard: localhost:8090/__mockifyr. All 33 rehearse beats verified green in-container (e-mail beat auto-skips: the SMTP listener is loopback-only — product issue filed). |
| `DEMO.md` | full runbook: cheat sheet, act-by-act notes, cut order, failure recovery |
| `docs/konusma-notlari.tr.md` | speaker notes per beat (Turkish) |
| `deck.html` / `Mockifyr-Demo.pptx` | 15-slide intro/closing deck (browser / PowerPoint) |
| `specs/payments.yaml` | the OpenAPI document imported live in Act 1 |
| `grpc/greeter.{proto,dsc}` | gRPC descriptor for the hot-upload beat |
| `send-email.py` / `ws-client.mjs` | real SMTP / WebSocket demo clients |
| `work/`, `upstream/` | the two hosts' data directories (safe to ignore) |

## The optional SSO act (verified end to end)

```bash
./demo/start.sh sso    # ONE command: Keycloak (Docker, realm auto-imported) + the OIDC engine
```

Two operational notes: (1) if your browser ever shows stale "Sample data" against a running
host, open the dashboard via **http://127.0.0.1:8080/__mockifyr/** — a fresh origin with no
cached bundle or stored state (the engine now also serves the SPA shell with
`Cache-Control: no-cache`, so this class of problem is fixed going forward); (2) if you
recreate the Keycloak container, restart the engine too — a new container means new token
signing keys, and the engine caches the old ones (symptom: fresh tokens get 401).

In SSO mode the **whole flow still works** — all 33 rehearse beats verified green with the
admin surface locked. `demo.sh`/`seed.sh` detect the mode (the `demo/.sso` marker) and fetch
bearer tokens per run (password grant, as any CI would), attaching them to `/__admin` calls
only. Tenant locking is real, so there are two identities: `demo`/`demo123` manages
**acme-pay**, `globex`/`globex123` manages **globex** — the recording act runs under the
second one. Back to the normal demo: `./demo/stop.sh && ./demo/start.sh`.

Open the dashboard: the login switches to **"Sign in with your identity provider"** →
Keycloak page → `demo` / `demo123` → redirected back, signed in. Without a token the admin
API answers **401**; the token's `tenant` claim locks the user to **acme-pay** (other
tenants 403 → the dashboard falls back to sample data). Basic auth keeps working next to
OIDC for machine accounts. Deep dive: `docs/auth.md`.

## The flow in one breath

Act 1 spec→sandbox (import, CRUD, keys, honest 429) · Act 2 matching + near-miss diagnostics ·
Act 3 webhook callback with journal proof · Act 4 SMS/e-mail/OTP inbox · Act 5 gRPC/GraphQL/WS ·
Act 6 scenario state machine · Act 7 record the "real" API into stubs (+ optional drift check) ·
Act 8 (optional) tenant clock, seeded chaos, contract conformance.

Two rules learned the hard way: switch the tenant BEFORE starting a recording, and if you use
`record-verify`, always run it BEFORE `record-stop`.
