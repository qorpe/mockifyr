# Mockifyr live demo — runbook

Audience: technical. Length: 30–40 min. Everything below was rehearsed end to end on 2026-08-06.

**The story.** One narrative carries the whole demo: *Acme Payments* is an enterprise payments
API. We spin up a sandbox for it from its OpenAPI contract, exercise every channel a real
integration touches (REST, callbacks, SMS/e-mail, gRPC, GraphQL, WebSocket), then show the
platform features that keep a mock **trustworthy**: scenarios, recording, drift detection,
tenant clock, deterministic chaos, and contract conformance. A second tenant, *Globex Retail*,
proves isolation and plays the record-and-mock storyline.

> Naming rule for all spoken/visible material: never name the reference engine on stage or in
> slides — say "the reference engine" / "mapping JSON format" (see CLAUDE.md §0a).

---

## Cheat sheet — the exact run order

🖱 = by hand in the dashboard · ⌨ = `./demo/demo.sh <step>` in the terminal

> ⚠️ **Before Act 1, every time:** the tenant switcher (bottom-left) must say **Acme Payments**.
> The #1 rehearsal mistake: importing the spec or reading the journal while the switcher is on
> *Default* — everything "disappears" because you're looking at the wrong tenant.

```text
Act 1  🖱 Stubs → New stub → OpenAPI → paste yaml → Import
       ⌨ payments-create → payments-get → payments-list
       🖱 show Resources
       🖱 Access → Issue key (partner-portal, quota 10) → show token, close
       ⌨ key-quota                       (uses the seeded key from demo/.demo-key;
                                          or: key-quota mfk_xxx with a live key, quota 5)
Act 2  🖱 open the order stub in Stubs
       ⌨ order-ok → order-bad → near-miss
       🖱 Request journal
Act 3  ⌨ webhook            🖱 journal detail → Callback tab
Act 4  ⌨ sms → otp → email  🖱 Messages
Act 5  ⌨ grpc-descriptor → grpc → graphql → graphql-messy → ws
       🖱 protocol chips in the Stubs tree
Act 6  ⌨ scenario           🖱 click a state pill in Scenarios
Act 7  🖱 Tenant → Globex
       🖱 Recordings: Target http://localhost:9090 → Start recording
       ⌨ record-drive
       🖱 Snapshot → Import all
       (optional, if it flows for you: ⌨ drift → record-verify — the drift-detection beat;
        otherwise cover it with one sentence: "with a session live you can also ask whether
        the real API has drifted from your stubs — it reports field by field")
       ⌨ record-stop
       🖱 Tenant → Acme Pay
Act 8  (optional as a whole — cover it from the closing slide with one sentence per feature)
       ⌨ token → clock-freeze → token → clock-reset
         ("per-tenant clock: freeze time, test the token that expires in an hour — no waiting")
       ⌨ chaos-on → chaos-probe → chaos-off
         ("degradation profiles: seeded latency/errors tenant-wide; same seed, same chaos")
       ⌨ verify-stubs → verify-traffic
         ("conformance: are my stubs inside the contract, did clients stay inside it too")
```

Speaking notes per beat: `demo/konusma-notlari.md`. Pre-show green check: `./demo/demo.sh rehearse`
(runs every beat against a fresh seed; afterwards run `./demo/seed.sh` once more so the real demo
starts clean).

---

## 0. Pre-flight (do this ~30 min before, once)

```bash
cd ~/Repositories/mockifyr
dotnet build Mockifyr.sln -c Release          # warm build so dotnet run starts fast
./demo/run-server.sh                          # terminal 1 — main host :8080 (+TLS :8443)
./demo/run-upstream.sh                        # terminal 2 — "real" upstream :9090
./demo/seed.sh                                # terminal 3 — resets + seeds everything
```

Then:
- Open **http://localhost:8080/__mockifyr/** — switch tenant (bottom-left) to **Acme Payments**.
- Keep terminal 3 for demo steps: every beat is `./demo/demo.sh <step>` (no args = list steps).
- Font size up in the terminal; keep `jq` output visible.

**Reset between runs:** re-run `./demo/seed.sh` (safe, resets everything), and re-do the
OpenAPI import (Act 1) — imports are in-memory state for non-default tenants.

Known rough edges (rehearsed, avoid live surprises):
- Boolean CLI flags need `=true` (`--sandbox-auth=true`) — already in run-server.sh.
- `--dashboard`/`--root-dir` want **absolute paths** — already in run-server.sh.
- With `--sandbox-auth=true` the `X-Api-Key` header is *reserved* for sandbox keys — the demo
  stub deliberately uses `X-Partner-Key`.
