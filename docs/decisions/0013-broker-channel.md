# 0013 — The broker channel: messages that arrive on a topic, not on a socket

## Status

Proposed — 2026-08-05. Supersedes nothing. Extends ADR 0009 (message mocking) rather than replacing
it, and follows ADR 0010's rule that a channel gets its own editing surface rather than being bent
into the HTTP one.

## Context

Mockifyr calls itself an enterprise API mock and **integration sandbox** platform. It covers four
synchronous surfaces (HTTP, gRPC, GraphQL, WebSocket) and two asynchronous ones that arrive over the
network as requests (SMTP, SMS). The channel most enterprise integrations actually run on — a message
broker — is absent.

The practical shape of the gap: a team can mock the REST call that *starts* a payment, and cannot mock
the event that reports it *settled*. The half they cannot mock is the half that is hardest to test,
because it is the half with no synchronous reply to assert on.

Three things make this worth an ADR rather than a ticket:

1. **A broker message is not a request.** It has no method, no URL, no status. Every existing matching
   and response concept assumes those, and forcing a message into them would be the transport leaking
   into the model — exactly what ADR 0001 exists to prevent.
2. **Delivery has semantics a mock cannot ignore.** Offsets, consumer groups, redelivery, ordering.
   Getting these wrong does not produce a wrong response; it produces a mock that appears to work and
   silently loses or repeats messages, which is worse.
3. **It is a group, not a feature.** Produce-on-match, serve-on-consume and capture are three verticals
   that can ship separately, and deciding their shape up front is what stops the second one
   contradicting the first.

### What already exists that this must reuse

- `MessageEnvelope` / `IMessageStore` / `IMessageSink` (ADR 0009) — a message is already a domain
  value in Core, with a tenant-scoped bounded inbox and a verify surface at `/__admin/messages`.
- `message-mappings` (G15d, WebSocket) — a trigger of body matchers plus templated sends. The closest
  existing precedent for "an inbound message produces outbound messages".
