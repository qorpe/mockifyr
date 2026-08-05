# Parity notes — G4 Delay + fault injection

Verified WireMock delay/fault behaviors against the oracle (`wiremock/wiremock:3.10.0`). See
[README](README.md) for the format. These are **transport/timing** behaviors: the pure engine only
records a directive (`CanonicalResponse.Delay`/`.Fault`); a facade applies it.

## Response delay (G4)

- **Group / item:** G4 — validated against the oracle.
- **`fixedDelayMilliseconds`** delays the response by that many ms; the response **content is
  unchanged** (verified: `fixedDelayMilliseconds: 600` → the body/status/headers are identical to an
  undelayed stub, just ~600ms later).
- **How it's validated.** The delayed response's content still diffs green, **and** both sides take
  at least the requested delay. Only a **generous lower bound** is asserted (delay 400ms → both
  ≥ 300ms): a fixed delay can never make a response *faster*, so this is robust against CI timing
  noise while still catching a delay that isn't applied. Timing is measured by
  `DifferentialRunner.ProbeTimedAsync`.
- **Where it's applied.** The engine stays pure — it only puts a `DelayDirective` on the response.
  The **library facade** (`MockifyrServer.Handle`) applies the delay in-process (the HTTP facade will
  apply it over the wire at G12). See docs/decisions/0001.
- **Deferred:** `delayDistribution` (lognormal/uniform random delays) and `chunkedDribbleDelay`.
- **Regression cases:** `G4DelayTests.FixedDelay_ContentParityAndTiming`,
  `G4DirectiveParsingTests.FixedDelay_IsParsedOntoTheResponse`.

## Fault injection (G4 — parsed; emission deferred to G12)

- **`fault`** is a **socket-level** behavior, so it **cannot be diffed through the in-process
  harness** (which drives the engine, not a socket). Probed against the oracle over HTTP:
  - `EMPTY_RESPONSE` → the connection is closed with no response (`curl` sees HTTP 000).
  - `MALFORMED_RESPONSE_CHUNK` → a 200 status line followed by garbage, then close.
  - `RANDOM_DATA_THEN_CLOSE` → random bytes, then close.
  - `CONNECTION_RESET_BY_PEER` → the connection is reset.
- **What G4 does now.** The adapter parses `fault` into a `FaultDirective(FaultKind)` on the response
  (all four kinds), so the directive is recorded and unit-tested. **Emitting the socket behavior and
  validating it belong to the HTTP facade (G12)** — there is no transport to produce or observe it
  in-process yet.
- **Regression case:** `G4DirectiveParsingTests.Fault_IsParsedOntoTheResponse` (all four kinds).


## Tenant degradation profiles (#289, post-1.0)

- **Group / item:** post-roadmap platform feature — **self-tested**; no oracle exists (the reference
  engine has no tenant-wide degradation), so the validation method is stated here per the G18 rule.
- **The problem.** `delay` and `fault` are per-stub directives: the right shape for "this endpoint is
  slow", the wrong one for the question integration teams actually ask — *what does my system do when
  this whole dependency degrades?* Answering it meant editing every stub in the tenant and then editing
  them all back, so nobody did, and the resilience test never happened.
- **Shape.** `PUT /__admin/degradation` with `latency` (`fixedMs` + `jitterMs`), `errorRate`
  (`ratio` + `status`) and `faultRate` (`ratio` + one of the four dialect fault names); `GET` reads it
  back, `DELETE` restores full health in one call — a drill has to be bounded or it becomes a cleanup
  project nobody finishes.
- **It composes, it does not replace.** A stub asking for 200 ms still gets 200 ms, plus whatever the
  dependency is adding today. Asserted on the wire.
- **A broken connection outranks an error status.** With both gates open the fault wins every time: a
  dependency that resets the connection does not first politely explain itself with a 503.
- **Latency still applies to a request that then fails**, because a degraded dependency is usually slow
  *and* failing — answering instantly with a 503 would exercise a client's timeout handling less than
  the real thing does.
- **The admin API is never degraded.** The profile is applied in the mock-serving endpoint only. If it
  reached the control plane an operator could degrade a tenant and then be unable to un-degrade it —
  the profile would be a trap rather than an instrument. There is a test.
- **Deterministic, and the seed is a promise.** The outcome is a pure function of the seed and the
  request's ordinal, which is what turns a chaos experiment into a regression test. A seed is *always*
  stored and reported: nobody supplies one until a run turns up something interesting, by which time it
  is too late to start recording. The generated sequence is pinned by a golden test, because a recorded
  seed that replayed something different on a newer build would be worse than no seed at all.
- **Ordinals, not a shared `Random`.** A per-tenant `Interlocked` counter is thread-safe without a lock
  on the hot path. Under concurrency the ordinals are still handed out in arrival order; what varies is
  which request gets which ordinal, not what ordinal *n* receives.
- **Validation.** `DegradationPlanTests` (18 unit cases: rates asserted over 10 000 samples to within
  one percentage point — a generator merely "roughly" right would make a 5% profile indistinguishable
  from a 7% one, which is the difference being measured; determinism; the golden; precedence; far
  ordinals) and `DegradationProfileTests` (15 wire cases: the error status reaching the wire, measured
  latency, a real transport failure, the admin API staying up, tenant isolation, clearing, the reported
  seed, replay, composition with a stub's own delay, five refusals, and an empty profile reading as
  healthy).
- **Stryker: 12 survivors, all analysed as equivalent** — and measured rather than asserted:
  - `>>` → `>>>` (four): identical operations on `ulong` by language definition.
  - `> 0` → `>= 0` on the two delay guards and the two ratio guards (four): at zero both branches
    produce the same value.
  - `<` → `<=` on the two ratio comparisons (two): they differ only when a draw is *exactly* the ratio,
    a measure-zero event on a 53-bit uniform.
  - `z ^= z >> 31` → `z |= z >> 31` (one): changes the raw generator state in **199 635 of 200 000**
    states, and changes the resulting decision in **none** of 32 consecutive ordinals — the difference
    lives below the precision anything observable uses (whole milliseconds, and comparisons against a
    ratio). Two generators that differ only under the observable precision are equivalent for this
    contract. Measured, not assumed.
  - The `IsHealthy` early return (one): a fast path; a healthy profile computes to the same decision
    without it.
