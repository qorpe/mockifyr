# G15 — Message-based + extras (Faker, JWT, WebSocket, multi-domain)

A grab-bag of WireMock 4.x-beta and extension features. Some are ordinary templating/matching additions
(Faker) that validate cleanly against an extension oracle; others (WebSocket, still beta) have no stable
oracle and will use alternative validation. Each slice states its method.

## Faker / `random` helper (G15a)

- **Group / item:** G15a — validated **structurally** against WireMock + the faker extension.
- **`{{random 'Class.method'}}`** renders fake data from a Datafaker-style expression, mirroring
  WireMock's faker extension (the `random` helper, backed by Datafaker). Mockifyr uses **Bogus**
  (Datafaker's .NET counterpart). A broad subset of the common providers is supported —
  `Name.firstName/lastName/fullName/name/username/prefix`,
  `Internet.emailAddress/url/uuid/domainName/ipV4Address/macAddress`,
  `Address.city/country/countryCode/zipCode/state/stateAbbr/streetAddress/streetName/buildingNumber/`
  `secondaryAddress/fullAddress/latitude/longitude`, `Number.digit`, `Company.name`,
  `Commerce.productName`, `Lorem.word/sentence`, `PhoneNumber.phoneNumber/cellPhone`. An **unknown
  expression** yields WireMock's own error string (`[ERROR: Unable to evaluate the expression <expr>]`).
- **Learned: `Internet.url` is scheme-less.** Datafaker renders `www.<word>.<tld>` with **no**
  `https://` prefix, whereas Bogus's `Internet.Url()` prepends one. The provider is therefore composed
  by hand (`www.{DomainWord()}.{DomainSuffix()}`) to hold the oracle contract. This divergence sat
  undetected because `Internet.url` was registered but had no field in the regression case — pinning
  the previously untested providers (`url`, `country`, `Company.name`, `PhoneNumber.phoneNumber`,
  `Name.name`) is what surfaced it.
- **Learned: dotted helper names are not dispatchable.** Handlebars.Net parses `{{geo.city}}` as a
  **model path**, not a helper invocation, so a helper registered under the name `geo.city` renders
  **empty** (only the bracket-escaped `{{[geo.city]}}` reaches it). Faker categories are therefore
  addressed exclusively through the string argument (`{{random 'Address.city'}}`), which is also what
  WireMock does — a `{{geo.city}}`-style surface would be a non-parity invention.
- **How it's validated (the racy-feature method).** Faker output is non-deterministic, so it can't be
  byte-diffed. Instead the same stub is served by both sides and, over 15 iterations, each generated
  field must satisfy a **format contract** (e.g. email regex, a 5(-4)-digit zip, a single digit, a
  UUID). The **oracle** satisfying the contract proves it is real WireMock/Datafaker behavior;
  **Mockifyr** (Bogus) satisfying the same contract is the parity claim — the same structural method
  the `randomValue` helpers (G2e) and `now` use. The G15e long-tail providers each carry their own
  contract (dotted-quad IPv4, colon-hex MAC, a domain ending in a TLD, a period-terminated sentence),
  loose enough for both engines yet tight enough to catch a wrong provider.
- **Deferred (tracked):** the remaining long tail of Datafaker providers beyond this subset (added on
  demand); argument-taking expressions (`Number.numberBetween` etc.); locale selection.
- **Learned: a sampled contract needs a derived character set, not a guessed one.** Contracts are
  asserted against *random draws*, so a character that appears in a rare catalog entry passes locally
  and fails later. Two escaped a 15-draw run and only surfaced afterwards, both in the country catalog
  that `Address.fullAddress` embeds: **parentheses** ("British Indian Ocean Territory (Chagos
  Archipelago)") and an **ampersand** ("Svalbard & Jan Mayen Islands"). The fix is methodological —
  the permitted set for every provider was derived by sampling each one **300k times** and collecting
  the non-alphanumeric characters actually emitted, rather than widening the regex one failure at a
  time. The iteration count is also raised to 60. When adding a provider, derive its set the same way:
  a contract that only ever saw the common values is not evidence.
- **Regression case:** `G15aFakerTests.FakerHelper_StructurallyMatchesTheOracle` (30 fields: 9 core,
  the G15e long-tail, the geo/address/phone set, and the previously unpinned providers). The extractor
  is anchored to the `|` delimiter — an unanchored `name=[…]` search matched inside `username=[…]` and
  compared the wrong provider's output.

## JWT / `jwt` helper (G15b)

- **Group / item:** G15b — validated by **content parity** against WireMock + the JWT extension.
- **`{{jwt sub='u1' role='admin'}}`** renders a signed JWT, mirroring WireMock's JWT extension. Claim
  defaults match the reference exactly (read from its source): `iss=wiremock`, `aud=wiremock.io`,
  `sub=user-123`, `iat=now`, `exp=now+maxAge` (default **36500 days**); any non-reserved parameter
  becomes a **private claim**. Signed **HS256**. The signing is hand-rolled (base64url header/payload
  + HMAC-SHA256) — no new dependency.
- **How it's validated.** WireMock's default signing secret is **random per instance** and `iat` is the
  current time, so a token **can't be byte-diffed**. Instead the same stub is rendered by both sides,
  both tokens are decoded, and the **header and non-time claims must be identical** — the meaningful
  content parity. The oracle producing those claims proves it is real extension behavior; Mockifyr
  producing the same claims is the parity claim. The racy `iat`/`exp` are checked structurally
  (`iat` ~now, `exp = iat + default maxAge`) and the signature must be well-formed.
- **RS256 (feature audit).** `{{jwt alg='RS256' …}}` signs with **RS256** (a per-instance RSA key,
  `RSA.Create`), the header carrying `alg=RS256` + a random `kid` and the payload leaking the `alg`
  claim — matching the reference. Content-validated the same way (the `kid` is key-specific/random, so —
  like the signature — it is excluded from parity; the RSA signature must be well-formed).
- **JWKS (G15h).** `{{jwks}}` renders the **JSON Web Key Set** for the RS256 public key the `{{jwt}}`
  helper signs with — served from a stub, exactly like the reference extension. The shape matches the
  oracle byte-for-shape: `{ "keys": [ { "kty":"RSA", "kid":…, "use":"sig", "alg":"RS256", "n":…, "e":… } ] }`,
  `n`/`e` big-endian base64url. The key is random per instance, so it is validated **structurally** (the
  racy-feature method) plus a **self-consistency** anchor: on each side an RS256 token minted by `{{jwt}}`
  **verifies** against the public key in that side's `{{jwks}}`, and the token's `kid` names that key — so
  the jwks exposes the same key that signs the tokens, exactly as the oracle's does.
- **Deferred (tracked):** a configurable signing secret (so tokens are byte-compatible with a specific
  WireMock instance), `nbf`, array/object claims, and the claim-parsing helpers. WireMock also (quirkily)
  leaks `maxAge` into the payload as a claim; Mockifyr consumes it instead — a documented deviation.
- **Regression cases:** `G15bJwtTests.Jwt_ContentMatchesTheOracle`,
  `G15hJwksTests.Jwks_StructureAndSelfConsistency_MatchTheOracle`.

## Multi-domain matching — `host` / `port` / `scheme` (G15c)

- **Group / item:** G15c — validated **byte-for-byte** against the oracle (deterministic, so no
  structural fallback is needed).
- **What it is.** WireMock 3.x lets one instance serve many domains by matching on the request's
  `host`, `port`, and `scheme` in the `request` block. `scheme` is a plain string (`"http"`/`"https"`);
  `host` is a full **StringValuePattern** (so `equalTo`, `matches`, `contains`, … all apply); `port`
  is an integer. Mockifyr parses all three in the WireMock JSON adapter and evaluates them as ordinary
  request matchers.
- **Where the values come from (learned from the oracle).** The differential run **empirically
  confirmed** how real WireMock derives each field — this was the open question the tests resolved:
  - **`host`** = the hostname from the request's **`Host` header** (not the TCP peer / listener name).
    Overriding the `Host` header on the client is enough to route to a different domain's stub.
  - **`port`** = the **port component of the `Host` header** (e.g. `svc.internal:4321` → port `4321`),
    independent of the actual TCP port the request arrived on. (When the `Host` header carries no port,
    WireMock falls back to the listener port — untested here and left to the transport; see Deferred.)
  - **`scheme`** = the listener the request arrived on — `http` over the plaintext port, `https` over
    the TLS port. A stub requiring `scheme: "https"` does **not** match a plaintext request (→ 404).
  - `scheme` comparison is case-insensitive (schemes are canonically lower-case); `host`/`port` use
    their value-matcher / exact-integer semantics.
- **How Mockifyr mirrors it.** `CanonicalRequest` gained `Scheme`/`Host`/`Port`. The request builder
  derives host+port by splitting the `Host` header, exactly as WireMock does, so the in-process
  differential drive and the real HTTP facade (which additionally supplies `scheme` from the
  connection) see the same values the oracle did.
- **Deferred (tracked):** the no-port-in-`Host` listener-port fallback (needs a wire-level facade test,
  not the in-process drive); IPv6-literal `Host` headers (`[::1]:8080`) are passed through unsplit;
  matching on the port a TLS request's `Host` header omits.
- **Regression cases:** `G15cMultiDomainTests` (host equalTo/regex, multi-domain routing, port,
  http/https scheme — 8 cases).

## WebSocket message serving (G15d)

- **Group / item:** G15d — validated by a **self-test round-trip**, not differentially. WireMock's
  WebSocket support is still beta and ships **no stable oracle** (it isn't in the pinned
  `wiremock/wiremock:3.10.0` image), so — like the roadmap flagged from the start — this slice uses
  alternative validation: a real `ClientWebSocket` drives a live Mockifyr host and the replies are
  asserted directly.
- **The model (mirrors WireMock 4's message framework).** A message stub is registered via
  `POST /__admin/message-mappings`: a `trigger` (`message.body` value-matcher) and one or more `send`
  `actions` whose `message.body.data` is a template. A WebSocket client's inbound message is matched
  against every stub's trigger; each matching stub's responses are rendered and sent back to the
  originating channel. Connections are accepted on **any** path.
- **What it reuses (no new matching/templating logic).** The trigger body matcher is the **standard**
  value-matcher set — parsed by wrapping the trigger body as a `bodyPatterns` request pattern
  (`WireMockMappingReader.ReadRequestPattern`), so `equalTo`/`matches`/`matchesJsonPath`/… all work. The
  response `data` renders through the **same Handlebars engine and helpers** as response templating, via
  a small `MessageTemplateRenderer` exposing the inbound message as `{{message.body}}` (so
  `Echo: {{message.body}}` and `{{jsonPath message.body '$.x'}}` work). A new `Mockifyr.Facade.WebSocket`
  project hosts the transport (a front-of-pipeline middleware) + an in-memory, tenant-scoped store; the
  engine stays untouched.
- **Broadcast + admin push (G15e).** A `WebSocketChannelRegistry` tracks the open channels per tenant, so
  a `send` action with a non-`originating` `channelTarget` **broadcasts** to every connected client, and
  the admin **`POST /__admin/channels/send`** (`{message:{body:{data}}}`) dispatches a server-initiated
  message to them. Self-tested with two clients (both receive the admin push and the broadcast).
- **Connect-time messages + `filePath` bodies (G15g).** A mapping with `"trigger": { "type": "connection" }`
  is **connect-time**: its `send` actions fire once, unsolicited, the moment a client connects (rendered
  against an empty message body), rather than on an inbound message. A `send` body may be
  `{ "filePath": "<name>" }` instead of `{ "data": … }`, read from the host's `__files` directory
  (`<root-dir>/__files`, WireMock's convention) and resolved to text at registration so the runtime path
  is unchanged. Connect-time mappings are excluded from the per-message loop; both are self-tested.
- **Deferred (tracked):** per-path/pattern `channelTarget` targeting (broadcast is to all tenant channels);
  binary frames; message-mapping listing/reset. These extend the same seam.
- **Regression cases:** `G15dWebSocketTests` (echo round-trip; `equalTo` routing) +
  `G15eWebSocketBroadcastTests` (admin `channels/send`; broadcast `channelTarget`) +
  `G15gWebSocketConnectFileTests` (connect-time unsolicited send; `filePath` body) — 6 self-tests.

## Broadcast reaches the channels the server has registered (#325)

An admin broadcast (`POST /__admin/channels/send`) and a `channelTarget: broadcast` send both go to the
channels in the registry. A client's `ConnectAsync` returns when the **client** finishes its handshake;
the server adds the channel only after `AcceptWebSocketAsync()` returns. Between those two moments the
client believes it is connected and the registry does not know it exists, so a broadcast in that window
skips it.

**This is a property of the protocol, not a defect to fix.** No amount of server-side care removes the
gap between a client's handshake completing and the server's accept returning, and an operator
broadcasting to a client that connected microseconds earlier has no way to know whether it made it.
Stated here rather than left for somebody to discover from a message that never arrived.

It cost a diagnosis first. `G15eWebSocketBroadcastTests` connected two clients and broadcast
immediately, which failed intermittently — once on a pull request that changed only `CHANGELOG.md` and
a version number. The tests now make registration **observable**: an echo mapping answers a probe, and
the endpoint registers the channel *before* entering its receive loop, so a reply is proof. Ten
consecutive runs green.

The rejected alternative was a sleep. It would have hidden the race rather than closed it, and charged
every future run the time it took to hide it — while still failing on a slow enough machine.
