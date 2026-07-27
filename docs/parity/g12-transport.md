# Parity notes — G12 Transport HTTP facade

Verified WireMock behaviors that are only observable **over the wire**, against the oracle
(`wiremock/wiremock:3.10.0`). The mock-serving HTTP facade is where the several transport/socket
behaviors deferred throughout the roadmap are finally implemented and validated.

## Mock serving over HTTP (G12a)

- **Group / item:** G12a — validated **over real HTTP** (not in-process). `Mockifyr.Facade.Http`'s
  `MapMockServing` fallback turns every non-admin request into a `CanonicalRequest`, resolves it
  through the pure `StubEngine`, and writes the response to the wire. `Mockifyr.Server` hosts the
  admin surface + this fallback.
- **How it's validated.** The same stub is loaded and driven over HTTP against **both** the oracle and
  a hosted Mockifyr (`WebApplicationFactory<Program>`), and the wire responses are compared — one
  `DriveOverWire(client, …)` helper run on each (like the G7b admin diff). Reason phrase, multi-value
  headers, and the body are all read from the real `HttpResponseMessage`.
- **Closed here (previously deferred to G12):**
  - **Mock serving over HTTP** — responses are now produced by a real transport, not just in-process.
  - **`statusMessage`** — the custom **reason phrase** is written on the status line (`HTTP/1.1 418 I
    am a teapot`) via `IHttpResponseFeature.ReasonPhrase`, and diffed. (It was parsed since G2a but
    only assertable over the wire.)
  - **Multi-value response headers on the wire** (`X-Multi: a` / `X-Multi: b`) and `jsonBody` served
    over HTTP.
- **Response `delay`** is applied by the facade (`await Task.Delay`) before writing — the in-process
  library facade already did this; the HTTP facade now does it over the wire.
- **Server-injected headers are masked.** The oracle adds `Matched-Stub-Id`, `Transfer-Encoding:
  chunked`, etc.; only the headers a scenario declares are compared (plus status, reason phrase, body).
- **Tenant resolution.** The facade reads an optional `X-Mockifyr-Tenant` header, else the default
  tenant (the oracle is single-tenant, so the wire diff uses the default).
- **Deferred within G12:** socket **fault emission** (the four `fault` kinds) → **G12b**;
  `delayDistribution` → G12b; `/__admin/scenarios*`, `/__admin/recordings/*`, `/__admin/ext/*`, and
  gzip/`Content-Encoding` → **G12c**; TLS + HTTP/2 → **G11**. The **no-match 404 body** is WireMock's
  verbose near-miss diagnostic table — a genuine moving target tied to G6; only the 404 **status** is
  served/diffed, the body is left for a focused near-miss-diagnostic follow-up.
- **Regression case:** `G12aHttpServingTests.Serves_OverTheWire_MatchingTheOracle`.

## Fault injection + delayDistribution (G12b)