- Never point a recording at the same instance (self-proxy loop) — that's why upstream is :9090.
- Recording: run **verify before stop** (stop clears the captured exchanges).
- Near-miss returns the top-3; ties resolve by insertion order — don't recreate the order stub
  mid-demo before the near-miss beat.
- The browser cannot set WS handshake headers — the WS beat uses `node demo/ws-client.mjs`.

---

## Act 0 — Opening (slides, ~3 min)

Slides 1–4: the problem (shared/broken integration environments; silently drifting mocks that
manufacture confidence), what Mockifyr is (transport-agnostic engine, thin facades, first-class
multi-tenancy — scope is a compile-time argument, not a convention), and the demo map.

---

## Act 1 — Spec → sandbox in one minute (~5 min) · Dashboard + Stubs + Resources + Access

1. **Dashboard page** — point at the *Spin up a sandbox* quick-start (four steps).
2. **Stubs → New stub → OpenAPI channel** — paste `demo/specs/payments.yaml`
   (open it in an editor tab beforehand), leave **Stateful CRUD** on, **Import spec** → "5 imported".
   Stubs tree now shows `/payments` CRUD. *Talking point:* every operation became a stub; the
   resource-shaped pair became live CRUD backed by the sandbox document store.
3. ```bash
   ./demo/demo.sh payments-create   # 201 + Location, then follows it
   ./demo/demo.sh payments-get      # seeded PAY-1001
   ./demo/demo.sh payments-list     # seeded 3 + created 1
   ```
4. **Resources page** — the `payments` collection; open a document. *Data, not canned strings.*
5. **Access page** — *Issue key* (name `partner-portal`, quota 10) → one-time token reveal
   dialog. *Talking point:* only a salted hash is stored; this is the last time the token exists.
6. ```bash
   ./demo/demo.sh key-quota         # 5×200 with X-RateLimit-*, 6th → 429 + Retry-After
   ```
   *Talking point:* the key **is** the tenant — no header needed; invalid key is an honest 401,
   never a silent fall-through; honest 429 with rate headers.

## Act 2 — Matching anatomy + "why didn't it match?" (~4 min) · Stubs + Journal

1. **Stubs page** — open *Create order — partner key + body validation*: header matcher +
   `matchesJsonPath` body matcher (Form/JSON tabs), templated response.
2. ```bash
   ./demo/demo.sh order-ok          # 201, sku/qty echoed via templating
   ./demo/demo.sh order-bad         # wrong key → 404
   ./demo/demo.sh near-miss         # names the disagreeing attribute: headers['X-Partner-Key']
   ```
   *Talking point:* near-miss speaks the mapping JSON's own vocabulary — you can grep your own
   file for the string it printed. Diagnosis never changes the served 404 (byte-stable surface).
3. **Request journal page** — matched/unmatched chips; open the unmatched `/api/orders` row.

## Act 3 — Callbacks (~3 min) · Journal detail

```bash
./demo/demo.sh webhook
```
Stub answers **202 immediately**, then fires the callback (templated from `originalRequest`,
500 ms delay). The journal shows BOTH: the authorize request (detail → **Callback** tab:
delivered, response 200 captured) and the callback landing on the receiver stub.
*Mention (slide only):* the same post-serve seam publishes to Kafka (`publish` action) — the
sandbox answers the request *and* emits the event; validated against a real broker container.

## Act 4 — Messages: SMS + e-mail + OTP (~4 min) · Messages page

```bash
./demo/demo.sh sms      # provider-shaped send API answers like the real thing (sid, queued)
./demo/demo.sh otp      # /__admin/messages/otp → {"otp":"482913"} — E2E tests read this
./demo/demo.sh email    # SMTP capture; AUTH username = tenant
```
**Messages page** — one inbox, channel chips, the SMS row even shows an **OTP** chip.
*Talking point:* your app sends real mail/SMS at a real protocol; Mockifyr answers like the
provider and delivers nothing. Verify-by-API replaces "check the phone".

## Act 5 — Beyond HTTP: gRPC, GraphQL, WebSocket (~6 min) · Stubs tree chips

```bash
./demo/demo.sh grpc-descriptor   # upload .dsc → {"serving":true} hot, no restart; lists methods
./demo/demo.sh grpc              # grpcurl over HTTP/2+TLS, tenant via metadata → stub answers
./demo/demo.sh graphql           # matched on query+variables+operationName
./demo/demo.sh graphql-messy     # same query reordered/minified — AST-normalized, still matches
./demo/demo.sh ws                # connect greeting, ping→pong, broadcast — tenant on handshake
```
Show the **Stubs tree**: the same list carries HTTP, GraphQL, gRPC and WS entries with protocol
chips — one engine, one admin surface, per-channel wire adapters at the edge.

