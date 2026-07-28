# Parity notes — G7 Admin API + stub metadata

WireMock admin behaviors probed against the oracle (`wiremock/wiremock:3.10.0`) plus notes on how the
management path is built. See [README](README.md) for the format.

## Admin CRUD shapes (probed from the oracle)

- **`POST /__admin/mappings`** creates a stub and returns it with an **`id`** and a duplicate
  **`uuid`** (the same GUID), the request/response, and any **`metadata`** — status **201**. The stub
  serves immediately.
- **`GET /__admin/mappings`** → `{"mappings":[ … ]}`, each entry carrying `id`/`uuid`/`metadata`.
- **`GET /__admin/mappings/{id}`** → 200 with the stub.
- **`DELETE /__admin/mappings/{id}`** → **200**; the stub stops serving afterwards. Idempotent.
- **`POST /__admin/mappings/reset`** clears every mapping.
- **Invalid stub JSON** → **422**.

## G7a — management path (CQRS via Mediant), validated in-process

- **Group / item:** G7a. The admin JSON is volatile-field-heavy (`id`/`uuid` differ per engine), so
  the HTTP surface is validated **semantically** in G7b; G7a validates the underlying **CQRS handlers
  in-process** (`Mockifyr.Application.Tests`).
- **CQRS.** `Mockifyr.Application` (Mediant 1.0.0, `ISender`, `Result<T>`): `CreateStubCommand`,
  `DeleteStubCommand`, `ImportMappingsCommand`, `ResetMappingsCommand`, and the queries
  `GetStubsQuery`/`GetStubQuery`/`CountRequestsQuery`/`FindUnmatchedRequestsQuery`. Handlers depend
  only on Core contracts (`IStubStore`) and the engine's read-only verify methods. Mediant lives
  **only** here (decision 0005).
- **Shared state.** `AddMockifyr` (composition root, `Mockifyr.Server`) registers the in-memory
  stores + engine + Mediant handlers as singletons, so a stub created through the **management path**
  is immediately served by the **hot path** — verified (`CreatedStub_IsServedByTheEngine_AndCounted`).
- **Stub id / metadata.** The adapter now honours an explicit `id`/`uuid` (else mints one) and parses
  the arbitrary `metadata` object onto `StubMapping.Metadata` — verified round-tripping
  `metadata.team`.
- **Regression cases:** `Mockifyr.Application.Tests.AdminCqrsTests` (5 cases).

## G7b — admin HTTP facade, validated semantically against the oracle

- **Group / item:** G7b. `Mockifyr.Facade.Admin` maps the WireMock-compatible `/__admin/*` routes to
  Mediant commands/queries (`AdminEndpoints.MapAdminEndpoints`); `Mockifyr.Server`'s host wires
  `AddMockifyr` + the endpoints. Thin: HTTP → `ISender.Send` → Application → Core.
- **How it's validated.** The **same** admin scenario is driven over HTTP against both the oracle and
  Mockifyr's in-memory admin host (`WebApplicationFactory<Program>`), and the **observation sequence**
  — status codes + mapping counts — must match. Per-engine stub ids differ, so the comparison is
  semantic (effects), not byte-for-byte. Verified identical on both:
  `reset→200`, `count 0`, `create→201`, `count 1`, `get→200`, `getMissing→404`, `delete→200`,
  `count 0`, `import(bundle of 2)→200`, `count 2`, `malformed create→422`, `reset→200`, `count 0`.
- **Status codes matched to the oracle:** create **201**, get/delete/import/reset/list **200**, a
  missing id **404**, malformed stub JSON **422** (the handler catches the parse error and returns a
  validation `Result`, which the endpoint maps to 422 — no exceptions for control flow).
- **Deferred:** mock-serving over HTTP (a catch-all → engine → wire response) belongs to the
  transport facade (**G12**); `/__admin/scenarios*` listing and the rich admin response JSON export
  shape (only ids/counts are surfaced now); tenant resolution (default tenant until G12).
- **Regression case:** `G7bAdminHttpTests.Admin_Crud_MatchesTheOracle`.

> **G7 is complete** with G7b: the management path is a full Mediant CQRS layer behind a
> WireMock-compatible admin HTTP surface, validated in-process (handlers) and over HTTP (semantic
> differential).

## Backfill — `PUT /__admin/mappings/{id}` (stub update)

WireMock supports **replacing** a mapping in place via `PUT /__admin/mappings/{id}`; the URL id is
authoritative. Mockifyr originally shipped only create (`POST`), read, delete, import and reset, so an
edit from the dashboard hit a non-existent route (`404`) and the change was silently dropped. Added
`UpdateStubCommand` + `UpdateStubHandler` (forces the parsed stub's id to the route id so the store
upserts in place rather than appending a duplicate) behind `admin.MapPut("/mappings/{id:guid}")`,
returning `200 { id, uuid }` on success and `422` for malformed/empty JSON — the same shape as create.

- **Non-obvious:** the WireMock JSON reader throws `InvalidOperationException` (not `JsonException`)
  when a field is well-formed JSON but the wrong type — e.g. a string-encoded `"status"`. The create
  and update handlers now treat that as a client input error (`422`) rather than letting it surface as
  a `500`. (The dashboard was the trigger: number-input form fields serialize as strings; the editor's
  `toWireMock` now coerces `status`/`priority` to JSON numbers.)
- **Regression case:** `AdminCqrsTests.Update_ReplacesInPlace_AndIsServed` (the update reaches the
  serving path — a follow-up request returns the new status) and `Update_MalformedJson_ReturnsValidationError`.


## Bounded journal (#220, post-G hardening)

The journal is bounded per tenant (default 1000; `--journal-limit`, reference alias
`--max-request-journal-entries`; `<=0` = unbounded; `--journal-disabled` / `--no-request-journal`
records nothing). **Eviction semantics proven differentially**: both engines started with
`--max-request-journal-entries 3`, five requests driven through each — both retain exactly the
NEWEST three, oldest evicted first (`JournalLimitTests`). Consequences carried on purpose:
`/__admin/requests/count` and verify only see retained events — identical to the reference engine.
The detail route (`/__admin/requests/{id}`) now resolves through an id index instead of
materializing the whole journal (O(1) instead of O(n)); the index is tenant-gated, covered by a
unit test the oracle cannot express. Stryker on the journal store: **100 %** (9/9 killed + 1 timeout).
Default change note: before #220 the journal was unbounded — a long-running host accumulated every
request (and its Authorization headers) forever; 1000 mirrors `--message-limit`.
