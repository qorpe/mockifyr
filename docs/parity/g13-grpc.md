# G13 — gRPC extension

gRPC parity by **reuse, not reimplementation**: a gRPC call is decoded to JSON, run through the exact
same stub engine (matching + templating) as HTTP, and the response JSON is re-encoded to protobuf. The
engine never learns about protobuf; gRPC is a codec + transport at the facade edge — the design
ARCHITECTURE.md called for. Validated against the **official WireMock gRPC extension** as the oracle.

## Unary serving (G13a)

- **Group / item:** G13a — validated over the wire against WireMock + its gRPC extension.
- **A gRPC stub is an ordinary stub.** WireMock's gRPC extension (and Mockifyr) map a call to a POST to
  `/{package.Service}/{Method}` with the request message as a JSON body — so a stub is just
  `urlPath` + an `equalToJson` body pattern → a `jsonBody` response. The whole matching/response
  machinery is unchanged; only the message ↔ JSON conversion is new. Confirmed: the same stub JSON,
  loaded into both sides, yields byte-identical replies for the same call.
- **Descriptor-driven codec.** Unlike Java, C#'s Google.Protobuf has **no runtime `DynamicMessage`**, so
  Mockifyr can't parse an arbitrary user message from a descriptor out of the box. `ProtobufJsonCodec`
  fills the gap: given only a `MessageDescriptor` (loaded from a compiled `.dsc`), it walks the wire
  format with `CodedInputStream`/`CodedOutputStream`, mapping each field by its descriptor to/from
  proto3-canonical JSON (the same protobuf-java-util shape the extension emits) — so the JSON matches
  what a hand-written stub expects. Covered: the proto3 scalars, `string`/`bytes` (base64), nested
  messages, and non-packed repeated fields; 64-bit ints render as JSON strings (proto3 JSON).
- **Descriptors.** `ProtoDescriptors` loads `*.dsc` (from `<root-dir>/grpc/`, the same place WireMock's
  extension reads) and indexes services/methods by the gRPC path. Serving is enabled when descriptors
  are present.
- **Transport.** The `GrpcServingMiddleware` handles the gRPC HTTP/2 framing (`[flag][len][message]`)
  and the `grpc-status` trailer, ahead of the HTTP mock-serving fallback. gRPC requires HTTP/2; it is
  driven **over TLS (ALPN-negotiated h2)**, since plaintext h2c is nondeterministic on WireMock
  (`HTTP_1_1_REQUIRED` — see g11-tls-http2.md). Mockifyr serves gRPC on its `--https-port` listener.
- **Validation.** The oracle is the real WireMock image with the official
  `wiremock-grpc-extension-standalone` jar loaded (downloaded once and cached, mounted with the
  descriptor + stub). The same unary `SayHello` call is driven against the oracle and Mockifyr with a
  real gRPC client; the replies must be equal.
- **Deferred (explicitly tracked — not a silent gap):** client/server **streaming**; packed repeated
  scalars, **maps, enums, oneofs**, and the well-known wrapper types in the codec; gRPC **status/error**
  responses and no-match semantics; and gRPC admin reset. Each builds on this codec + middleware.
- **Regression case:** `G13aGrpcTests.UnaryCall_MatchesTheOracle`.

## Codec expansion — enum / map / repeated (G13b)

- **Group / item:** G13b — validated over the wire against WireMock + its gRPC extension.
- **More proto3 field kinds.** `ProtobufJsonCodec` now covers, driven by the descriptor:
  - **`enum`** — decoded to its value *name* (proto3 JSON), encoded from name (or number).
  - **`map<k,v>`** — a map field is repeated entry messages on the wire; decoded to a JSON object and
    encoded back to entries. (A decoded entry value is detached from its entry before being placed in
    the map — a `JsonNode` may have only one parent.)
  - **repeated** — both **packed** scalar/enum (a single length-delimited run, decoded until the
    sub-stream ends) and **unpacked** (repeated tags); written unpacked, which every reader accepts.
- **Validation.** A `Describe` call carries a repeated `string`, an `enum` (`GREEN`), and a
  `map<string,int32>` in the request, and a repeated (packed) `int32` in the reply. The same stub is
  served by the oracle and Mockifyr; the decoded replies (`summary`, `codes`) must match — so the
  request JSON Mockifyr decodes matches the stub's `equalToJson` exactly as the reference extension's
  does, across all these field kinds.
- **Deferred (tracked):** `oneof` and the well-known wrapper types (→ G13c); streaming; gRPC
  status/error responses; gRPC admin reset.
- **Regression case:** `G13bGrpcCodecTests.Describe_WithEnumMapRepeated_MatchesTheOracle`.

## Codec expansion — oneof / well-known wrappers (G13c)