- **Group / item:** G12b — validated **over a real Kestrel socket** (faults need genuine transport;
  the in-memory test server can't reproduce a connection reset). `MockifyrKestrelHost` starts Mockifyr
  on an ephemeral loopback port; the test drives it and the oracle with a real `HttpClient`.
- **All four `fault` kinds surface identically to an HTTP client** — a failed request. Verified: with
  a `.NET HttpClient`, `EMPTY_RESPONSE`, `MALFORMED_RESPONSE_CHUNK`, `RANDOM_DATA_THEN_CLOSE`, and
  `CONNECTION_RESET_BY_PEER` all throw `HttpRequestException` (inner `HttpIOException`) against the
  oracle — they are **not** distinguishable at the client layer. So the differential compares the
  **outcome class** (request failed vs succeeded): every fault stub must fail the request on both
  sides, and a control (non-fault) stub must succeed on both.
- **Emission.** The facade breaks the connection: empty-response/reset `context.Abort()` with nothing
  written; malformed/random write a few bytes first, then abort mid-response. Byte-level fidelity
  (garbage vs reset) is not client-observable and would need raw-transport access — deferred as a
  fidelity nuance, not a functional gap (the observable contract "the request fails" is met).
- **`delayDistribution` (uniform)** — a random delay drawn from `[lower, upper]`, applied by the
  facade. Validated over the wire with a **lower-bound** assertion (a uniform delay can't be shorter
  than `lower`; robust against CI variance). **Lognormal** distributions are deferred — no reliable
  lower bound to assert (racy, like `now`/random).
- **Regression cases:** `G12bFaultTests.Fault_BreaksTheConnection_LikeTheOracle` (4 kinds + control),
  `G12bFaultTests.UniformDelayDistribution_AppliesAtLeastTheLowerBound_LikeTheOracle`.

## Scenarios admin + gzip (G12c)

- **Group / item:** G12c — validated over HTTP against the oracle.
- **Scenarios admin.** `GET /__admin/scenarios` lists each scenario with its **`state`** and
  **`possibleStates`** (WireMock: the default `Started` plus every state the scenario's stubs require
  or transition to). `PUT /__admin/scenarios/{name}/state` sets a state directly;
  `POST /__admin/scenarios/reset` returns all to `Started`. Wired thin (HTTP → Mediant
  `GetScenariosQuery`/`SetScenarioStateCommand`/`ResetScenariosCommand`). Validated **semantically**
  by driving a three-state walk over HTTP and comparing the scenario's `state`, `possibleStates`, and
  the served responses (incl. after a set-state) — identical on both sides. (The admin JSON's
  per-scenario `mappings` carry volatile ids, so they aren't byte-compared.)
- **gzip.** WireMock gzips the response body whenever the client sends `Accept-Encoding: gzip`,
  **regardless of content type** (verified: a body with no declared type was still gzipped). ASP.NET's
  built-in response compression is MIME-gated, so the facade gzips in the handler instead. Validated
  over the wire: `Content-Encoding: gzip` and the **decompressed** body match the oracle (the
  compressed bytes differ by implementation and aren't compared).
- **Deferred to G12d (explicitly tracked — not a silent gap):** the `/__admin/recordings/*` HTTP
  endpoints (the stateful recording *mode* on the mock server; the recorder *logic* is validated in
  G9) and `/__admin/ext/*` admin-extension routing (the `IAdminApiExtension` seam is public;
  dispatching it over HTTP is a small follow-up). Then standalone/deploy + config, then G11.
- **Regression cases:** `G12cAdminTests.Scenarios_Admin_MatchesTheOracle`,
  `G12cAdminTests.Gzip_MatchesTheOracle`.

## Proxy-over-wire + recording mode (G12d)

- **Group / item:** G12d — validated over HTTP against the oracle.
- **Outbound edge extraction.** `ProxyResponder`, `StubRecorder`, and the new `RecordingSession` live
  in a dedicated **`Mockifyr.Outbound`** project (references Core + the WireMock JSON adapter, no
  transport). Both the library facade (in-process, G8/G9) and the HTTP facade (over the wire, G12d)
  depend on it — so the wire path reuses the *same* responder/recorder the in-process differentials
  already proved, and neither facade depends on the other (the architecture's facade→facade ban).
- **Proxy over the wire.** A `proxyBaseUrl` stub, when matched, forwards the request to the upstream
  over HTTP and relays the upstream response **verbatim** — status, headers (minus the transport
  headers Kestrel reframes), and body with no re-encoding (the upstream already set its own
  `Content-Encoding`; the facade does **not** re-gzip a proxied body). This closes the wire gap G8
  left open (G8 proved proxying only in-process). Validated by driving the same `proxyBaseUrl` stub
  over HTTP against both sides (each pointed at the shared upstream by the host it can reach) and
  diffing status + body + the upstream's `X-Upstream` marker.
- **Record-through-proxy mode.** WireMock's recording is a stateful *mode* on the server: while a
  session is live, every incoming request is proxied to the target and a stub generated from the
  exchange. Modeled with a singleton `RecordingSession` shared between the admin control endpoints and
  the mock-serving fallback: `POST /__admin/recordings/start` (`{"targetBaseUrl":…}`) begins,
  `GET /__admin/recordings/status` reports `Recording`/`Stopped`, `POST /__admin/recordings/snapshot`
  returns the stubs captured so far, and `POST /__admin/recordings/stop` ends and returns the
  `{"mappings":[…]}` envelope. The recorder *logic* was proven in G9; G12d proves the wire *mode* —
  the fallback intercepts while recording, and the stubs it generates over HTTP load into the **real
  oracle** and replay the captured response (status + body + `X-Upstream`).
- **Learned (oracle-verified): recording proxies EVERY request — matched ones included.** A stub that
  existed before `recordings/start` does **not** answer while the session is live: the oracle proxies
  the request to the target and captures it like any other, and existing stubs resume only when the
  recording stops. Assumed the opposite ("only unmatched requests are proxied", as a low-priority
  catch-all would behave) until the diff refuted it — the dashboard hint used to make that wrong
  claim. Captured upstream 404s are faithful too: a recorded 404 replays as a 404 stub.
- **Deferred to G12e (explicitly tracked — not a silent gap):** `/__admin/ext/*` admin-extension
  routing (the `IAdminApiExtension` seam is public; dispatching it over HTTP is a small follow-up) and
  standalone/deploy + config (host config, `--port`/`--https-port`, mappings-dir load). Then G11
  (HTTPS/TLS + HTTP/2). Recording refinements from WireMock — filters, body-file extraction, and
  repeat-request → scenario generation — remain deferred (noted on `StubRecorder` since G9).
- **Regression cases:** `G12dProxyRecordTests.Proxy_OverTheWire_MatchesOracle`,
  `G12dProxyRecordTests.Record_OverTheWire_GeneratesStubsThatReplayOnOracle`,
  `G12dProxyRecordTests.Recording_ProxiesEveryRequest_EvenOnesAnExistingStubWouldMatch`.

## Admin-extension routing (G12e)

- **Group / item:** G12e — validated **in-process over HTTP** (custom admin endpoints have no WireMock
  equivalent, so — like the other extension seams in G10 — there is no oracle to diff against; the
  contract validated is *our* routing/dispatch, not parity with WireMock's Java extension API).
- **Dispatch shape.** `IAdminApiExtension` gains a transport-agnostic handler:
  `Task<AdminApiResponse> HandleAsync(AdminApiRequest, CancellationToken)`. The request is *lowered*
  from HTTP to `AdminApiRequest(Method, Subpath, Query, Body)` — the extension never sees an
  `HttpContext`, keeping the transport out of the extension contract (mirrors how Core never sees a
  transport). `AdminApiResponse(Status, ContentType, Body)` (with a `Json(...)` helper) is written
  back verbatim.
- **Routing.** The admin facade maps a catch-all `Map("/ext/{**rest}")` under `/__admin`. The first
  path segment after `/__admin/ext/` selects the extension by exact `RoutePrefix`; the remainder is
  the `Subpath` (leading slash preserved, empty when the prefix itself is hit). The extension owns
  everything below its prefix — an unrecognized *subpath* is the extension's own decision (it may
  return 404), while an unknown *prefix* (or no extensions registered) is a facade 404.
- **Registration.** `AddMockifyr(cfg => cfg.AddAdminApiExtension(ext))` registers each as a DI
  singleton; the route resolves `IEnumerable<IAdminApiExtension>`.
- **Deferred to G12f (explicitly tracked — not a silent gap):** standalone/deploy + config (host
  config, `--port`/`--https-port`, mappings-dir load) — the final G12 slice. Then G11 (HTTPS/TLS +
  HTTP/2).
- **Regression cases:** `G12eAdminExtensionTests.RegisteredExtension_ServesUnderItsPrefix`,
  `G12eAdminExtensionTests.UnknownPrefix_Returns404`,
  `G12eAdminExtensionTests.ExtBaseWithNoRegistration_Returns404`.

## Standalone host + config (G12f)

- **Group / item:** G12f — validated **in-process over HTTP** (deploy/config plumbing; the loaded
  stubs' *serving* semantics are already oracle-covered throughout the roadmap, so there is nothing
  new to diff against WireMock — the contract validated is "read a mappings dir and bind a port").
- **`MockifyrHost.Build(args)`.** The standalone composition, separated from `Program` (now a
  one-liner) so tests exercise the exact wiring. Reads config keys `port` and `root-dir` from the
  command line (`--port`, `--root-dir`). Binds `http://0.0.0.0:<port>` (WireMock's default **8080**;
  port `0` asks Kestrel for an ephemeral port, which the tests use).
- **Mappings-dir load.** Given `--root-dir <path>`, a `DirectoryMappingsLoader` (the `IMappingsLoader`
  seam, public since G10, wired here) reads `<path>/mappings/*.json` — each file a single stub or a
  `{"mappings":[…]}` bundle — in **filename order** (deterministic ids / equal-priority tie-breaks) and
  imports them into the default tenant at startup. Non-`.json` files and a missing directory are
  ignored (empty load). It does file I/O, so it lives at the host edge, never in Core. `customMatcher`
  references in files resolve against the DI `IMatcherRegistry`.
- **Deferred to G11 (explicitly tracked — not a silent gap):** `--https-port` and the rest of
  HTTPS/TLS + HTTP/2 (a real TLS listener needs a certificate — that is G11's scope, kept out of G12f
  to avoid a no-op flag). This closes the **G12** group.
- **Regression cases:** `G12fStandaloneHostTests.DirectoryMappingsLoader_ReadsSingleStubsAndBundles_InFilenameOrder`,
  `G12fStandaloneHostTests.DirectoryMappingsLoader_MissingDirectory_ReturnsEmpty`,
  `G12fStandaloneHostTests.Host_LoadsRootDirMappings_AndServesThemOverHttp`.
