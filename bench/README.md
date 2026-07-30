# Benchmarks and load testing

Two harnesses, because they answer different questions and a regression in one is invisible in the
other.

| Harness | Answers | Needs |
|---------|---------|-------|
| `Mockifyr.Benchmarks` (BenchmarkDotNet) | What does one request cost **inside the engine** — matching, templating, the journal | .NET 10 SDK |
| `load/` (k6) | What does a **client** see — Kestrel, the network, concurrency | a running host + [k6](https://k6.io) |

Published figures and sizing guidance live in [docs/parity/performance.md](../docs/parity/performance.md).

## Engine benchmarks

```bash
dotnet run --project bench/Mockifyr.Benchmarks -c Release -- --filter '*'
```

Release only — a Debug number is noise, not a number. Results land in
`BenchmarkDotNet.Artifacts/results/`.

`--quick` trades precision for a run short enough for CI. That is what the **Engine benchmarks** job
runs on every pull request: it exists to make a change that breaks or badly regresses the hot path
visible on the PR that caused it, not to produce a citable figure. Citable figures come from a full
run on the machine stated in the parity document.

Run one case while you work on it:

```bash
dotnet run --project bench/Mockifyr.Benchmarks -c Release -- --filter '*MatchAmongManyStubs*'
```

## Load tests

Start a host, seed it, then drive it:

```bash
dotnet run --project src/Mockifyr.Server -- --port 8080 --journal-disabled
```

```bash
node bench/load/seed.mjs && k6 run bench/load/mockifyr-load.js
```

The seed writes four measured stubs plus 999 filler stubs, so matching is measured against a store
of realistic size rather than a store of four.

Pick a case with `SCENARIO`, and size the load with `VUS` and `DURATION`:

```bash
SCENARIO=templated VUS=100 DURATION=60s k6 run bench/load/mockifyr-load.js
```

To measure what the journal costs, run the same scenario twice — once against a host started with
`--journal-disabled` and once without. That difference is the number that decides the flag; anything
else is guesswork.