- **Group / item:** G13c — validated over the wire against WireMock + its gRPC extension.
- **`oneof` is transparent — no codec change.** A oneof member is an ordinary tagged field on the
  wire, so decode reads whichever member's tag arrives, and encode writes only the member present in
  the JSON (the field loop already skips absent properties). The oneof grouping in the descriptor
  needs no special handling; a differential test simply pins that a set member round-trips and the
  others stay absent.
- **Well-known wrapper types** (`StringValue`, `Int32Value`, `Int64Value`, `BoolValue`, `DoubleValue`,
  `FloatValue`, `UInt32Value`, `UInt64Value`, `BytesValue`) **do** need special-casing. In proto3 JSON a
  wrapper renders as its **bare inner scalar** — `StringValue "x"` → `"x"`, not `{"value":"x"}` — even
  though on the wire it is still a message with a single `value` field (#1). The codec detects a wrapper
  by full name and, on the message path only:
  - **decode:** unwraps the inner message to its `value` scalar; an absent `value` on the wire (proto3
    omits default scalars) means the wrapper carries its type's **default** (`""`/`0`/`false`/`"0"` for
    64-bit), which is synthesized so a present-but-default wrapper still renders.
  - **encode:** re-wraps the bare scalar into `{value: …}` before encoding the message.
  This is confined to `ReadValue`/`WriteValue`'s message case, so wrappers work anywhere a message can
  appear (singular, repeated, map value).
- **Validation.** A `Wrapped` call carries `StringValue`/`Int32Value`/`BoolValue` wrappers and the
  `text` arm of a request `oneof`; the reply carries `StringValue`/`Int64Value` wrappers and the `ok`
  arm of a reply `oneof`. Because the request's `equalToJson` (`{note,count,active,text}` with bare
  values) matches on both sides, Mockifyr's decoded proto3 JSON is proven identical to the reference
  extension's; the decoded replies (`label`, `total`, `ResultCase`/`ok`) must then match too. The
  `.dsc` fixture was regenerated with `protoc … --include_imports` so `google/protobuf/wrappers.proto`
  is resolvable in the descriptor set.
- **Deferred (tracked):** wrapper fields left at their default value inside a repeated/map (only the
  singular default is exercised); streaming; gRPC admin reset.
- **Regression case:** `G13cGrpcWrappersOneofTests.Wrapped_WithWrappersAndOneof_MatchesTheOracle`.

## Error / status responses (G13d)

- **Group / item:** G13d — validated over the wire against WireMock + its gRPC extension.
- **How the extension returns an error (learned from the oracle).** It reads two **response headers**:
  `grpc-status-name` (the status **code name**, e.g. `NOT_FOUND`/`INVALID_ARGUMENT`/`INTERNAL`) and the
  optional `grpc-status-reason` (the status **detail** message). The call then fails with that gRPC
  status and no message body — a `jsonBody`, if present, is **not** delivered. The **numeric**
  `grpc-status` header is *not* the mechanism (it surfaced as `UNKNOWN` "Application error"), so the
  extension keys off the **name**.
- **Mockifyr's handling.** The engine already carries response headers, so the gRPC middleware checks
  for `grpc-status-name`, maps the name to its `google.rpc.Code` number (the canonical 0–16 set), and
  writes that as the `grpc-status` trailer with `grpc-status-reason` as `grpc-message`, skipping the
  message frame. No new Core surface — the error is just a stub with those response headers.
- **Validation.** A `SayHello` stub with `grpc-status-name: NOT_FOUND` + `grpc-status-reason: "no such
  hero"` fails the call with the **same** status code (`NotFound`) and detail on the oracle and
  Mockifyr.
- **Regression case:** `G13dGrpcStatusTests.ErrorStatus_MatchesTheOracle`.

## Admin-managed gRPC stubs (G13e)

- **Group / item:** G13e — validated over the wire against WireMock + its gRPC extension.
- **The management path feeds gRPC serving.** A stub POSTed to `/__admin/mappings` at runtime (not
  loaded from a file) is served over gRPC on both sides — the admin/CQRS store and the gRPC hot path
  share state, the gRPC analogue of the G7a in-process check. A `Describe` stub added via the admin API
  serves the same reply (`summary`/`codes`) on the oracle and Mockifyr.
- **Learned from the oracle (why `reset` is *not* the test):** WireMock's `/__admin/reset` **reloads
  the file-backed mappings** rather than leaving the store empty, so a file-seeded stub keeps serving
  after a reset. Mockifyr's `reset` clears the store without a file reload, so a reset-clears assertion
  would diverge on file-seeded stubs — reset-reload parity is tracked separately and deferred. The
  admin-add path above sidesteps it (it adds, never resets).
- **Regression case:** `G13eGrpcAdminTests.AdminAddedStub_IsServedOverGrpc_MatchesTheOracle`.

