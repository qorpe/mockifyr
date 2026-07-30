# Contributing to Mockifyr

Contributions are welcome. This page is the short version of the bar a change has to clear; the long
versions live in [docs/testing.md](docs/testing.md) (the binding test contract) and
[CLAUDE.md](CLAUDE.md) (how work is organised in this repository).

## Before you write code

- **Open an issue first for anything non-trivial.** Mockifyr is built as a sequence of narrow,
  validated verticals ([docs/roadmap.md](docs/roadmap.md)); a change that fits the current one lands
  quickly, and one that cuts across several is worth a conversation before the work.
- **A bug fix needs no issue** — a pull request with a failing test that now passes is the clearest
  possible report.
- **Security issues are not pull requests.** See [SECURITY.md](SECURITY.md).

## The bar

A change is done when all of this is true — not when it builds.

**Tests you wrote and ran yourself.** Every layer the change touches:

| The change touches | It ships with |
|--------------------|---------------|
| Pure logic (matching, templating, a store, a filter) | Unit tests, plus **Stryker at 100 %** — or each survivor analyzed as an equivalent mutant and written down in `docs/parity/` |
| Behavior of the mapping dialect | A **differential test** against the WireMock oracle (Docker). This is the only accepted proof of parity — a self-written assertion of what "should" happen is not |
| An admin endpoint, a facade, a CLI flag | A wire test on a real Kestrel host |
| A protocol or provider with no oracle | A real-client self-test (MailKit, the Twilio SDK, a real gRPC client), and a note in `docs/parity/` stating where the oracle boundary is |
| The dashboard | In-browser verification, plus `tsc`, `lint` and `build` |

Everything green before the pull request: `dotnet build` with **0 warnings** (warnings are errors),
all four test projects, and the UI checks if you touched `ui/`. CI repeats them — green CI is
confirmation, not discovery.

**Documentation in the same pull request.** A feature the docs do not describe is unfinished:

- `docs/parity/<group>.md` — what was learned, how it was validated, and any deferred edge (stated,
  never silent).
- `docs/roadmap.md` — the item ticked or recorded.
- `docs/decisions/` — an ADR when a design decision was made or changed.
- `README.md` — the flags table and feature claims still true.
- The docs website ([qorpe/mockifyr.qorpe.com](https://github.com/qorpe/mockifyr.qorpe.com)) — a
  companion pull request for any user-facing change.

**Compatibility.** Read [VERSIONING.md](VERSIONING.md) before changing an existing surface. Flags are
renamed by adding an alias, never by removing the old name.

## Architecture rules that are not negotiable

These exist because retrofitting them is the expensive path, and a pull request that breaks one will
be asked to change shape:

- **`Mockifyr.Core` has zero external dependencies**, does no I/O, and never references a transport, a
  mediator or a persistence library. Delay, fault and proxy are *directives* a facade applies.
- **Transport never leaks into the engine.** Matching and templating live behind Core contracts, never
  inside an HTTP handler.
- **Every store and engine entry point takes an explicit `TenantId`.** There is no tenant-less
  overload — forgetting to scope something must be a compile error.
- **No facade depends on another facade.** External libraries live at the edges.

The dependency rule in one line: all arrows point inward to Core.

## Style

- **.NET 10**, nullable enabled, file-scoped namespaces, `var` where the type is apparent.
- **Conventional Commits**, in English, imperative mood:
  `feat(matching): add urlPathTemplate named path variables`.
- **Everything committed is in English** — code, comments, commit messages, documentation.
- Tests are named for the behavior they pin, not the method they call.

## Running it locally

```bash
dotnet build Mockifyr.sln -c Debug
dotnet test Mockifyr.sln -c Debug
dotnet run --project src/Mockifyr.Server -- --port 8080 --dashboard ui/dist
```

The differential suite needs Docker — it starts a real WireMock container as the oracle. Everything
else runs without it.