## Act 6 — Stateful flows: Scenarios (~2 min) · Scenarios page

```bash
./demo/demo.sh scenario          # first poll: pending → second: settled (stays settled)
```
**Scenarios page** — the `payment-PAY-1001` card; state pills are clickable; click **Started**
to rewind live, re-run the curl → `pending` again. **Reset all** button top-right.

## Act 7 — Record reality, then catch it drifting (~6 min) · Recordings page (tenant Globex Retail!)

Switch tenant to **Globex Retail** (empty stub list — isolation proven in passing).

1. **Recordings page** — Target base URL `http://localhost:9090`, **Start recording**
   (red pulsing pill). *Say:* the ":9090 upstream" plays Globex's real billing API.
2. ```bash
   ./demo/demo.sh record-drive     # two real calls answered BY the upstream, through the mock
   ```
3. **Snapshot** on the page → *Captured stubs 2* → **Import all** — they appear under Stubs.
   (CLI equivalent: `record-snapshot` + `record-import`.)
4. ```bash
   ./demo/demo.sh drift            # the "real" API changes shape behind everyone's back
   ./demo/demo.sh record-verify    # fieldUnexpected /currency, fieldMissing /settlementBatch
   ```
   *Talking point:* structural, never literal — ids/timestamps don't drown findings. This is the
   question no mock usually answers: **has reality moved since I recorded?**
5. ```bash
   ./demo/demo.sh record-stop
   ```
Switch back to **Acme Payments**.

## Act 8 — Time, chaos, and the contract (~5 min) · terminal + slides

```bash
./demo/demo.sh token         # issuedAt/expiresAt = real now
./demo/demo.sh clock-freeze  # tenant clock frozen at 2027-01-01
./demo/demo.sh token         # the token now "expires" in 2027 — no waiting an hour
./demo/demo.sh clock-reset
```
*Talking point:* per-tenant virtual time; journal/audit/inbox deliberately keep real time.

```bash
./demo/demo.sh chaos-on      # degradation profile: +300±200 ms, 40% 503, seed 42
./demo/demo.sh chaos-probe   # mixed 200/503, visibly slower — SAME sequence every seeded run
./demo/demo.sh chaos-off
```
*Talking point:* the profile composes with every stub; the seed makes a chaos run a
**regression test**; admin API is never degraded, so you can always turn it off.

```bash
./demo/demo.sh verify-stubs    # stubs vs contract: 5/5 covered + 8 undeclared (on purpose)
./demo/demo.sh verify-traffic  # what CLIENTS sent vs contract — the failure a permissive mock hides
```
*Talking point:* three conformance checks (stubs-vs-spec, upstream-vs-stubs, traffic-vs-spec)
share one engine and one set of ambiguity rules — two reports can't disagree about which
operation a path belongs to.

## Act 9 — Why you can trust it (slides, ~3 min)

- Differential testing against the **reference engine** running in Docker is the definition of
  done — never self-assessment. 1122 tests green across four suites.
- Where no oracle exists: real clients (MailKit, the official SMS SDK, the official Kafka
  client) and mutation testing (Stryker, 100% on the message logic).
- Measured, not claimed: template caching 699 µs → 1.21 µs; matching the last of 1000 stubs
  29.1 µs → 392 ns (semantics pinned by the differential suite).
- Honest failure modes everywhere: 429 with rate headers, 401 for a bad key, import warnings
  for unsupported fields, a public deferred-edge register.
- Enterprise posture: Helm chart w/ CI-asserted posture, non-root image, SBOM + keyless signing,
  OpenTelemetry + Prometheus, admin audit trail, OIDC sign-in, per-tenant credentials.

Close on the roadmap beat: the broker channel (answer the request *and* emit the event) is
shipping — the sandbox is growing from HTTP-shaped to event-shaped.

---

## If you're running late — cut order

1. Act 8 chaos block (keep clock — it's 20 seconds and lands well)
2. `graphql-messy` + WS broadcast (keep ping/pong)
3. Act 2 journal detour (near-miss alone carries it)
4. Email (keep SMS+OTP)

## If something breaks live

- Any step is re-runnable; `./demo/seed.sh` + re-import returns to a known state in ~10 s.
- Dashboard blank → hard-reload; check tenant switcher is on the tenant you think.
- gRPC refuses → re-run `grpc-descriptor` (descriptor upload is hot, idempotent).
- Recording stuck → `record-stop`, then start over from `record-start` (per-tenant, no restart).