## Single-message streaming (G13f)

- **Group / item:** G13f — validated over the wire against WireMock + its gRPC extension.
- **What the extension supports (from its docs).** Server streaming returns a **single** message; client
  streaming consumes a **single** message; **bidirectional** streaming is **not supported** (a WireMock 4
  follow-up). At the wire level a single-message stream is one request frame → one response frame — the
  same shape as unary — so the **unchanged codec + middleware serve it**; no gRPC-facade change was
  needed. Only the proto/descriptor gained `stream` RPCs.
- **Validation.** `ServerStream` (server-streaming) yields exactly `["server-stream-reply"]` and
  `ClientStream` (client-streaming, one request) yields `"client-stream-reply"` — identically on the
  oracle and Mockifyr, driven through the generated C# streaming clients. The `.dsc` was regenerated
  with the new `stream` RPCs.
- **Deferred (extension limitation):** multi-message streams and **bidirectional** streaming — the
  WireMock extension itself does not support them (no oracle), pending WireMock 4.
- **Regression case:** `G13fGrpcStreamingTests.SingleMessageStreaming_MatchesTheOracle`.


## Oracle warm-up (test infrastructure, 2026-07-29)

`G13dGrpcStatusTests` flaked in CI roughly one run in ten. The cause was not a divergence: the
oracle container is declared ready by an HTTP probe against `/__admin/mappings`, which says nothing
about the gRPC extension having loaded its descriptor and mappings. A call landing in that window
comes back `UNIMPLEMENTED` (or a transport error), and comparing that to Mockifyr'''s correct answer
fails a test that has found nothing.

The fix is in the test, not the assertion: the oracle call retries **only** while the status is one
of the startup shapes (`Unimplemented`/`Unavailable`/`Internal`), with a 30-second deadline. A
genuine mismatch still fails immediately — the retry cannot mask one, because a real divergence
produces a settled status, not a startup status.


## The oracle's warm-up has more shapes than the retry knew (post-1.14.0)

`G13dGrpcStatusTests` waits out the oracle container's gRPC warm-up before comparing statuses,
because the container's readiness probe is an HTTP call that says nothing about the gRPC extension
having loaded its descriptor. The retry treated `Unimplemented`, `Unavailable` and `Internal` as
startup shapes.

**`Cancelled` and `DeadlineExceeded` are startup shapes too.** On a loaded runner the handshake is
torn down mid-flight and the client reports the abort rather than the container's readiness — so the
loop returned immediately with `Cancelled`, compared it against Mockifyr's correct `NotFound`, and
failed.

It surfaced on a **`StackExchange.Redis` bump**, which touches no gRPC code whatsoever. That is the
tell worth keeping: a test that fails on a change it cannot possibly be affected by is reporting its
own environment, not the change. The fix widens the retry rather than lengthening a sleep; a genuine
divergence still fails, because the assertion runs either way once the deadline passes.


## The oracle has to be serving the stub, not merely listening (#367)

**What was reported red for the wrong reason.** A gRPC differential test failed once in CI with
`Unimplemented: Bad gRPC response. HTTP status code: 404` — a plain 404 from the oracle, which means
the request arrived before the mounted mapping was loaded, not that the two engines disagreed. The
same commit passed on a re-run and locally.

**Why that is worth fixing rather than re-running.** The differential suite is this project's
definition of done. A suite that goes red for a reason unrelated to the diff teaches everyone to
re-run first and read second, which is exactly how a genuine divergence gets waved through.

**Two preconditions, one of them previously unestablished.** The container wait gates on the plaintext
admin port; a poll added the HTTPS listener gRPC actually uses. Neither says the stub is *loaded* — it
is mounted as a file and read by the gRPC extension after the admin surface starts answering. Readiness
now asks the question the test is about to ask: does the admin surface list a mapping for each path this
oracle was built to serve.

**Every path, not the first.** A mapping file carrying two methods is loaded as a unit, but waiting for
one of them would leave the same race for the other while looking thorough.

**The paths are read from the mapping itself** rather than passed in beside it — two sources for one
fact is how a readiness probe ends up waiting for a path nobody serves and passing instantly. A mapping
naming no path is refused at construction for the same reason.

**Failure says what failed.** Exhausting the wait throws "the gRPC oracle never became ready for
'<path>' … <what was last seen>", so the next person reads that instead of working backwards from an
`Unimplemented` status inside an unrelated assertion.

**Not a retry and not a longer sleep.** A retry around a differential assertion could hide a genuine
flapping divergence; a sleep hides the signal either way.

**The HTTP oracle does not share the pattern.** It loads stubs through the admin API at test time, so
by the time a request is made the stub is demonstrably registered — the race exists only where a
mapping is mounted as a file and picked up asynchronously.
