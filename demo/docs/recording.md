# Recording: turn the real API into a mock

## The mental model: a reverse proxy with a tape deck

While a recording session is live, Mockifyr behaves as a **recording reverse proxy**:

```
your app  ──request──▶  Mockifyr (8080)  ──same request──▶  the real API (9090)
          ◀─response──  (keeps a copy)   ◀────response─────
```

Your app talks to Mockifyr's address; every request is forwarded verbatim (method,
path+query, body, headers minus transport ones), the upstream's response is relayed
byte-for-byte — and each request/response pair is written to the tape.

Three rules that surprise people:

1. **While recording, existing stubs go silent** — everything is proxied so the tape
   captures pure reality, never a mix of real and mocked answers. Stubs answer again after stop.
2. **The session is per-tenant** — start, drive, snapshot, verify and stop all act on the
   tenant you're in. Start it in the right tenant.
3. **Never point the target at the same instance** — that's a proxy loop.

## The concrete walk (the demo's Act 7)

```
1  Start:    target http://localhost:9090            (the "real" billing API)
2  Drive:    GET 8080/billing/invoices/INV-2041  →  answered BY the upstream
3  Snapshot: every taped pair becomes a stub:
             { "request":  { "method": "GET", "url": "/billing/invoices/INV-2041" },
               "response": { "status": 200, "body": "{\"id\":\"INV-2041\",\"currency\":\"EUR\",…}" } }
4  Import:   the stubs join the tenant — zero hand-written mocks
5  Stop:     stubs answer from now on; the real API can leave the stage
```

Repeats become **scenario chains**: record the same URL three times with different answers
and the snapshot emits three chained stubs that replay in order — the real API's behavior
*over time* is captured too.

In practice: point your app's config at Mockifyr for one afternoon, run your normal test
suite once (whatever the app calls gets taped), snapshot, import — and from then on those
tests run without the real API, in milliseconds, in CI.

## Drift: is my copy still telling the truth?

A recording is a snapshot of reality — and reality moves. Months later the real API drops a
field, adds another, nobody tells you. Your mock keeps answering the old shape: builds stay
green, production breaks. That is the worst property a mock can have.

With a session live, ask:

```
POST /__admin/recordings/verify
```

It compares what the upstream **just returned** against what your stubs **would answer** —
structurally, never literally (ids and timestamps produce no noise):

```json
{ "agrees": false, "findings": [
  { "kind": "fieldMissing",    "pointer": "/settlementBatch",
    "detail": "the upstream returns this field and the stub does not." },
  { "kind": "fieldUnexpected", "pointer": "/currency",
    "detail": "the stub returns this field and the upstream does not." } ] }
```

It serves nothing and mutates nothing while it looks. One operational rule: **verify before
stop** — stopping clears the taped exchanges, so a post-stop verify is an empty green.

## Related: permanent proxy stubs

Independent of recording, a stub can carry `response.proxyBaseUrl` — that stub then always
forwards its matches to the upstream. The hybrid pattern: mock the three endpoints you care
about, proxy the rest to the real environment.
