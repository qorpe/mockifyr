# Mockifyr — Roadmap

Derived from the WireMock feature inventory: ~40 validated steps. **The gate for every
step:** green oracle diff + the regression suite grows + a commit + a short summary. At every
checkpoint: stop, show, get approval. No autonomous drift.

Detailed rationale and per-group contents:
[../ARCHITECTURE.md](../ARCHITECTURE.md#12-full-parity-roadmap-40-validated-steps).

## Phase A — Narrow vertical (first working, proven core)

- [x] **G0** — Foundation + differential harness (solution layout, engine interfaces, tenant
  model, in-memory store, Java WireMock container + canonical diff). Gate met: the harness
  diffs a trivial stub (exact URL + static response) against the `wiremock/wiremock:3.10.0`
  oracle, green. Also lands the first slice of G1a (urlEqualTo/urlPathEqualTo/method/ANY) and
  G2a (static response). The generator is still a stub.
- [x] **G1 — Matching**
  - [x] G1a URL basic (urlEqualTo, urlPathEqualTo, method + ANY)
  - [x] G1b URL advanced — `urlPattern` (anchored full-URL regex), `urlPathPattern` (anchored
    path regex), `urlPathTemplate` (one segment per `{var}`), fuzz-validated. Named path-variable
    **extraction** (`{{request.path.<name>}}`) landed as a later backfill — see G2b
  - [x] G1c header/query/cookie matchers (+ multi-value) — header/query/**cookie**
    `equalTo`/`contains`/`absent`/`doesNotMatch`/`caseInsensitive` and multi-value
    `hasExactly`/`includes` (real keys, not `havingExactly`/`including`) fuzz-validated. Cookie value
    matching was root-caused (a harness keep-alive artifact, not a Mockifyr bug) and now diffs green.
    Header multi-value still awaits a harness that sends discrete header lines (query covers it)
  - [x] G1d body basic (equalTo, binaryEqualTo, contains, matches) — `equalTo`/`contains`/
    `matches`/`doesNotMatch`/`caseInsensitive` and now `binaryEqualTo` (exact byte comparison)
    fuzz-validated
  - [x] **Fuzzing generator** (brief §5) — deterministic seed-driven `MatcherScenarios` emit
    hundreds of corpus-spanning probes; the property suite asserts the match decision agrees
    with the oracle. It already caught the empty-body divergence above.
  - [x] G1e equalToJson (ignoreArrayOrder × ignoreExtraElements) — semantic JSON comparator
    fuzz-validated across all 4 flag combinations plus edges (number precision, null,
    nested-in-array reorder, extra trailing array items). Only duplicate keys and non-body
    targets remain unfuzzed
  - [x] G1f matchesJsonPath — presence + expression/sub-matcher forms fuzz-validated over the
    common subset (property/index/wildcard/recursive-descent) via Newtonsoft as the Jayway proxy;
    filters `[?(...)]`, functions, and indefinite-path sub-matchers deferred
  - [x] G1g equalToXml / matchesXPath — semantic XML equality (whitespace/attr-order/**sibling
    order** insensitive) and XPath presence + text/attribute sub-matcher, fuzz-validated via
    System.Xml. **Namespaced XPath** (`xPathNamespaces` prefix→URI) now works too: a prefixed step must
    match the bound URI, an unprefixed step is namespace-agnostic (matches a default-namespaced doc) —
    the object form requires a sub-matcher (WireMock 422s otherwise), all learned from the oracle.
    **`equalToXml` placeholders** (`enablePlaceholders`) also work: `${xmlunit.ignore}`/`isNumber`/
    `isDateTime`/`matchesRegex(…)` (whole-value only, matchesRegex is a partial match, custom delimiters
    supported), oracle-validated. **XPath functions** work too: a scalar result (`count()`/`contains()`/
    `string()`) matches the presence form regardless of value, and a sub-matcher compares its string form
    (integer for whole numbers, `true`/`false` for booleans). Explicit namespaceAwareness modes,
    `exemptedComparisons`, element-node sub-matcher deferred
  - [x] G1h matchesJsonSchema — JSON Schema validation via json-everything's JsonSchema.Net
    (default Draft 2020-12); inline + string schema forms and `schemaVersion` fuzz-validated over the
    common keyword subset (type/required/properties/bounds/enum/items). **`format`** now matches WireMock:
    assertion on Draft-07 and earlier, annotation-only no-op on 2019-09+ (the reverse of JsonSchema.Net's
    defaults, so the dialect is pinned + `RequireFormatValidation` toggled per draft). networknt's
    **typeLoose** quirk is now reproduced too: a non-string scalar top-level body is validated as its
    JSON-literal string form as well (so `123` matches `type:string`/`enum`/`const`), while the reverse,
    objects/arrays, and nested positions stay strict — the full matrix was mapped against the oracle.
    Internal `$ref` (`#/$defs/…`, `#/definitions/…`) resolves identically to the oracle (validated, no
    change needed). Draft 4 and remote/URL `$ref` deferred. See docs/parity/g1-matching.md
  - [x] G1i date/time matchers — `before`/`after`/`equalToDateTime` on absolute ISO-8601 instants
    (+ `actualFormat`) fuzz-validated; `now`-relative/offset/truncation deferred (racy vs a second
    clock)
  - [x] G1j number matchers — delivered as **JSONPath numeric filters** (`[?(@.x > n)]`),
    fuzz-validated against the oracle for `>`/`>=`/`<`/`<=`/`==` on int & decimal. The standalone
    `equalToNumber`/`greaterThanNumber`/… keys are **not in open-source WireMock** (Cloud-only, no
    oracle) — see docs/parity/g1-matching.md
  - [x] G1k logic (`and`/`or`/`not`) + basicAuth + multipart + stub priority/selection, each
    fuzz-validated. **clientIp is not in open-source WireMock** (rejected `422`, no oracle) — deferred
    like the standalone number matchers. The equal-priority tie-break (load-path dependent) and
    per-part multipart headers are deferred; see docs/parity/g1-matching.md
- [x] **G2 — Response + templating** (G2a–G2h complete)
  - [x] G2a static response — status, multi-value headers, literal `body`, `jsonBody` (compact),
    `base64Body` (bytes) fuzz-validated. `statusMessage` parsed (not yet diffable); `bodyFileName`
    (needs `__files` + templating → G2b) and gzip (transport) deferred. See docs/parity/g2-response.md
  - [x] G2b templating engine — Handlebars.Net wired behind the `response-template` transformer;
    request model (`method`/`url`/`path`/`pathSegments`/`query`/`headers`/`body`), non-escaping
    output, and templated response headers fuzz-validated. `request.path.<name>` named path vars
    (dual string/object model, via a custom Handlebars object descriptor) landed as a backfill and are
    oracle-validated (`Templating_PathVariables`); built-in helpers are G2c–G2h. See
    docs/parity/g2-response.md
  - [x] G2c data helpers — `jsonPath` (scalar, empty-on-miss, compact array, Jackson-pretty object),
    `xPath` (text/attr/string/count values + XML element serialization), `regexExtract`
    (whole-match, capture-group variable, `default=` / error string), `formData` (first value +
    `urlDecode`), `parseJson` (navigable variable — inline **and** the `{{#parseJson}}…{{/parseJson}}`
    block form, block body rendered-then-parsed), validated against the oracle. Multi-value `formData`
    indexing is deferred. See docs/parity/g2-response.md
  - [x] G2d date helpers — `parseDate` (ISO-8601 + Java `SimpleDateFormat` input) composed into
    `date` (Java format patterns incl. `E`/`a`/`S` translation, `epoch`/`unix`, default ISO, plural
    `offset=` units), validated against the oracle over fixed instants. The **`now`** helper (default
    ISO + `offset=` + `format=`) landed as a backfill, **structurally** validated (racy output can't be
    byte-diffed — both sides must produce a correctly-formatted value inside the request's time window;
    `Templating_NowHelper`). `now` `timezone=`/`truncate=` and the unparseable-date fallback remain
    deferred; `timezone=` is ignored on a parsed instant to match the oracle. See docs/parity/g2-response.md
  - [x] G2e random helpers — `randomValue` (UUID + `[a-z0-9]`/`[a-z]`/`[0-9]`/`[0-9a-f]` types with
    `length`/`uppercase`, plus **`ALPHANUMERIC_AND_SYMBOLS`** — lowercase+digits+the printable-ASCII
    symbol set the oracle uses, no `A-Z`/space/`~`), `pickRandom`, `randomInt` (half-open `[lower,upper)`),
    and bounded `randomDecimal`, validated **structurally** against the oracle (the racy output can't be
    byte-diffed, so the oracle and Mockifyr must each satisfy the same charset/length/range
    contract). Unbounded-decimal distribution deferred. See
    docs/parity/g2-response.md
  - [x] G2f json manipulation helpers — `jsonArrayAdd` (parsed item + `maxItems` front-drop),
    `jsonMerge` (deep merge, B over A), `jsonRemove` (path delete) emit compact JSON; `toJson`
    emits Jackson-pretty (shared `JacksonJson.Write`, reused by `jsonPath`). Validated against the
    oracle. Array-valued key merge deferred. See docs/parity/g2-response.md
  - [x] G2g format/math/array helpers — jknack built-ins WireMock registers: `math` (`+ - * /`,
    half-up integer division, Java-style doubles), `numberFormat` (DecimalFormat pattern + currency/
    percent), `size`, `join`, `substring`, `replace`, `upper`, `lower`, `capitalize`, `trim`,
    validated against the oracle. `%`/`^` and non-OSS helpers (abs/round/split/…) deferred. See
    docs/parity/g2-response.md
  - [x] G2h system helpers — `systemValue` (deny-by-default `[ERROR: Access to <key> is denied]`,
    byte-diffed; permitted-key allowlist deferred to G12) and `hostname` (host-specific, validated
    structurally). `systemProperty`/`env` are not in open-source WireMock. See docs/parity/g2-response.md
- [x] **G3 — Webhook / correlation** (G3a–G3b; sub-event journaling → G6/G7)
  - [x] G3a serve-event listener + async outbound — `postServeActions` webhook (static
    method/url/headers/body) fired via `WebhookServeEventListener` (`IServeEventListener`), the
    engine's first outbound I/O at the facade edge. Validated differentially with a host-side
    webhook receiver (oracle reaches it via host.docker.internal). Templating/correlation → G3b.
    See docs/parity/g3-webhook.md
  - [x] G3b templated webhook + originalRequest correlation — the webhook `url` (path + query),
    header values, and body are Handlebars-templated against `originalRequest` (automatic, no
    transformer flag), reusing the response templating engine/helpers via the shared
    `HandlebarsFactory`/`RequestModel` and the `IServeEventTemplateRenderer` seam. Validated
    differentially. Sub-event recording deferred to G6/G7 (no admin/verify surface yet). See
    docs/parity/g3-webhook.md

## Phase B — Everything else, up to parity

- [x] **G4** Delay + fault injection — `fixedDelayMilliseconds` recorded as a `DelayDirective` and
  applied by the facade (content parity + robust lower-bound timing, both sides); `fault`
  (all four kinds) parsed into a `FaultDirective`. Socket-level fault *emission* and
  `delayDistribution` deferred to the HTTP facade (G12). See docs/parity/g4-delay-fault.md
- [x] **G5** Stateful scenarios — `scenarioName`/`requiredScenarioState`/`newScenarioState` parsed
  into `ScenarioBinding` (the engine already gated eligibility + wrote transitions); default start
  state `Started`. Validated differentially with a multi-step state walk and per-scenario isolation.
  Direct state-set + scenarios admin listing → G7. See docs/parity/g5-scenarios.md
- [x] **G6** Verify + near-miss diagnostics — `count`/`find`/`unmatched` over the request journal
  (reusing the stub matchers; `{}` matches all) validated **semantically** against the oracle's
  `/__admin/requests*` (counts, not the volatile-field-heavy JSON). Near-miss ranking by ascending
  match distance validated as pure logic. Cross-engine near-miss identity deferred. See
  docs/parity/g6-verify.md
- [x] **G7** Admin API (full) + first-class stub metadata
  - [x] G7a Application/CQRS + metadata — the `Mockifyr.Application` management path (Mediant 1.0.0):
    Create/Delete/Import/Reset stub commands + GetStubs/GetStub/CountRequests/FindUnmatched queries,
    `Result<T>` pattern, dispatched via `ISender`. `AddMockifyr` composes shared stores + engine +
    handlers, so the management path and serving hot path share state. Adapter now parses stub
    `id`/`uuid` and `metadata`. Validated in-process. See docs/parity/g7-admin.md
  - [x] G7b Admin HTTP facade — `Mockifyr.Facade.Admin` maps `/__admin/*` (mappings CRUD/import/reset,
    requests/count) to `ISender`; validated over HTTP via a `WebApplicationFactory` test host by
    comparing the status-code + mapping-count observation sequence to the oracle (201/200/404/422 all
    match). Mock-serving-over-HTTP + `/__admin/scenarios*` deferred to G12. See docs/parity/g7-admin.md
- [x] **G8** Proxying — `proxyBaseUrl` recorded as a `ProxyDirective`; a facade edge
  (`ProxyResponder`) forwards the matched request (method + path/query + body + headers) to the
  upstream and returns its response. Validated differentially: both sides proxy to one shared
  host-side upstream and the proxied response (status + body + marker header) matches.
  `additionalProxyRequestHeaders` / URL rewriting deferred. See docs/parity/g8-proxy.md
- [x] **G9** Record & Playback — `StubRecorder` proxies to the target, captures the exchange, and
  `WireMockRecordingWriter` generates a stub (exact URL + method + body `equalTo`, captured response).
  Validated by **cross-engine replay**: Mockifyr's generated stubs, loaded into the real oracle,
  replay the captured response (and Mockifyr replays them identically). Recorder admin endpoints,
  filters, body-file extraction, and scenario generation deferred. See docs/parity/g9-record-playback.md
- [x] **G10** Extensibility (public) — `AddMockifyr(cfg => …)` with a `MockifyrExtensions` builder
  registers user extensions; four types validated in-process (custom **matcher** via `customMatcher`,
  **serve-event listener**, **template helper**, **response transformer**). The Core seams were
  already public/dogfooded; the remaining ones (`IResponseDefinitionTransformer`,
  `ITemplateModelProvider`, `IAdminApiExtension`, `IMappingsLoader`) are wired incrementally.
  Validated in-process (not oracle-differential — custom extensions have no WireMock equivalent). See
  docs/parity/g10-extensibility.md
- [x] **G11** HTTPS/TLS + HTTP/2
  - [x] G11a HTTPS/TLS serving — `MockifyrHost` binds `--https-port` on Kestrel with an ephemeral
    self-signed cert (`SelfSignedCertificate`), like WireMock's default. Validated over a **real TLS
    connection** against the oracle's own `--https-port` listener (status/body/headers diff). The
    `--https-port` hook deferred from G12f. See docs/parity/g11-tls-http2.md
  - [x] G11b HTTP/2 — both Kestrel listeners `Http1AndHttp2`; **h2 over TLS (ALPN)** validated against
    the oracle (both negotiate `response.Version` == 2.0, matching body). Plaintext prior-knowledge
    h2c is *not* asserted — the oracle answers it nondeterministically (h2 vs `HTTP_1_1_REQUIRED`); the
    plaintext listener is left h2c-capable to match. See docs/parity/g11-tls-http2.md
  - [x] G11c Configured keystore + mutual TLS — `--https-keystore`/`--https-keystore-password` load the
    server cert from a PFX; `--https-require-client-auth` + `--https-truststore` require and CA-validate
    a client certificate (custom-root-trust chain). **Self-tested** (standard transport auth, no
    WireMock-specific semantics): a client with a CA-signed cert is served, one without fails the
    handshake. See docs/parity/g11-tls-http2.md
- [x] **G12** Transport HTTP facade + standalone/deploy + config
  - [x] G12a Mock-serving HTTP facade — `Mockifyr.Facade.Http` fallback (request → engine → wire),
    hosted by `Mockifyr.Server`. Validated **over the wire** against the oracle (status, reason
    phrase/`statusMessage`, multi-value headers, body, `jsonBody`). Closes mock-serving-over-HTTP +
    `statusMessage`; `delay` applied by the facade; tenant via `X-Mockifyr-Tenant`/default. See
    docs/parity/g12-transport.md
  - [x] G12b Socket faults + `delayDistribution` — all four `fault` kinds emitted over a real Kestrel
    socket (they surface to an HTTP client identically, as a failed request; diffed as
    failed-vs-succeeded against the oracle) and uniform `delayDistribution` (lower-bound timing).
    Lognormal distribution and byte-level fault fidelity deferred. See docs/parity/g12-transport.md
  - [x] G12c Scenarios admin + gzip — `GET /__admin/scenarios` (state + `possibleStates`), set-state,
    reset; and gzip response encoding when the client accepts it. Validated over HTTP against the
    oracle. See docs/parity/g12-transport.md
  - [x] G12d Proxy-over-wire + `/__admin/recordings/*` (HTTP recording mode) — the outbound edge
    (`ProxyResponder`/`StubRecorder`/`RecordingSession`) extracted to `Mockifyr.Outbound` (shared by
    both facades, no facade→facade dep); a `proxyBaseUrl` stub now proxies **over the wire** (closes
    the G8 wire gap, previously validated only in-process) and record-through-proxy (`start`/`stop`/
    `status`/`snapshot`) captures generated stubs that replay on the real oracle. Validated over HTTP.
    See docs/parity/g12-transport.md
  - [x] G12e `/__admin/ext/*` admin-extension routing — `IAdminApiExtension` made dispatchable
    (transport-agnostic `AdminApiRequest`/`AdminApiResponse` + `HandleAsync`); the admin facade routes
    any request under `/__admin/ext/<prefix>/*` to the extension whose `RoutePrefix` is that first
    segment (subpath + query + body lowered, extension owns everything below, unknown prefix → 404).
    Registered via `AddMockifyr(cfg => cfg.AddAdminApiExtension(…))`. Like the other extension seams
    (G10) there is no WireMock oracle, so validated in-process over HTTP. See docs/parity/g12-transport.md
  - [x] G12f Standalone/deploy & config — `MockifyrHost.Build(args)` binds the mock-serving port
    (`--port`, WireMock default `8080`) and, given a `--root-dir`, loads its `mappings/*.json` (single
    stubs + `{"mappings":[…]}` bundles, filename order) into the default tenant at startup via the
    `IMappingsLoader` seam (`DirectoryMappingsLoader`). `Program` is now thin. Deploy/config plumbing
    (the loaded stubs' serving is already oracle-covered), so validated in-process over HTTP: the
    loader parses a temp dir, and a real Kestrel host on an ephemeral port serves a disk-loaded stub.
    `--https-port` landed in **G11a** (needs TLS). See docs/parity/g12-transport.md
- [x] **G13** gRPC extension
  - [x] G13a Unary serving — `Mockifyr.Facade.Grpc`: a descriptor-driven `ProtobufJsonCodec`
    (protobuf ↔ proto3-JSON via `CodedInputStream`/`CodedOutputStream`, since C# has no runtime
    `DynamicMessage`) + a gRPC HTTP/2 middleware that decodes the call, routes it through the
    **unchanged** `StubEngine` as a POST to `/service/method`, and re-encodes the response. Descriptors
    from `<root-dir>/grpc/*.dsc`. Validated against the **official WireMock gRPC extension** oracle over
    TLS (a unary `SayHello` reply matches). See docs/parity/g13-grpc.md
  - [x] G13b Codec expansion — `ProtobufJsonCodec` now covers `enum` (by value name), `map` (as a JSON
    object ↔ entry messages), and repeated fields (packed + unpacked), driven by the descriptor.
    Validated against the oracle with a `Describe` call carrying repeated/enum/map in and a packed
    repeated `int32` out. `oneof`/wrappers, streaming, status responses, and gRPC admin reset deferred.
    See docs/parity/g13-grpc.md
  - [x] G13c oneof + well-known wrappers — `oneof` is transparent (a member is an ordinary tagged field,
    so only the set one is read/written — no codec change). The wrapper types (`StringValue`/`Int32Value`/
    `Int64Value`/`BoolValue`/…) render as their **bare inner scalar** (not `{"value":…}`); the codec
    detects them by full name and unwraps on decode (synthesizing the type default when the wire omits it)
    / re-wraps on encode, confined to the message path so they work anywhere a message can. Validated
    over the wire with a `Wrapped` call carrying wrappers + a oneof in both request and reply; the `.dsc`
    was regenerated with `--include_imports`. Streaming/status/admin-reset deferred. See docs/parity/g13-grpc.md
  - [x] G13d Error / status responses — the extension returns a gRPC error via two response headers
    (`grpc-status-name` = the code name, `grpc-status-reason` = the detail); the message body is not
    delivered. The middleware maps the name to its `google.rpc.Code` number and writes the `grpc-status`/
    `grpc-message` trailers, no message frame — an error is just a stub with those headers, no new Core
    surface. Validated over the wire: a `NOT_FOUND` stub fails the call with the same code + detail on
    both sides. See docs/parity/g13-grpc.md
  - [x] G13e Admin-managed gRPC stubs — a stub POSTed to `/__admin/mappings` at runtime is served over
    gRPC on both sides (the management/CQRS store feeds the gRPC hot path — the gRPC analogue of G7a).
    The oracle revealed that WireMock's `/__admin/reset` *reloads* file-backed mappings (so it does not
    clear a file-seeded stub); Mockifyr's reset clears without reload, so reset-reload parity is deferred
    and the test uses admin-add (never reset). Streaming deferred. See docs/parity/g13-grpc.md
- [x] **G14** GraphQL extension
  - [x] G14a Query matching — a `GraphqlQueryMatcher` (parse + AST-sort + canonical print, so equal
    queries match regardless of whitespace and field/argument order) via GraphQL-Parser; the adapter
    recognizes the `graphql-body-matcher` `customMatcher` (`parameters.query`). Validated against the
    **community WireMock GraphQL extension** oracle across five query variants (exact/reformatted/
    reordered/different/invalid all agree). See docs/parity/g14-graphql.md
  - [x] G14b Variables + operationName — `GraphqlQueryMatcher` now aggregates query + `variables`
    (semantic JSON-equal, or absent when unspecified) + `operationName` (string-equal, or absent), the
    way the extension does. Validated against the oracle across five request variants. See
    docs/parity/g14-graphql.md
  - [x] G14c Response templating over a GraphQL match — the extension is only a matcher, so a matched
    stub renders through the standard `response-template` transformer; `request.body` in the template is
    the original GraphQL POST body, so `{{jsonPath request.body '$.variables.id'}}` etc. work. Validated
    over the wire: a stub extracting the request's variables/operationName renders the same response body
    on both sides. (A stub must constrain the fields it templates, per the G14b absent-when-unspecified
    rule.) See docs/parity/g14-graphql.md
  - [x] G14d Directive + fragment ordering — the AST sort now also normalizes **directive** order (and
    each directive's argument order) on every node that can carry them (`IHasDirectivesNode`), so a field
    with reordered `@include`/`@skip` matches. Confirmed against the oracle (which normalizes directives
    too). See docs/parity/g14-graphql.md
- [x] **G15** Message-based/WebSocket + JWT + Faker + multi-domain
  - [x] G15a Faker / `random` helper — `{{random 'Class.method'}}` renders fake data (Datafaker-style
    expression) via **Bogus** (Datafaker's .NET counterpart), a curated provider subset; unknown
    expression → WireMock's error string. Racy output, so **structurally** validated against the
    WireMock faker-extension oracle (each field satisfies a format contract on both sides over many
    iterations). See docs/parity/g15-extras.md
  - [x] G15b JWT / `jwt` helper — `{{jwt sub=… role=…}}` renders an HS256-signed JWT with claim defaults
    matching WireMock (`iss`/`aud`/`sub`/`iat`/`exp`, default maxAge 36500 days) + custom claims;
    hand-rolled HMAC (no new dep). Random secret + racy `iat`, so validated by **content parity**
    (decoded header + non-time claims match the JWT-extension oracle; `iat`/`exp`/signature structural).
    (RS256 added in bucket ③ #1.) Configurable secret, `nbf`, array claims deferred. See docs/parity/g15-extras.md
  - [x] G15h JWKS / `jwks` helper — `{{jwks}}` renders the JSON Web Key Set for the RS256 public key the
    `jwt` helper signs with (`{ "keys":[{ kty,kid,use,alg,n,e }] }`), served from a stub like the reference.
    Racy key → validated **structurally** + a **self-consistency** anchor: an RS256 `{{jwt}}` token verifies
    against that side's `{{jwks}}` key (and its `kid` names it) on both the oracle and Mockifyr. See
    docs/parity/g15-extras.md
  - [x] G15c Multi-domain — `request.host` / `request.port` / `request.scheme` matching so one instance
    serves many domains. `scheme` is a plain string, `host` a full StringValuePattern (equalTo/matches/…),
    `port` an integer. Byte-diffed against the oracle; the run **confirmed** WireMock derives host+port
    from the `Host` header and scheme from the listener. `Host`-header-less port fallback + IPv6 literals
    deferred. See docs/parity/g15-extras.md
  - [x] G15d WebSocket message serving — WireMock 4's message framework: register a message-mapping via
    `POST /__admin/message-mappings` (a `trigger` body matcher + `send` actions with a templated
    `message.body.data`); a WebSocket client's inbound message is matched and the templated replies sent
    to the originating channel. A new `Mockifyr.Facade.WebSocket` project (front-of-pipeline middleware +
    in-memory store) reuses the standard body value-matchers and the templating engine (`{{message.body}}`)
    — the engine is untouched. **No stable WireMock oracle** (beta), so validated by a self-test round-trip.
    See docs/parity/g15-extras.md
  - [x] G15e/g WebSocket broadcast + admin push + connect-time + `filePath` — a `WebSocketChannelRegistry`
    powers `channelTarget` **broadcast** and admin `POST /__admin/channels/send` (G15e); a
    `"trigger": { "type": "connection" }` mapping sends **unsolicited on connect**, and a `send` body may be
    `{ "filePath": … }` read from `<root-dir>/__files` (G15g). All self-tested (6 cases). Binary frames /
    per-pattern channel targeting deferred. See docs/parity/g15-extras.md
  - [x] G16a File-based persistence — an `IStubPersistence` seam (no-op default) the management-path
    handlers call; `--root-dir` registers `FileSystemStubPersistence`, writing each stub as an
    id-stamped WireMock JSON file to the **same** `<root>/mappings` the G12f loader reloads, so
    create/import/delete/reset survive a restart with stable ids. Durability validated over the admin
    API; the reloaded stub's served response is diffed against the oracle. Multi-tenant reload,
    Postgres (G16c)/Redis (G16d), and change-feed (G16e) deferred. See docs/parity/g16-persistence.md
  - [x] G16b LiteDB persistence — `LiteDbStubPersistence` + `LiteDbMappingsLoader` behind the same
    `IStubPersistence`/`IMappingsLoader` seams (proving multi-provider), each stub a document in an
    embedded single-file db; `--litedb <path>` turns it on (DI-owned `LiteDatabase` singleton).
    Durability validated over the admin API; reloaded response diffed against the oracle. Redis
    (G16d)/change-feed (G16e) deferred. See docs/parity/g16-persistence.md
  - [x] G16c PostgreSQL persistence — `PostgresStubPersistence` + `PostgresMappingsLoader` (Npgsql)
    behind the same seams; each stub a row, upserted, with a shared `CREATE TABLE IF NOT EXISTS`.
    `--postgres <connstr>` turns it on. Durability validated against a **real Postgres container**
    (Testcontainers); reloaded response diffed against the oracle. Redis (G16d)/change-feed (G16e)
    deferred. See docs/parity/g16-persistence.md
  - [x] G16d Redis persistence — `RedisStubPersistence` + `RedisMappingsLoader` (StackExchange.Redis)
    behind the same seams; each tenant's stubs a Redis hash keyed by id. `--redis <connstr>` turns it
    on. Durability validated against a **real Redis container** (Testcontainers); reloaded response
    diffed against the oracle. See docs/parity/g16-persistence.md
  - [x] G16e Change-feed reload — every `RedisStubPersistence` mutation announces on a pub/sub channel;
    `--change-feed` opts a host into a `RedisChangeFeedReloader` (`IHostedService`) that reloads +
    reconciles its store on any announcement. Multi-instance coherence validated with **two live hosts**
    sharing Redis (create/delete on one propagates to the other without a restart). See
    docs/parity/g16-persistence.md
  - [x] G16f Postgres change-feed reload — the same coherence over PostgreSQL `LISTEN`/`NOTIFY`: every
    `PostgresStubPersistence` mutation runs `NOTIFY mockifyr_changes`; `--change-feed` opts a
    Postgres-backed host into a `PostgresChangeFeedReloader` (`IHostedService`) that `LISTEN`s and
    reconciles via the shared `ChangeFeedReconciler`. Validated with **two live hosts** sharing a Postgres
    container. See docs/parity/g16-persistence.md
  - [x] G16g Multi-tenant change-feed reload — the reconcile now spans **every** tenant, not just the
    default: `IStubStore.GetTenants()` + optional `IMultiTenantMappingsLoader.LoadAllTenants()` (Postgres
    `SELECT tenant,json`; Redis `SCAN mockifyr:stubs:*`) let `ChangeFeedReconciler` upsert-then-prune per
    tenant over the union of reloaded + in-store tenants. Validated: a peer writing two non-default tenants
    is served/pruned independently under the `X-Mockifyr-Tenant` header. See docs/parity/g16-persistence.md

## Out of scope — WireMock **Cloud**, not open-source (no OSS oracle)

These exist only in WireMock Cloud; the pinned OSS oracle (`wiremock/wiremock:3.10.0`) **rejects them
with `422`**. Implementing them would make Mockifyr *diverge* from OSS WireMock rather than close a
parity gap — the opposite of the goal — and, with no oracle, could only be self-validated (against
golden rule #3). They are therefore deliberately unsupported. Reaching **Cloud** parity is a separate
track that would need a Cloud reference/oracle of its own.

- **`clientIp` request matching** — OSS rejects the stub (`422`). (`CanonicalRequest.ClientIp` is
  carried, so a future Cloud track has the plumbing.)
- **Standalone number matchers** (`equalToNumber`/`greaterThan`/`greaterThanOrEqual`/`lessThan`/
  `lessThanOrEqual`) — Cloud-only. The OSS-available route (**JSONPath numeric filters**, G1j) is
  already delivered and oracle-validated.
- **`systemProperty`/`env` helpers** — the OSS `systemValue` helper (with `type=PROPERTY|ENVIRONMENT`,
  deny-by-default) already covers this surface (G2h); the distinct Cloud helper names are not in OSS.
- **`math` `%`/`^` operators** — OSS WireMock's own `math` helper rejects them at registration, so *not*
  supporting them is correct parity.

## Remaining after the feature audit — triaged (every open item accounted for)

The top-down WireMock feature audit closed the practical gaps (matching: `doesNotContain`,
`formParameters`, `exemptedComparisons`, element-node XPath, namespaced/function XPath, XMLUnit
placeholders; response templating: the `request.host/port/scheme/baseUrl/cookies/bodyAsBase64` model +
`base64`/`urlEncode`/`formatJson`/`formatXml`/`assign`/`isOdd`/`isEven`/`range`/`array`/`lookup`/
`arrayAdd`/`truncateDate`; proxy `additionalProxyRequestHeaders`). Everything still open is triaged
below — none is a silent gap.

- **① Racy — validated as far as physically possible (structural, or documented no-oracle-claim).** A
  byte diff is impossible because the output depends on a live clock/RNG or non-observable transport
  timing: `now`-relative date matchers, `now` `timezone=`/`truncate=`, the unparseable-date fallback,
  unbounded `randomDecimal` distribution, **lognormal** `delayDistribution` + `chunkedDribbleDelay` (no
  reliable lower bound), byte-level fault fidelity, `request.id` (random UUID). These are closed to the
  extent the oracle allows.
- **② Oracle rejected / behavior quirky — deferred with evidence.** Probed against the oracle and left
  out because there is nothing valid to reproduce: `jsonSort` (oracle 500s), `soapXPath` (empty
  result), `arrayRemove` (removes the last element regardless of index), `matchesJsonPath` array-size
  (`length()`/`size()` filters don't match).
- **③ Separate efforts (real features, each a mini-project with its own validation setup) — ✅ ALL DONE.**
  Each oracle- or self-validated with its own PR: **RS256** JWT (#1), **remote/URL `$ref`** JSON Schema
  (#2), single-message gRPC **streaming** (#3), WebSocket **broadcast** / `channels/send` (#4), the
  **Datafaker long tail** (#5), multipart **`request.parts`** templating (#6), **mTLS** / configured
  keystore (#7), the Postgres **`LISTEN`/`NOTIFY`** change feed (#8), and **multi-tenant persistence
  reload** (#9). **Micro-edges — ✅ all done too:** GraphQL directive/fragment ordering (G14d), WebSocket
  connect-time / `filePath` (G15g), and the JWKS `{{jwks}}` helper (G15h). Nothing tracked remains open
  before the UI.
- **④ Out of scope — WireMock Cloud, not OSS.** See the section above (`clientIp`, standalone number
  matchers, `systemProperty`/`env`, `math` `%`/`^`) — implementing would *diverge* from the OSS oracle.

## Post-phase — UI / dashboard (`ui/`)

The dashboard is a decoupled React SPA (`ui/`) that consumes only the `/__admin/*` REST API — it
cannot touch the engine, so the .NET side stays untouched (differential suites remain the safety net).
Delivered in phased, build-green PRs.

- [x] **UI-P0** Foundation + app shell — React 19 + TS + Vite + Tailwind v4 + shadcn/ui (Radix).
  Token-first design system (near-black accent, one-file re-skin), class-driven dark mode, 6 locales
  incl. RTL (react-i18next). Praxis-style shell: pill nav, segmented tabs, rounded auto-scroll surface,
  collapsible sidebar (icon rail + tooltips), bottom profile menu (language + dark mode). Dashboard page
  with KPI cards. `pnpm build` green; `dotnet build` unaffected. See `ui/README.md`.
- [x] **UI-P1a** Stubs data-grid + tenant switcher — TanStack Table (sortable columns, URL filter,
  density toggle, row selection + bulk bar, pagination, skeleton/empty states, protocol tabs
  HTTP/gRPC/GraphQL/WebSocket), method chips + status pills from the semantic token ramp. First-class
  **multi-tenancy**: a sidebar tenant switcher scopes the grid; TanStack Query + an admin API client
  send `X-Mockifyr-Tenant` and fall back to sample data when no host answers. `pnpm build` green.
- [x] **UI-P1b** Stub editor — a right slide-over (Radix Dialog) with **Form + JSON** dual-mode
  (React Hook Form + Zod; live form→JSON sync, raw-JSON escape hatch). Covers method, URL match
  (url/urlPath/pattern), header/query/body matchers (field arrays), response status/headers/body +
  templating, fixed delay, fault, proxy, scenario (name/required/new state), priority. Create/edit/delete
  wired to `/__admin/mappings` (tenant-scoped, sonner toasts, query invalidation); sample-mode fallback
  when no host. `pnpm build` green; verified in-browser. (Full-fidelity edit of an existing mapping's raw
  body is P1c; edit currently seeds method/url/priority/scenario.)
- [x] **G7b** Admin tenant resolution — every `/__admin/*` route now scopes to the tenant named by the
  `X-Mockifyr-Tenant` header (the same header the mock-serving facade honours); an absent header resolves
  to the default tenant, so single-tenant callers are unchanged. Makes the UI tenant switcher real
  end-to-end. Self-tested isolation (`G7bAdminTenantTests`) — a stub created under one tenant is visible
  only to it; no oracle (WireMock is single-tenant). Recordings/`ext` stay global.
- [x] **UI-P2 + G6b** Request journal — a new `StubEngine.GetServeEvents` + `GetServeEventsQuery` +
  admin **`GET /__admin/requests`** (tenant-scoped, `?unmatched=true` filter) expose the request log;
  the UI Journal page (TanStack Table) shows method / URL / status (colour-coded) / matched-vs-unmatched,
  with an All/Unmatched toggle, filter, pagination and 5s auto-refresh. Self-tested end-to-end
  (`G6bJournalTests`: matched + unmatched events, unmatched filter, tenant isolation). `dotnet`/`pnpm`
  builds green. (Timestamps aren't shown — the pure engine doesn't stamp time by design.)
- [x] **UI-P3** Scenarios — a card grid of the tenant's stateful stub groups; each card shows the state
  machine as chips (current state highlighted, click another to move the scenario via
  `PUT /__admin/scenarios/{name}/state`) plus a **Reset all** action (`POST /__admin/scenarios/reset`).
  Tenant-scoped (TanStack Query + mutations, sonner toasts); sample fallback when no host. UI-only —
  the admin endpoints already existed. `pnpm build` green; verified in-browser.
- [x] **UI-P4** Recordings — a record-through-proxy control (target base URL → Start; live Recording/
  Stopped status with 4s poll; Snapshot / Stop capture the generated stubs) plus a captured-stubs list.
  Wired to `/__admin/recordings/{start,status,snapshot,stop}` (session is global, not tenant-scoped);
  sonner toasts + sample fallback. UI-only. `pnpm build` green; verified in-browser.
- [x] **UI-P5 + G7c** Settings/Status + Extensions — closes the UI. A new admin **`GET /__admin/health`**
  reports host name/version, the **active persistence provider**, and live tenant/stub counts (from DI);
  the Settings page shows Status (real health) + Persistence (active provider highlighted) + Transport
  (read-only capability list — host-config, not admin-mutable) + Appearance (theme + language). An
  Extensions page documents the built-in capabilities (templating helpers, matchers, protocols) and
  extension seams. Self-tested (`G7cHealthTests`: reports provider + live counts). `dotnet`/`pnpm` green.

**UI complete** — every nav destination (Dashboard · Stubs · Journal · Scenarios · Recordings ·
Extensions · Settings) is a real, tenant-aware, i18n'd (6 locales incl. RTL), dark-mode page. Mockifyr
is now end-to-end: engine + platform + dashboard.

- [x] **UI polish** — all 6 locales fully translated (no English fallback); ⌘K command palette (cmdk);
  route-level code-splitting (editor deps load on demand).
- [x] **UI deploy (G12g)** — `--dashboard <dir>` serves the built UI under the reserved `/__mockifyr`
  prefix (static + SPA fallback), scoped so mock-serving is untouched (`G12gDashboardTests`);
  `pnpm build:embedded` (base `/__mockifyr/`); a multi-stage **Dockerfile** builds one image serving the
  engine + admin + dashboard; **CI** now also builds the dashboard on every PR.
- [x] **Recording chains repeated requests into scenarios** — capturing the same request twice used to
  produce two disconnected duplicate stubs; the oracle showed WireMock chains repeats into a
  generated scenario instead (first capture serves at `Started` and advances; replay yields the
  recorded responses in recorded order; distinct requests stay scenario-free). `RecordingSession`
  now generates identically (oracle-pinned by
  `Recording_RepeatedIdenticalRequests_CaptureLikeTheOracle`; chain rules unit-tested and
  mutation-tested to 100 %). Closes the G9-era "repeats → scenarios" deferral.
- [x] **Recording decodes compressed upstream bodies** — a gzip/deflate/br response (any browser-driven
  recording of a real API) used to bake its raw compressed bytes into the generated stub as mojibake;
  the stub now stores the decoded payload without `Content-Encoding`, matching the oracle
  (`Recording_AGzippedUpstreamResponse_GeneratesAReplayableStub`; learned note in
  `docs/parity/g12-transport.md`). Live pass-through unchanged.
- [x] **Recordings flow completed** — the captured-stub list's dead-end closed: **View JSON** now
  actually renders the generated mapping (it was an empty `<details>`), and captured stubs can be
  saved — per-row **Add to stubs** and **Import all** go through the standard bulk-import path,
  imported rows leave the list, and the tenant switch clears it. The session hint corrected to match
  the oracle-verified semantics (recording proxies EVERY request — see `docs/parity/g12-transport.md`)
  and a new differential test pins that behavior. Verified in-browser end-to-end against a live
  upstream (record → snapshot → view → import → replay, including a faithful 404 replay).
- [x] **UI test runner** (#203) — a **Test** button on HTTP/GraphQL stubs opens a dialog that fires a
  REAL request at the host (embedded: same origin; dev: a `/__mock/` proxy tunnel): method/URL +
  Params/Headers/Body tabs seeded from the stub's exact-match matchers, client-side `{{key}}` preview,
  and the response (status/time/size, headers, pretty body) in the journal's visual language
  (`http-view` shared components) with copy-body / copy-as-curl and a body Beautify. The request runs
  the full serving pipeline — environments resolve, scenarios advance, and it lands in the journal.
  No server change; verified in-browser (matched 200, unmatched 404, network error, reopen-resets).

## G17 — Environments (post-UI)

- [x] **G17 Environments** (#165, #166) — tenant-scoped keys, each with several values and one active,
  referenced from stubs as `{{key}}` and resolved **at serve time** rather than baked in at save time.
  Replaces the UI-only localStorage design of #157, which froze the value into the mapping and leaked
  across tenants. `IEnvironmentStore`/`IEnvironmentResolver` in Core (every method tenant-scoped;
  `RenderContext.Tenant` is `required` so forgetting the scope is a compile error), a pre-Handlebars
  substitution pass that touches only defined keys, `/__admin/environments` CQRS + REST, persistence
  across file/LiteDB/Postgres/Redis, and a rewritten dashboard page. Keys named after a built-in helper
  are refused (`Environment.ReservedKey`). No WireMock oracle exists for this, so it is validated by
  unit + behavioral self-tests and a 26-check end-to-end script including a restart; see
  `docs/parity/g17-environments.md` and ADR 0008. `dotnet`/`pnpm` green; verified in-browser.
- [x] **G17b — Environments ride in export/import bundles** (#198). The dashboard export switches to
  the `{"mappings":[…]}` wrapper when the tenant has environments and adds a sibling `environments`
  section (keys, values, active selection — `resolved` stays out, it is computed); the import path
  (`POST /__admin/mappings/import`, and therefore the UI import tab) restores that section before
  loading the mappings, through the same validation as the admin PUT. Overwrite-by-key semantics;
  invalid entries are skipped without failing the import; bare-array and section-less exports import
  unchanged. `EnvironmentJsonReader` mutation-tested to **100 %** (37/37 killed); behavioral
  self-tests in `G17EnvironmentExportImportTests` (no oracle — WireMock has no environments).
  Verified in-browser end-to-end (export file inspected, import restores the Environments page).

## G18 — Message mocking: email + SMS (ADR 0009, ADR 0010)

Mockifyr becomes a **message capture platform** as well as an API mock: applications send real
email (SMTP) and SMS (provider HTTP APIs) at Mockifyr, get realistic protocol answers, and every
message lands in a tenant-scoped, queryable inbox — protocol mock + capture/verify in one tool.
No WireMock oracle exists for any of this; each vertical is validated by real-client self-tests
(MailKit, the official Twilio C# SDK) plus unit/integration/mutation coverage, stated per vertical
in `docs/parity/g18-messages.md`. Everything is opt-in: no flag → no listener, no routes, no
behavior change.

- [x] **G18-pre — Protocol-aware stub UX** (#184, ADR 0010). Computed read-only `protocol` field on the
  admin mappings list (`grpc` via descriptor lookup, `graphql` via the custom matcher, else
  `http`) — never stored, byte-identical round-trip asserted. UI: protocol badge + facet on the
  stub tree; Add flow starts with a channel choice (HTTP form unchanged; gRPC service/method from
  loaded descriptors; GraphQL query editor emitting the `graphql-body-matcher` JSON; WebSocket
  message-mapping form). Descriptor upload/list/delete via admin + Settings. WS mappings listed in
  the UI.
- [x] **G18a — Core message model + store + admin API** (#185). `MessageEnvelope` (channel `email`|`sms`),
  tenant-scoped `IMessageStore` (bounded, ring-buffer eviction) + `IMessageSink` in Core (zero
  deps); in-memory store; `/__admin/messages` CQRS + REST: list (channel/recipient/text filters),
  get, delete, reset, count.
- [x] **G18b — SMTP facade (capture)** (#186). `Mockifyr.Facade.Smtp`: opt-in `--smtp-port` ESMTP
  listener (EHLO/MAIL/RCPT/DATA/QUIT; AUTH accepted-unchecked), MimeKit parse at the edge →
  envelope → sink. Tenant from AUTH user, else recipient domain, else default. Self-test: MailKit
  sends; capture asserted through the admin API.
- [x] **G18c — Mail inbox UI** (#187). Messages section: inbox list with search/filters, detail view
  (sandboxed HTML preview, source, headers, attachments), delete/clear. Verified in-browser.
- [x] **G18d — SMS provider profile: Twilio + UI** (#188). Opt-in `--sms-profile twilio` mounts
  `POST /2010-04-01/Accounts/{sid}/Messages.json`: form body → SMS envelope → store; realistic
  Twilio JSON reply. Self-test: the official Twilio C# SDK pointed at Mockifyr sends and accepts
  the response. UI: SMS thread view per recipient with OTP badges.
- [x] **G18e — Behaviors: faults, webhooks, retention** (#189). SMTP fault injection (550 reject, delay,
  drop), provider error simulation, message-received events through `IServeEventListener` → the
  G3 webhook infrastructure, store capacity/retention flags.
- [x] **G18f — Verify + OTP extraction** (#190). Count/matcher verify on `/__admin/messages` (sibling of
  `/__admin/requests`), `GET /__admin/messages/{id}/otp?pattern=…` (default `\b\d{4,8}\b`); e2e:
  an app sends an OTP mail + SMS, the test retrieves the code in one admin call.

## G19 — Integration sandbox: stateful resources, OpenAPI import, access (ADR 0011)

Mockifyr becomes usable as a **self-hosted integration sandbox platform**: dynamic CRUD state
(`POST /orders` creates what `GET /orders/{id}` returns), OpenAPI-driven bootstrap ("spec in,
working sandbox out"), and operator-issued API keys that scope traffic to a tenant. Everything
rides the existing multi-tenancy, scenarios, delay/fault, messages and persistence — state and
quotas are facade-applied *directives* (like delay/fault), the engine stays pure, and the
mapping-JSON parity surface does not move (the differential suites must stay green throughout).
No WireMock oracle exists for any of this; each vertical is validated by real-client self-tests
plus unit/integration/mutation coverage, stated per vertical in `docs/parity/g19-sandbox.md`.
Everything is opt-in: no directive, no flag, no import → no behavior change.
**Enterprise-readiness acceptance criteria and the binding per-vertical test matrix live in ADR
0011's addendum** (admin/key separation, key material + restart persistence, race-free quotas,
size caps + pagination, SSRF-safe import, version semantics, compliance notes) — they are part of
each item's definition of done, not follow-ups.

- [x] **G19a — Core resource model + store + admin API.** `ResourceDocument` (id, collection, JSON
  body Core never parses, timestamps, version), tenant+collection-scoped `IResourceStore`
  (bounded, ring-buffer eviction) + `IResourceIdGenerator` seam + `ResourceOptions` in Core (zero
  deps); `InMemoryResourceStore` (injected clock, updates never evict, last-write-wins keeps
  position); `/__admin/resources` CQRS + REST: collections, paginated list, get/put/delete,
  per-collection + per-tenant reset, transactional seed import (JSON array → collection, ids from
  the seam when absent); flags `--resource-limit` / `--resource-max-body` (413 beyond the cap).
  Addendum criteria delivered: exact-boundary validation (collection 64 / id 256 / UTF-8 byte
  cap), honest 404/413/422 surface, pagination from day one, concurrency-safe store. 25 unit + 8
  handler + 7 wire tests; **Stryker 100 %** on `ResourceRules` (24/24) and
  `InMemoryResourceStore` (24/24); differential suites untouched and green. See
  `docs/parity/g19-sandbox.md`.
- [x] **G19b — State directive + templating.** Opt-in `state` directive on stub responses
  (create/read/update/delete/list on a named collection; applied by the templating renderer —
  the engine keeps calling the same `IResponseRenderer` seam, untouched); operation result exposed
  as `{{state.id|body|version|count|list}}`; `id`/`document` are templates over the request with
  generator/request-body defaults; unknown-id misses short-circuit to a configurable status
  (default 404), and the serve-time guards reuse `ResourceGuards` (413 over the cap, 422 non-JSON/
  unknown operation). Declaring the directive is the templating opt-in. Self-tested end-to-end
  over the wire (POST→GET→PUT→LIST→DELETE incl. admin-surface agreement, tenant isolation, and a
  zero-change proof); **Stryker 100 %** on `StateDirectiveApplier` (44/44). See
  `docs/parity/g19-sandbox.md`.
- [x] **G19c — OpenAPI import.** `Mockifyr.Adapters.OpenApi` (Microsoft.OpenApi.Readers, MIT,
  edge-only): OpenAPI 3.0/3.1 (JSON + YAML) → ordinary mappings — generated as mapping JSON and
  read back through the SAME reader as any bundle, so dialect compliance holds by construction.
  Examples serve as-is; example-less schemas synthesize via `SchemaSample` (Faker-backed formats,
  enum-first, deterministic dates); `?stateful=true` wires resource-shaped pairs to the G19b
  directive (create incl. `Location` header, read/update/delete/list). `/__admin/openapi/import`
  + the **OpenAPI** channel in the Add-stub chooser (all six locales). Addendum delivered: no
  remote `$ref` fetch (typed refusal names the pointer), 5 MiB + 32-depth spec-bomb guards,
  transactional import. Golden-file fixtures pin the output byte-for-byte; wire tests prove
  import BY SERVING (incl. the full stateful CRUD loop from YAML); **Stryker 97.3 %** with the
  five survivors analyzed as equivalents in `docs/parity/g19-sandbox.md`.
- [x] **G19d — Sandbox access: API keys + quotas.** Opt-in `--sandbox-auth`: hashed per-tenant keys
  (`IApiKeyStore` in Core, **persisted via the G16 seam** — keys survive restarts) managed via
  `/__admin/apikeys`; key-based tenant resolution ahead of the ADR 0003 host/header chain
  (gRPC/GraphQL/WS inherit via the HTTP facade; SMTP keeps AUTH-as-tenant); **a sandbox key never
  reaches `/__admin/*`**; optional per-key request quota (race-free, fixed window) with realistic
  `429` + rate headers; usage counters via admin. Self-test: two keys → two tenants → provably
  isolated stubs/resources; parallel quota-boundary test. Delivered exactly as specified —
  wire self-tests against a restarted real host, **Stryker 28/29** with the single survivor
  analyzed as equivalent in `docs/parity/g19-sandbox.md`.
- [x] **G19e — Sandbox UI + positioning.** Sidebar gains a **Sandbox** group (between Mocking and
  Platform): **Resources** (browse collections/documents per tenant, edit/delete/reset, seed
  import) and **Access** (issue/revoke keys, quotas, usage); dashboard quick-start "spin up a
  sandbox" (import spec → seed data → issue key → copy base URL). Verified in-browser against a
  live `--sandbox-auth` host end-to-end (a key issued through the dialog authenticated a real
  request and its usage showed 3/50 in the table); all six locales shipped.

## G20 — Payload cryptography (ADR 0012)

**Complete** (v0.21.0). Enterprise upstreams protect the payload on top of TLS; today every body
matcher sees ciphertext, templating cannot correlate, and nothing can encrypt or sign a response.
Phased behind explicit per-stub opt-in, with key material at the host edge and Core seeing only an
abstract scheme (`IPayloadDecryptor` / `IPayloadProtector`).

- [x] **G20a — field-level decryption for matching + templating.** `request.decrypt: { scheme, fields }`
  with JWE compact (dir + A256GCM, RFC 7516 §5.1) via `--decrypt-key`; the envelope keeps matching as
  today, the named fields become matchable and templatable. Decryption is a *view* matching looks
  through — the recorded request stays what the client sent (asserted on the journal). Key material
  lives in the new `Mockifyr.Crypto` project; Core keeps zero dependencies. 11 unit + 4 wire tests
  against an independent RFC implementation, **Stryker 26/29** with the survivors analyzed as
  deliberate defense-in-depth redundancy in `docs/parity/g20-cryptography.md`.
- [x] **G20b — response protection.** `response.protect: { scheme, fields }` encrypts named fields
  (readable envelope) and, with no field named, the whole body as one JWE token. Runs LAST — after
  templating and every transformer — so what is encrypted is what would have gone on the wire; the
  serve event records the protected response. Fresh nonce per token (asserted); a body that cannot
  carry named fields is served as rendered rather than silently switching shape. 6 unit + 4 wire
  tests, all decrypting with the paired implementation, **Stryker 100 %**.
- [x] **G20c — signing.** `request.signature { scheme, header, digestHeader }` requires a signed
  request (an unsigned or tampered one is a non-match, and the gate fails closed with no verifier),
  and `response.sign { … }` adds the digest of the served bytes plus its HMAC — applied after
  protection so it covers what the client receives. PSD2 / Berlin Group header names by default,
  HMAC-SHA256 over the `Digest` value, `--sign-key`. 8 unit + 4 wire tests with independently
  computed signatures; **Stryker 11/14** with three analyzed equivalents.
- [x] **G20e — cryptography in the dashboard.** `/__admin/health` reports the four capabilities the
  host was given keys for; Settings shows them as a card, and stub rows carry lock/signature icons
  for declared crypto. Answers the two different questions — what the stub asks for, and what the
  host can honor — so a keyless host is diagnosable instead of mysterious. 2 wire tests +
  in-browser verification, six locales.
- [x] **G20d — whole-body inbound decryption.** `decrypt` with no `fields` decrypts the entire body
  as one JWE token — the mirror of `protect` with no fields, and the case `binaryEqualTo` cannot
  express because a fresh IV changes the bytes every request. Whitespace tolerated; a non-token body
  is left untouched (non-match). 2 unit + 1 wire test; **G20 complete**.

No oracle exists (the reference engine has no payload cryptography), so validation follows the
G18/G19 precedent: real-client self-tests against a standard JOSE library, unit tests + Stryker on
the pure logic, and the differential suite staying green to prove the parity surface did not move.

Deferred edges (tracked from day one in `docs/parity/g19-sandbox.md`): durable resource
persistence via the G16 seam, GraphQL SDL / AsyncAPI import, per-key scenario isolation, OpenAPI
*export* of authored stubs. Out of scope by decision (ADR 0011): developer portal,
self-registration, billing, OAuth issuance, hosted SaaS.

## G21 — The broker channel (ADR 0013)

The integration sandbox was HTTP-shaped: a team could mock the call that *starts* a payment and not
the event that reports it *settled* — the half that is hardest to test, because it has no synchronous
reply to assert on. Kafka first, because it is the harder shape (partitions, consumer groups,
offsets); designing for it means AMQP fits inside rather than the reverse. Everything is opt-in: a
host without `--kafka-bootstrap` builds no producer, joins no group and connects to nothing.

No oracle exists — the reference engine has no broker concept — so validation follows the G18/G19
precedent: a **real broker in a Testcontainer** driven by the **official client**, plus unit tests and
Stryker on the pure logic. Recorded in `docs/parity/g21-broker.md`.

- [x] **G21a — publish on match** (#301). A `publish` post-serve action beside `webhook`: a stub
  answers `201` *and* emits. Templated topic/key/body/headers, delivery recorded on the journal entry
  either way. The ADR's image-size trigger fired (+20 MB measured) and the recorded judgement is that
  the split it prescribed is not worth taking yet, with the number and the revisit condition written
  down rather than the deviation being silent.
- [x] **G21b — capture** (#302). `--kafka-subscribe` lands what the system under test publishes in the
  tenant's message inbox, so `/__admin/messages` and its verify surface answer for broker messages
  with no new API — one inbox, as the ADR decided. Offsets commit after the inbox write.
- [x] **G21c — serve on consume** (#291). `brokerMappings`: an inbound message matches and produces
  outbound ones. `whenTopic`/`whenHeaders`/`whenMessage` reuse the existing value and body matchers, so
  a broker stub is new syntax around oracle-verified semantics; replies template against
  `message.body`/`topic`/`key`/`headers.*` and resolve the tenant's environments and clock. Every
  matching mapping contributes (a fan-out is a real broker pattern), and an unmatched message is
  acknowledged rather than parked. 31 unit + 7 integration cases; **Stryker 89.80 %** with three
  analyzed equivalents.
- [x] **G21d — AMQP** behind the same `IBrokerPublisher` contract (`--amqp-uri`, `--amqp-subscribe`).
  The design bet paid: the publisher implements the existing contract unchanged and everything above
  it — mappings, templates, matchers, inbox, tenancy, admin routes — needed no transport-specific
  code. Two translations are stated rather than assumed: `"topic": "exchange/routing.key"` (a
  slash-free topic uses the default exchange, so one dialect means the obvious thing on both), and a
  partition key becoming `MessageId` since AMQP has no counterpart. A host may run both, with a
  `kafka:`/`amqp:` topic prefix naming one. 12 unit + 8 integration cases against a real RabbitMQ;
  **Stryker 6/7** with one proven-equivalent survivor. The ADR's image-size trigger fired and the
  measurement overruled it — `RabbitMQ.Client` is 0.33 MB, pure managed. **G21 complete.**

Two silent gaps found after G21a/b shipped, by running the released image rather than by a failing
test (1.10.1): a `publish` action on a host with no broker did nothing and said nothing, and a failed
publish recorded that it failed but not what it was carrying. Both closed; both recorded.

- [x] Adopt the published @qorpe/ui kit — M1 drop-ins + M2 (facet/search/tooltip/sheet/json-editor on kit 0.1.2; five locals deleted); M3 (all eleven form selects onto the family Select; NativeSelect deleted). Every remaining local carries a written reason or a trigger in ADR 0014; the dashboard has a kit-freshness gate + lint in CI. **M4 done**: the shell set moved onto the kit's `AppShell` and `CommandPalette` (249-line sidebar deleted, 340 lines out against 157 in), after seven kit gaps found — three by reading, four only by using the screens — went back as 0.2.0 through 0.3.2.

## G22 — the sandbox as a partner-facing platform (epic #345, ADR 0015)

An analysis of what the sandbox would need to be handed to an external partner rather than only used
internally. Filed as an epic with thirteen children in three phases; the register of what is open is
`docs/parity/deferred-edges.md`, not this list.

- [x] **#350 — relations.** Not an enhancement: a spec with `/customers/{customerId}/orders` imported
  to a flat collection, so every modelled customer listed every other customer's orders. Relations are
  declared once per collection and derived from the path shape at import; the key lives in the body
  when the contract declares it and in an optional metadata pointer otherwise, so the document still
  round-trips byte-for-byte and `POST /__admin/openapi/verify` cannot report our own sandbox as
  drifted. `onDelete` defaults to `restrict` — deleting a Stripe customer does not delete their
  charges — and enforcement is presence-triggered, which keeps mutually referencing collections
  creatable and makes cycles legal. Serving the imported spec found a second bug that had been there
  since G19c and that no unit test could see: the created-resource `Location` header carried a literal
  `{customerId}`. Relations are stored as documents under a reserved collection, so they persist,
  restore and reload on all four backends with no per-backend code — the alternative being ~400 lines
  mirrored four times, for state that must not outlive the documents it describes.
- [x] **#346 — a partner-safe principal.** `--partner-credential` is the same tenant scoping as `--tenant-credential` plus a refusal on the ways this host acts on the network. The issue listed three admin routes; the analysis found that half the capability lives in the **data plane**, because `POST /__admin/mappings` accepts `proxyBaseUrl` and post-serve actions — so blocking routes alone would have shipped a control that looks like it holds and does not. Both are refused, the refusal names the field, and OpenAPI import is unaffected. `--block-outbound-routes` also stopped being silent on an authenticated host, where it did nothing and said nothing.
- [x] **#348 — secret environment values.** An optional `secret` flag whose literal is withheld from the admin API, the dashboard and export bundles, and still resolved when a stub is served. Two leak points, not one: the value in the list and the `resolved` literal computed from it. Redaction creates its own hazard — a redacted read handed back on save stores empty strings — so a withheld secret means *unchanged*, an explicit literal rotates, and a brand-new secret with no literal is dropped rather than stored empty. Driving the real dashboard found that the screen would have **deleted** secrets on an untouched save, because the UI required `value`, filtered blank rows out and lost the marker on the way back. Taken ahead of #347 deliberately: that issue's own checklist says a partner may read environment values but never secrets, which was unenforceable until this existed.