- `IServeEventListener` (G3) — the seam webhooks use for outbound I/O, which is what publishing is.
- The G16 persistence seam, the G17 environments, the tenant clock (#290) — all of which a broker
  vertical should inherit rather than reinvent.

## Decision

### A broker message is a `MessageEnvelope` with a new channel, not a new model

`MessageChannel` gains `Broker`. Topic, partition key, and any broker-specific fields go in the
existing `Meta` map — the same place ADR 0009 already puts provider fields like Twilio's
`MessagingServiceSid`.

The alternative — a separate `BrokerMessage` type with its own store — was rejected because it would
split the inbox, the verify surface, the dashboard screen and the persistence seam into two of each,
to model something that is a body plus metadata either way. One inbox means "assert we emitted
`OrderSettled`" is the call people already know.

The cost is honest and small: `Subject` and `HtmlBody` stay null on broker messages, exactly as they do
on SMS today.

### Matching gets its own dialect section, in the shape of `message-mappings`

A broker stub is **not** a `request`/`response` pair. The dialect gains:

```json
{
  "brokerMappings": [
    {
      "whenTopic": { "equalTo": "orders.commands" },
      "whenMessage": [{ "matchesJsonPath": "$.type" }],
      "whenHeaders": { "correlation-id": { "matches": ".+" } },
      "publish": [
        { "topic": "orders.events", "key": "{{jsonPath message.body '$.orderId'}}",
          "body": "{\"type\":\"OrderSettled\",\"orderId\":\"{{jsonPath message.body '$.orderId'}}\"}" }
      ]
    }
  ]
}
```

- `whenTopic` / `whenHeaders` reuse the standard value matchers; `whenMessage` reuses the body
  matchers. Nothing new is invented for matching, so `equalToJson`, `matchesJsonPath` and the rest
  behave exactly as they do on the HTTP side — including their oracle-verified semantics.
- `publish` reuses the templating engine, with the inbound message exposed as `message.*` the way
  WebSocket exposes it today.
- An empty trigger matches every message on the subscribed topics, mirroring `message-mappings`.

### Producing is a serve-event listener, not new outbound machinery

An HTTP stub may also declare `publish` in a `postServeActions` entry, exactly beside `webhook`. The
implementation is an `IServeEventListener` pointed at a broker instead of at HTTP, so Core never learns
what a broker is and the existing correlation, retry and sub-event recording apply unchanged.

This is what makes the headline case work: a `POST /payments` stub that answers 201 **and** emits
`PaymentAccepted` on a topic.

### Capture is the existing inbox

Messages the system under test publishes to a subscribed topic land in the tenant's inbox with
`channel: broker`, so `/__admin/messages` and its verify/count surface answer for them with no new
API. The dashboard's Messages screen gains a channel filter rather than a new page.

### Delivery semantics are stated, not inherited by accident

- **One consumer group per host**, named from the tenant and configurable. Two Mockifyr replicas
  therefore share a subscription rather than each consuming every message, which is what an operator
  scaling out expects.
- **Offsets commit after the stub set has been evaluated and any `publish` dispatched.** A message that
  crashes the host is redelivered rather than silently dropped — at-least-once, stated.
- **Ordering is per partition**, inherited from the broker; Mockifyr adds no ordering guarantee of its
  own and says so.
- **A message matching no mapping is captured and acknowledged**, not parked. A mock that stalls a
  partition because somebody forgot a stub would be a worse failure than an unmatched HTTP request's
  404.

### Kafka first, AMQP second, behind one facade contract

Kafka is what most enterprise teams mean by "the broker", and it is the harder shape (partitions,
consumer groups, offsets) — designing for it first means AMQP fits inside rather than the reverse.

`Mockifyr.Facade.Broker` owns the client dependency, exactly as `Mockifyr.Facade.Smtp` owns MimeKit.
Core gains only `MessageChannel.Broker` and the mapping model. Nothing is connected unless a flag
configures it (`--kafka-bootstrap`), and a host without one must not change behaviour, startup cost, or
image size in any way an operator can measure.

### Validation follows the G18 precedent

No oracle exists — the reference engine has no broker concept — so the standing honesty rule applies
and `docs/parity/` will say so per vertical. The method is the one G16 used for databases: a **real
broker in a Testcontainer**, driven by the official client, asserting what a consumer actually
receives. Pure logic (trigger evaluation, topic matching, the publish plan) is unit-tested and
mutation-tested to the usual bar.

## Consequences

**Good.**

- The integration sandbox stops being HTTP-shaped. "Given this command, my system emits that event" is
  testable in the same host, with the same tenants, environments and clock as everything else.
- Verify already exists: capture lands in the inbox people already query.
- The matching vocabulary is the one already proven differentially. A broker stub is new syntax around
  old, oracle-verified semantics.

**Costs, accepted deliberately.**

- **A broker dependency is heavy.** The facade project carries a client library that a host not using
  brokers still ships. Measure it; if the image grows more than a few megabytes, split the facade into
  its own optional image layer before shipping, not after.
- **Testcontainers for Kafka is slow.** The differential suite already takes minutes; a broker suite
  will add more, and it belongs behind the same Docker-required gate.
- **`MessageEnvelope` grows a channel whose fields do not all apply.** Accepted, per above.
- **Consumer-group semantics leak into an operator's mental model.** Two replicas sharing a
  subscription is right, and it is also a thing to learn. It goes in the guide, not in a footnote.

**What this ADR does not decide.**

- Schema registries (Avro/Protobuf payloads). The mapping model treats a body as bytes plus text, and a
  registry-aware codec is a later decision with its own trade-offs.
- Transactions and exactly-once. Out of scope; the honest position is at-least-once, stated above.
- Whether the SMTP and SMS facades should eventually be re-expressed on top of this channel. They work;
  rewriting them for symmetry alone would be churn.

## Slices

Each is shippable on its own, in this order:

1. **Produce on match** — `publish` as a post-serve action on HTTP stubs. Delivers the headline case,
   touches no consume path, and proves the facade + config + Testcontainer harness.
2. **Capture** — subscribe to topics, land messages in the inbox, filter by channel in the dashboard.
   Delivers verification, and is where consumer-group and offset decisions get tested.
3. **Serve on consume** — `brokerMappings`: an inbound message matches and produces outbound ones.
   The largest slice, and the one that needs the other two working first.
4. **AMQP** behind the same contract, once the shape has survived contact with slice 3.
