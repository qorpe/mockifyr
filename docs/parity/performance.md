# Performance envelope (#249)

Published figures, how they were produced, and what they mean for sizing. Two harnesses, because a
regression in one is invisible in the other: [`bench/`](../../bench/README.md) has the runnable
versions.

Every number below is **measured**, not estimated. Where a number is bad, it is printed as measured
and linked to the issue tracking it — a performance document that only publishes flattering figures
is marketing, not engineering.

## Engine benchmarks

What one request costs **inside the engine**: matching, templating, the journal. No Kestrel, no
network, no client — those are the load harness's job, and mixing them in would hide an engine
regression under transport noise.

**Machine:** Apple M5, 10 cores, 24 GB, macOS 26.5.2, .NET 10.0.101, Release, server GC.
**Store:** 1000 stubs. **Date:** 2026-07-30, after template caching (#266).

| Case | Mean | Allocated | vs. baseline |
|------|-----:|----------:|-------------:|
| `Match` — one stub, method + path | **378 ns** | 1.14 KB | 1.0× |
| `MatchWithJournal` — the same, journal on | **491 ns** | 1.14 KB | 1.3× |
| `MatchJsonBody` — structural `equalToJson` | **686 ns** | 1.80 KB | 1.8× |
| `MatchAndRenderTemplate` — templated response | **1.21 µs** | 4.55 KB | 3.2× |
| `MatchAmongManyStubs` — 1000 stubs, matching the **last** | **29.1 µs** | 94.8 KB | 77× |
| `MatchAndRenderLargeBody` — 256 KiB templated body | **262 µs** | 2.49 MB | 693× |

Before template caching landed, `MatchAndRenderTemplate` was **699 µs / 88.9 KB** — the Handlebars
template was recompiled on every request. Publishing that number is what got it fixed; the history is
kept here because a performance document that only ever shows good numbers is not evidence of
anything.

Reproduce with `dotnet run --project bench/Mockifyr.Benchmarks -c Release -- --filter '*'`.

### What these say

- **Matching is cheap.** A static stub resolves in well under a microsecond, allocating about a
  kilobyte. For static mocking, Mockifyr's own cost is not what limits you — the network is.
- **The journal costs ~133 ns and no extra steady-state allocation** (~37 % on the cheapest possible
  request, proportionally far less on any realistic one). `--journal-disabled` is worth reaching for
  in a load test, and rarely worth it otherwise.
- **Structural JSON matching costs about 300 ns over a plain match.** Parsing a small body is not the
  expensive part of anything.
- **Matching scales linearly with stub count.** The 32 µs figure is the honest worst case: the request
  matches the *last* of 1000 stubs, so every one is evaluated. A first-hit request is back at the
  baseline. Two consequences: keep stub sets per tenant rather than piling every team's stubs into one
  tenant, and expect the 94.8 KB allocated in that case to be the dominant GC pressure on a busy host
  with a large store. Indexing candidate stubs by method and path prefix is the obvious optimization
  and is **not** implemented — tracked in [#265](https://github.com/qorpe/mockifyr/issues/265). This
  is now the largest cost in the engine.
- **Templating costs about 830 ns over a static match** — roughly 3× a static stub, not the 2000×
  it was before compiled templates were cached ([#266](https://github.com/qorpe/mockifyr/issues/266)).
  Templating is no longer a reason to reach for a static stub, and per-request helpers
  (`randomValue`, `now`, `faker`) still run per request: the cache holds the compiled delegate, never
  the rendered output.
- **A large body costs about 10× its own size in allocations** (2.5 MB for 256 KiB), which lands in
  Gen2 — visible as GC pauses under sustained large-payload load. Prefer proxying or a file-backed
  response for multi-megabyte payloads. The 262 µs is dominated by copying and rendering those bytes,
  not by matching.

## Load tests

The engine numbers do not include Kestrel, TLS, or the client. `bench/load/mockifyr-load.js` (k6)
drives a running host across the same cases plus a journal-on/journal-off comparison. It is **not** run
in CI — a load test needs a quiet machine to mean anything, and a shared runner is not one.

```bash
dotnet run --project src/Mockifyr.Server -- --port 8080 --journal-disabled
node bench/load/seed.mjs && k6 run bench/load/mockifyr-load.js
```

Published end-to-end throughput figures are deliberately absent until they can be produced on stated,
dedicated hardware. Numbers from a developer laptop under a video call are worse than no numbers,
because someone will plan capacity with them.

## Sizing guidance

Starting points, not promises — measure with your own stubs, which is what the harness is for.

| Workload | CPU request | Memory request | Notes |
|----------|------------:|---------------:|-------|
| A team's static mocks (< 200 stubs, low hundreds of rps) | 100m | 256 Mi | The default Helm values |
| A shared sandbox (1000+ stubs, templated responses) | 500m–1 | 512 Mi | Cost here is stub-count, not templating; scale on CPU |
| Large payloads (≥ 256 KiB bodies) | 500m | 1 Gi | Allocation, not CPU, is the constraint |
| Load-test target | 1–2 | 512 Mi | Add `--journal-disabled`; the journal is pure overhead here |

Memory floors to keep in mind, all bounded by design:

- The **request journal** holds up to `--journal-limit` (default 1000) serve events per tenant,
  including request and response bodies. On a host serving 256 KiB responses that is measured in
  hundreds of megabytes — lower the bound or disable it.
- The **message inbox** (`--message-limit`) and **sandbox collections** (`--resource-limit`) are
  bounded the same way, per tenant and per collection.
- The **audit trail** (`--audit-limit`) holds small entries; 1000 of them is negligible.
- **Stubs themselves** are small, but a store of 10 000 is ~10× the per-request matching work in the
  worst case above.

Scale **out**, not up: Mockifyr is stateless apart from its stores, so with a shared persistence
backend (Postgres or Redis) several replicas serve the same tenants. With in-memory or file-based
persistence, replicas do not share state — that is a persistence choice, not a scaling limit.

## How CI uses this

The **Engine benchmarks** job runs `--quick` on every pull request. Its numbers are not citable — a
shared runner is too noisy — and it is not a gate. It exists so a change that breaks the hot path,
throws, or allocates unboundedly shows up on the pull request that caused it rather than at the next
release measurement. Results are uploaded as an artifact on every run.

Published figures come from a full run on the machine stated above, refreshed per release.

## Template compilation caching (#266)

The first thing measuring found. `TemplatingResponseRenderer.RenderTemplate` was
`_handlebars.Compile(template)(model)` — every templated response paid full Handlebars compilation, so
a templated stub cost **1961× a static one**. `CompiledTemplateCache` caches the compiled delegate by
template text, and the same fix applies to the webhook and WebSocket-message renderers, which had the
same line.

| | Before | After |
|---|-------:|------:|
| Templated response | 699 µs | **1.21 µs** |
| Allocated | 88.9 KB | **4.55 KB** |

Two things had to stay true, and both are pinned by tests:

- **Compilation is cached; output is not.** `randomValue`, `now` and `faker` are helper invocations
  *inside* the compiled delegate, so they still run per render. Caching the rendered body instead
  would silently break every stub that relies on them — `Per_request_helpers_still_vary_between_renders`
  is the test that would fail.
- **The cache is bounded.** Template text is authored input, and on a shared sandbox a stub author can
  produce unlimited distinct templates, so an unbounded dictionary keyed by them is a memory leak with
  a trivial trigger. At the bound the cache clears rather than evicting one entry at a time: a
  template still in use recompiles once, while an LRU would cost a bookkeeping write on every hit to
  defend against a pathological case rather than a normal one. The bound is asserted after *every*
  render, not once at the end — a bound that is exceeded and then happens to be back under the limit
  when the test looks is not a bound.

Rendering output is unchanged, proven by the differential suite staying green (257 tests) rather than
by inspection.

**Stryker: 4/5 killed.** The survivor is the cache-hit fast path itself — removing it leaves behaviour
identical and only makes everything slower, so no unit test can see it. It is an equivalent mutant
*for correctness* and precisely the thing the benchmark exists to catch: with the branch gone,
`MatchAndRenderTemplate` returns to ~699 µs, which the CI benchmark job would show.
