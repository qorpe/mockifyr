# The control layer: tenant clock, seeded chaos, contract conformance

## Tenant clock — testing time without waiting for it

Time-dependent behavior is famously untestable: "the token expires in an hour — does the
app refresh it?" Waiting an hour is absurd; changing the system clock breaks the machine.

Mockifyr gives each tenant a **virtual clock**:

```
PUT /__admin/clock  {"frozenAt": "2027-01-01T09:00:00Z"}   freeze at an instant
PUT /__admin/clock  {"offsetSeconds": 86400}               or shift by a delta
DELETE /__admin/clock                                       back to real time
```

Everything templated reads it — `{{now}}`, date helpers, minted JWTs, webhook templates.
The demo's `/api/token` stub answers `issuedAt`/`expiresAt` from `{{now}}`: freeze the clock
at 2027 and the same stub instantly issues 2027 tokens. Only that tenant is affected, and
the journal, audit trail and inbox deliberately keep **real** time — records must not lie.

## Degradation profiles — chaos as a regression test

`delay` and `fault` describe one stub. The question teams actually ask is: *what does my app
do when the whole dependency degrades?*

```
PUT /__admin/degradation
{ "latency": {"fixedMs": 300, "jitterMs": 200},
  "errorRate": {"ratio": 0.4, "status": 503},
  "seed": 42 }
```

The profile composes over **every stub of the tenant**: added latency, a percentage of
honest 503s, optional connection faults. Two design decisions make it an instrument rather
than a stunt:

- **Deterministic from a seed** (always reported back): the same seed produces the same
  sequence of failures — a chaos run that found a bug becomes a repeatable regression test.
- **The admin surface is never degraded** — a chaos you couldn't switch off would be a trap.
  `DELETE /__admin/degradation` always works.

## Conformance — three questions no mock usually answers

A mock that drifted from the contract *manufactures confidence*. Three reports, one shared
engine and one set of ambiguity rules (so two reports can never disagree about which
operation a path belongs to):

| Question | Call | Finds |
|---|---|---|
| Do my **stubs** match the contract? | `POST /__admin/openapi/verify` (body = the spec) | uncovered operations, undeclared stubs, schema violations |
| Has **reality** drifted from my stubs? | `POST /__admin/recordings/verify` (live session) | missing/unexpected fields, changed statuses — see [recording.md](recording.md) |
| Did the **clients** stay inside the contract? | `POST /__admin/requests/verify` (body = the spec) | undeclared operations, missing parameters, request-schema violations |

The third one deserves emphasis: a permissive mock answers whatever the client sends, so a
client that wandered off-contract still sees green tests — the bug surfaces in production.
This report replays the journal against the spec and lists every off-contract call.

All three are **reports, never mutations**: they serve nothing, advance nothing, change
nothing while they look.
