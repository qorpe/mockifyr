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

### Deferred (the remaining slices of ADR 0013)

Capture (subscribe, land messages in the inbox), serve-on-consume (`brokerMappings`), and AMQP. None is
written; the ADR holds their shape.
