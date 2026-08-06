# G21 — The broker channel (ADR 0013)

No oracle exists for any surface here — the reference engine has no broker concept — so per the
standing rule (G18 precedent) the validation method is stated up front: unit tests over the pure
parsing and delivery logic, integration tests against a **real Kafka in a container** driven by the
official client, and the existing differential suites staying green untouched, which is the proof that
this vertical does not move the parity surface.

## Slice 1 — publish on match

- **Group / item:** G21a — self-tested; a stub answers a request *and* emits the event the rest of the
  system is waiting for.
- **The gap it closes.** A team could mock the REST call that starts a payment and not the event that
  reports it settled. The half that could not be mocked is the half that is hardest to test, because it
  has no synchronous reply to assert on.
- **Shape.** A `publish` post-serve action beside `webhook`, read from both `postServeActions` and
  `serveEventListeners` for the same reason webhooks are (#147):

  ```json
  {"postServeActions":[{"name":"publish","parameters":{
     "topic":"payments.events",
     "key":"{{jsonPath originalRequest.body '$.orderId'}}",
     "body":"{\"type\":\"PaymentAccepted\"}",
     "headers":{"correlation-id":"{{originalRequest.headers.X-Correlation-Id}}"}}}]}
  ```

- **Every field is templated** against the triggering request, which is what makes a partition key
  taken from the request body — the thing that makes per-entity ordering work — the obvious thing to
  write rather than a special case.
- **Core never learns what a broker is.** The engine records the intent on the stub; the I/O is an
  `IServeEventListener` in `Mockifyr.Facade.Broker`, exactly as ADR 0001 requires and exactly as the
  webhook listener already does it.
- **A publish with no topic is not a publish.** Accepting the action and dropping the message at
  delivery time would make a typo look like a working stub.
- **An unreachable broker never takes the response down with it.** The client already has its answer by
  the time the listener runs; the failure is recorded. Verified by running against a dead port and
  asserting both the served body and the recorded failure.
- **The journal shows what was published — and this was found by testing, not by design.** The listener
  recorded sub-events from the first version, but the journal detail endpoint only ever projected
  *webhook* sub-events, so "the failure belongs in the journal" was not true until the endpoint gained
  a `publishes` array beside `webhooks`. Kept separate rather than merged: somebody debugging "did the
  event go out?" is asking about a different system than "did the callback land?".

### Delivery decisions worth remembering

- **One producer for the host's lifetime**, flushed on shutdown. A producer per message would turn
  every publish into a connection handshake, and an unflushed buffer would lose exactly the events a
  test suite was about to assert on.
- **`Acks = Leader`, not `All`.** A mock is not a system of record; waiting for every replica would add
  latency to a test suite for a durability guarantee nobody here is relying on. Stated rather than
  defaulted into.
- **A five-second message timeout**, so a misconfigured broker surfaces as a recorded failure rather
  than a request that hangs.

### The image-size trigger fired, and the split was still not taken

ADR 0013 said to measure before shipping and split the facade into its own image if growth exceeded a
few megabytes. Measured: **+64 MB** across a multi-RID publish, but only **+20 MB** in the image the
Dockerfile actually produces (536 → 556 MB), because it cross-publishes for the target architecture.
`librdkafka.so` (8.3 MB) is present and working — verified by running the built image and watching a
publish fail with a real client error rather than a missing-library crash.

+20 MB is more than "a few", so the trigger fired. The judgement changed once the number was real, and
the ADR records why: 3.7 % on an already-large image does not justify two images to build, sign,
verify, document and choose between. Revisit when AMQP adds a second client or the image crosses
600 MB.

### Validation

- `PublishActionTests` (12 unit cases): the headline answer-and-emit, templating of every field,
  several publishes in order, a publish sitting *beside* a webhook without either consuming the other,
  both action-array spellings, a topicless action being no action, a delivery failure recorded rather
  than thrown, and an unrenderable template recorded rather than thrown.
- `BrokerPublishTests` (5 integration cases) against **`confluentinc/cp-kafka:7.6.1`**, consumed with
  the official client: a real consumer that knows nothing about Mockifyr receives the message, with the
  right key and headers; a stub declaring no publish emits nothing; several topics all arrive; the
  delivery is visible in the journal; and a dead broker still serves the response.
- Two test defects were found and fixed while writing them, both worth recording because they would
  have made the suite lie: a consumer subscribing before the topic exists throws "unknown topic"
  instead of waiting (so it polls), and one Kafka shared across the class lets one test read another's
  message (so each test uses its own topic).

## Slice 2 — capture

- **Group / item:** G21b — self-tested; messages the system under test publishes land in the tenant's
  inbox, so "assert we emitted `OrderSettled`" is one call against the surface people already query.
- **Shape.** `--kafka-subscribe topic-a,topic-b` (and optional `--kafka-group`). Capture is separate
  from publishing on purpose: a host that publishes but subscribes to nothing starts no consumer and
  joins no group.
- **One inbox, as ADR 0013 decided.** A captured message is a `MessageEnvelope` with
  `MessageChannel.Broker`; `/__admin/messages` and its count/verify surface answer for it with no new
  API. Asserted by a wire test that counts broker messages through the endpoint that already existed.
- **What the envelope carries.** The **topic stands in for the sender** — it is what somebody scanning
  the inbox is looking for, and leaving `From` empty would make every broker row look identical. There
  are **no recipients**: a published message is addressed to a topic, not to anybody, and inventing a
  consumer group there would suggest a delivery guarantee the inbox does not make. Topic, partition,
  offset and key go in `Meta`, which is what turns "a message arrived" into "this exact one did".
- **Producer headers are prefixed** (`header.*`) so a producer cannot overwrite `topic` or `offset`
  with a header of the same name. Those three have to be trustworthy rather than usually right.
- **Tenancy.** A topic carries none of its own, so an `X-Mockifyr-Tenant` message header addresses one
  and its absence lands in the default tenant — the same chain shape every other channel uses
  (ADR 0003/0009). Matched case-insensitively, because header names are; the first wins when a producer
  sets two, because Kafka allows repeats and picking deterministically beats picking whichever the
  client enumerated last.
- **Offsets commit after the inbox write, never before.** A host that crashed in between would
  otherwise have acknowledged a message nobody can see. At-least-once, as the ADR states, with
  redelivery preferred over silent loss.
- **A dedicated thread, not the pool.** `Consume()` blocks; parking a pool thread on it for the host's
  lifetime is the pattern that starves everything else.
- **A found bug, before it shipped.** The admin API projected a message's channel with a two-way
  ternary (`Email ? "email" : "sms"`), so every broker message would have been labelled **"sms"** the
  moment a third channel existed. Now a switch, with a wire test asserting it.
- **Validation.** `BrokerCaptureTests` (13 unit cases over the pure factory — tenancy, meta, the header
  prefix, a null payload stored as an empty body rather than pushed onward, a keyless message carrying
  no key rather than an empty one) and `BrokerCaptureWireTests` (6 integration cases producing with the
  **official client** against a real broker: capture, the channel label, provenance surviving into the
  inbox, tenant addressing, verification through the existing count endpoint, and a host that
  subscribes to nothing capturing nothing). **Stryker: 100 %** on `BrokerMessageFactory`.

## Two silent gaps, found by running the released image (1.10.1)

Both were found the same way: pulling `ghcr.io/qorpe/mockifyr:1.10.0` and driving the documented flow
by hand. Neither was a failing test, because neither was wrong — they were quiet, which the release
before them had already established is the failure mode this repo cares about most.

- **A `publish` action on a host with no broker did nothing, and said nothing.** No producer is built
  without `--kafka-bootstrap`, which is the correct posture — but the stub was still accepted, still
  served its 201, and emitted nothing at all, with no warning at import and no record in the journal.
  That is indistinguishable from a broker outage, and the flag is the last place anybody would look.
  It is exactly the shape of the `bodyFileName` and `delayDistribution` gaps 1.0 made loud, so it goes
  through the same surface: `UnsupportedFieldWarnings` now takes whether the host has a publisher, and
  reports the gap on `POST /__admin/mappings`, on import, and at startup for mappings loaded from disk.
  The question is asked of the **container** (`IBrokerPublisher` registered?) rather than of
  configuration, so the answer cannot drift from what actually does the work.
  The default is "a broker exists", because every caller that knows better passes the answer — a
  default that warned would tell an in-process library user about a flag they cannot pass.
- **A failed publish recorded that it failed, not what it was carrying.** The journal showed
  `{"topic": …, "key": null, "body": null, "delivered": false, "error": "Local: Message timed out"}` —
  the nulls were structural, because `PublishErrorData` had no room for them. "Delivery failed" and
  "delivery failed, and here is the body whose template you got wrong" are the difference between
  knowing something is broken and knowing what. The rendered key and body now ride on the failure.
  Nulls survive for one case only, and there they are a fact: rendering is what failed, so there was
  never a message — recording an empty body would claim we tried to send one.

**Validation.** 11 unit cases over the warning (a webhook not mistaken for a publish, the action name
matched case-insensitively, a host with no broker saying nothing about a stub that does not publish,
four malformed `postServeActions` shapes producing no warning rather than throwing, and a publishing
stub that *also* has a deferred field reporting both — which pins that neither check's early return can
swallow the other), two wire cases over a real host with and without the flag, and two over the failure
record. **Stryker: 98.08 %** on `UnsupportedFieldWarnings`, and it earned its keep again — it found
that the `(N stubs)` suffix's suppression for a single stub was asserted nowhere, so `"(1 stubs)"`
would have shipped unnoticed.

The one survivor is equivalent: the warning's *kind* key `"publish:no-broker"` mutated to `""` still
groups correctly, because a kind key only ever collides with itself and no other kind is empty. It is
equivalent to the current set of kinds rather than in principle — a future warning keyed `""` would
merge with it — which is exactly why the key is a descriptive constant and not an empty string.

The general lesson, third time it has paid: **the released artifact is a test surface**. Building it,
signing it and having a green suite says the code does what the tests say; running it says what an
operator sees.

## Slice 3 — serve on consume

- **Group / item:** G21c — self-tested; an inbound message matches a mapping and produces outbound
  ones, which is what turns "I can emit an event" into "I can stand in for an event-driven component".
- **Shape.** `brokerMappings`, registered at `POST /__admin/broker-mappings` and listed, deleted and
  reset there, in the shape ADR 0013 named:

  ```json
  {"whenTopic":{"equalTo":"orders.commands"},
   "whenMessage":[{"matchesJsonPath":{"expression":"$.type","equalTo":"SettleOrder"}}],
   "whenHeaders":{"source":{"equalTo":"erp"}},
   "publish":[{"topic":"orders.events","key":"{{jsonPath message.body '$.orderId'}}","body":"…"}]}
  ```

- **Nothing new was invented for matching, and that is the point.** `whenTopic` and `whenHeaders` are
  read through the request-pattern reader as header matchers, `whenMessage` as body matchers. So
  `equalTo`, `matches`, `contains`, `equalToJson`, `matchesJsonPath`, `equalToXml` and the rest arrive
  with the semantics the oracle already pinned on the HTTP side — a broker stub is **new syntax around
  old, verified behaviour**, and a matcher added to the dialect tomorrow works here the day it lands.
  The adapter is a purpose-built `CanonicalRequest` (topic as a reserved pseudo-header, message headers
  as headers, payload as body) whose method and URL are placeholders no matcher on this path reads.
- **Every matching mapping contributes, not just the first.** This is the one place the broker channel
  departs from HTTP serving on purpose: a fan-out — one command producing an event *and* an audit
  record from two separate stubs — is a real broker pattern, and first-match-wins would make it
  inexpressible without merging unrelated mappings. HTTP can send one response; a broker can emit any
  number.
- **A message's reply can name where it came from.** Templates see `message.body`, `message.topic`,
  `message.key` and `message.headers.<name>`. Without the last three the correlation has to be
  hand-carried into every stub's body, which is the kind of thing people get subtly wrong once and then
  everywhere. Destination topics are templated too, so content-based routing is one mapping rather than
  one per destination.
- **Environments and the tenant clock apply.** `MessageTemplateRenderer` gained optional
  `IEnvironmentResolver`/`IClockResolver` — a broker reply resolving `{{key}}` differently from an HTTP
  response in the same tenant would be a puzzle, not a feature. Both are optional, so the WebSocket
  facade that constructs it with neither behaves exactly as before.
- **Ordering with capture.** Captured **first**, served **second**, offset committed **after both**. A
  message that produced a reply must still be assertable afterwards, or debugging a mapping means
  guessing what arrived; and the ADR's at-least-once statement needs the commit to be last.
- **A broken template drops its own message and no other**, and is recorded in a bounded failure log
  rather than swallowed. A typo in an audit stub must not stop the event the system under test is
  waiting for — and `publish` shipped silent once already (1.10.1), which is why "recorded" is not
  optional here.
- **A message matching nothing is acknowledged, not parked**, per the ADR. Asserted by producing an
  unmatched message *before* a matched one: if the first stalled the partition the second would never
  be served.
- **Publishing the reply is synchronous inside the consume loop.** A fire-and-forget send would let the
  offset commit while the reply was still in a producer buffer. Nothing is serialised that was not
  already — the consumer handles one message at a time and ordering is per partition.
- **Validation.** 31 unit cases over the pure model and planner (trigger composition, topic as a value
  matcher, fan-out, tenant isolation, tombstones surviving planning, six wrong-shaped registration
  fields ignored rather than throwing) and 7 integration cases against a **real Kafka container** whose
  replies are read back with the **official client** — because the question is not "did we plan a
  message" but "did the system under test receive one". **Stryker: 89.80 %** on `BrokerMapping` +
  `BrokerMappingPlanner`; it found three real coverage gaps first — a wrong-typed `key`, a non-object
  `headers`, and a tombstone whose null body was pinned at read time but not through planning.

  Three survivors, all equivalent: the `"MESSAGE"` and `"/"` placeholders in the synthetic request
  (no matcher on this path can read a method or URL — that is why they are placeholders), and
  `First()` → `FirstOrDefault()` over a `GroupBy` group, which is never empty.

## Slice 4 — AMQP

- **Group / item:** G21d — self-tested; the second transport behind the same `IBrokerPublisher`,
  with `--amqp-uri` and `--amqp-subscribe`.
- **The design bet paid off, and that is the finding.** ADR 0013 said to build for Kafka first
  *because* it is the harder shape — partitions, consumer groups, offsets — so that AMQP would fit
  inside rather than the reverse. It did: `AmqpPublisher` implements the existing contract unchanged,
  and the mappings, templates, matchers, inbox, tenancy and admin routes above it needed **no
  transport-specific code at all**. The slice is a publisher, a consumer and a router.
- **Two translations had to be stated, because AMQP lacks the concepts the dialect was written
  against.** Both are the kind of thing that is a silent gap if assumed:
  - **A topic is not an AMQP concept.** `"topic": "exchange/routing.key"` addresses an exchange; a
    topic with no slash uses the **default exchange** with the topic as the routing key, which
    delivers straight to a queue of that name. `{"topic":"orders.events"}` therefore means the obvious
    thing on both transports. Only the first slash splits — a slash is legal inside a routing key, and
    losing part of one would route a message somewhere quietly wrong.
  - **A partition key has no AMQP counterpart.** `key` becomes the message's `MessageId` — the closest
    standard property, and one a consumer can read. Silently dropping it was the alternative, and is
    exactly the shape of gap 1.10.1 exists to punish.
- **The queue stands in for the topic on the way in**, so one `whenTopic` matcher works on both
  transports, and the delivery tag stands in for the offset — the same kind of fact about where a
  message sits in what the consumer has been handed.
- **AMQP header values are typed**, and the client hands byte arrays for strings. They are decoded on
  capture; storing them raw would ask a matcher to match against `System.Byte[]`.
- **`prefetchCount: 1` and manual ack.** Ordering within a queue is the only ordering AMQP offers, and
  a prefetch window would let a later message be served before an earlier one. The ordering guarantee
  is the same as Kafka's: **capture, serve, then acknowledge**.
- **Queues are declared on connect, and a failed connection retries.** A mock that required the system
  under test to have created its queues first would make test ordering a deployment concern, and a
  capture loop that gave up once would need a restart to recover from a broker that was merely slow to
  start.
- **Two transports on one host.** A topic can name one with a `kafka:` or `amqp:` prefix; an
  unprefixed topic goes to Kafka. A prefix rather than a `broker` field, because a field would have to
  be added to the mapping model in Core, to the mapping-JSON reader and to both publish actions —
  four places, to express what is part of the destination. Kafka topic names cannot contain a colon,
  so nothing legal is shadowed, and a host with one transport never meets the convention. A prefix
  naming a transport the host does not have falls back rather than failing, so a mapping stays
  portable between a Kafka-only and an AMQP-only host.
- **Image size: the ADR's own trigger fired and the measurement overruled it.** `RabbitMQ.Client` is
  **0.33 MB**, pure managed, no native library for any platform — three orders of magnitude below the
  Kafka client and invisible against a 556 MB image. Recorded in ADR 0013 rather than left as a
  deviation.
- **Validation.** 12 unit cases over the pure decisions (the topic split, including only-first-slash,
  leading and trailing slashes; routing with one and two transports; a prefix stripped before the
  transport sees it; a topic that merely *looks* like a prefix left alone) and 8 integration cases
  against a **real RabbitMQ container** driven by the official client — publish from an HTTP stub,
  capture into the inbox, serve on consume with the slice-3 mapping shape unchanged, an unmatched
  message not parking the queue, tenant addressing, an exchange-and-routing-key topic reaching a bound
  queue, an unreachable broker recorded without taking the response down, and a `publish` action no
  longer warning on an AMQP-only host.

  **Stryker: 6 of 7 tested mutants** killed on `BrokerRouter`. The survivor is provably equivalent:
  `_kafka ?? _fallback` where `_fallback = _kafka ?? _amqp` is *always* `_fallback`, so the `kafka:`
  prefix cannot change a destination today — it is documentation that becomes load-bearing the moment
  a third transport exists. The AMQP publisher's connection handling is not mutation-tested, because
  mutating an I/O wrapper from a unit project measures the mocks; the container suite is what proves
  it.

**G21 is complete.**

## A filter that filtered nothing, for two releases (1.13.0)

`GET /__admin/messages?channel=broker` returned **every** message in the inbox, and
`/count?channel=broker` counted them all. The parser mapped `email` and `sms` and fell through to
`null` — "no filter" — for anything else, so a channel added to the model and forgotten here filters
nothing *while looking like it filtered*.

The interesting part is why the tests said it was fine. `Verification_works_on_captured_messages_with_no_new_api`
counted broker messages in an inbox holding **only** broker messages — where "filtered correctly" and
"did not filter at all" give the identical answer. It was a green test asserting nothing.

The replacement puts a real second channel in the inbox (the Twilio profile, one HTTP call) and
asserts all four counts plus that the list and the count agree. It fails against the old parser —
verified by reverting the fix and watching it go red — which is the only evidence that a regression
test is a regression test.

**The rule this leaves behind:** a filter test on a single-valued collection tests nothing. The same
shape found the two-way channel ternary in the admin API during slice 2; this is the third time a
"there are only two of these" assumption has cost something in this group.

### Not here, and said so

AMQP is the last transport ADR 0013 planned. Still out: **schema registries** (Avro/Protobuf — the
mapping model treats a body as text), **transactions and exactly-once** (the honest position is
at-least-once, stated), and a **dashboard surface** — the Messages screen shows broker messages
because it reads the same inbox, but has no channel filter, and broker mappings are API-only.
