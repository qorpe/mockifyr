# Testing strategy

This document is the **binding test contract** for Mockifyr. Every change — human- or AI-authored —
ships with the tests this strategy demands, and the author **runs them before claiming done**.
"Looks like it works" is never done (CLAUDE.md §2).

## The layers

| Layer | Lives in | Proves | When required |
|-------|----------|--------|---------------|
| **Unit** | `tests/Mockifyr.Core.Tests`, `Mockifyr.Matching.Tests`, `Mockifyr.Application.Tests` | Pure logic: matchers, stores, filters, handlers, tenant-isolation invariants | Every new pure-logic unit or behavior branch |
| **Differential (oracle)** | `tests/Mockifyr.Differential.Tests` (Docker) | Byte/semantic parity with real Java WireMock | Every WireMock-dialect behavior — the **only** definition of done for parity claims |
| **Wire self-tests** | `tests/Mockifyr.Differential.Tests` (host-only, no Docker) | Mockifyr-own surface over a real Kestrel host (`MockifyrHost.Build(["--port","0",…])`) | Every admin endpoint, facade, or flag with no WireMock oracle |
| **Real-client self-tests** | same | Interoperability where no oracle exists: MailKit drives SMTP, the official Twilio SDK drives the SMS profile, a real gRPC client drives descriptors | Every protocol/provider surface (the parity docs must state the oracle boundary) |
| **Mutation (Stryker.NET)** | local tool (`.config/dotnet-tools.json`), run from `tests/Mockifyr.Application.Tests` | The unit suite actually kills defects | Every new pure-logic file; target **100%**, or each survivor individually analyzed as an equivalent mutant and documented in `docs/parity/` |
| **UI verification** | in-browser against a live host (`--port 8080 …` + `pnpm --dir ui dev`) | The dashboard change really works: DOM assertions + screenshots | Every UI change; `pnpm exec tsc -b`, `pnpm lint`, `pnpm build` must also pass |
| **Edge sweep** | ad-hoc, before every merge | Hostile/degenerate input does not break the surface | Every user-facing feature (checklist below) |

## How to run

```bash
dotnet build Mockifyr.sln -c Debug                       # 0 warnings — warnings are errors
dotnet test tests/Mockifyr.Core.Tests -c Debug
dotnet test tests/Mockifyr.Matching.Tests -c Debug
dotnet test tests/Mockifyr.Application.Tests -c Debug
dotnet test tests/Mockifyr.Differential.Tests -c Debug   # needs Docker for oracle-backed tests
cd tests/Mockifyr.Application.Tests && dotnet stryker --project <Project>.csproj --mutate "**/<File>.cs"
cd ui && pnpm exec tsc -b && pnpm lint && pnpm build
dotnet run --project bench/Mockifyr.Benchmarks -c Release -- --filter '*'   # engine benchmarks (#249)
```

Stryker note (learned): it only offers as mutable the projects the test csproj references
**directly** — add a direct `ProjectReference` when targeting a new assembly.

## The edge-case checklist

Run (and where cheap, codify) these before merging any user-facing surface:

- **Hostile input**: script/XSS payloads in rendered content (sandboxed iframes must hold),
  malformed JSON/MIME/regex (must degrade, never 500), path traversal in file names.
- **Empty & missing**: no subject/body/name, zero items, absent optional fields, unknown ids (honest
  404s with stable error codes).
- **Volume & size**: bulk collections (25+ recipients, 100s of rows — layout must not explode),
  large payloads (100KB+), store bounds (`--message-limit`-style eviction at, below, above capacity).
- **Unicode**: non-ASCII in every user-visible string (subjects, filenames, bodies) end to end.
- **Boundaries**: numeric limits at min/max/±1 (ports, priorities, error-code ranges, OTP digit
  counts — a 12-digit number must not match `\b\d{4,8}\b`).
- **Tenant isolation**: every new store/endpoint gets a cross-tenant test — reads, writes, deletes
  and resets must all refuse to cross (ADR 0003).
- **Concurrency/restart** where state is involved: hot reload, rewound request bodies, persistence
  round-trips.

## What each new vertical must leave behind

1. Failing test first, then the minimal implementation, then green (CLAUDE.md §3).
2. The test names state the behavior and the oracle expectation.
3. `docs/parity/<group>.md` records what was learned, what is deferred (never silent), and — where
   no oracle exists — exactly what the self-tests do prove.
4. Mutation score for new pure logic, with survivors analyzed.
5. The docs listed in `CLAUDE.md` §8 updated in the same change.
