# 0010 — Stubs surface their protocol: a computed field, badges, and channel-aware editors

## Status

Accepted. Design for the G18-pre vertical (prerequisite of the G18 message-mocking group).

## Context

By design, non-HTTP protocols reuse the stub engine: a gRPC call becomes `POST
/{package.Service}/{Method}` with a JSON body (G13), a GraphQL stub is an HTTP stub with the
`graphql-body-matcher` custom matcher (G14), and WebSocket message-mappings are a separate
admin-only resource (G15). The engine's transport-agnosticism is a strength — but the dashboard
currently erases the distinction entirely:

- The stub list gives no hint whether a mapping is HTTP, gRPC or GraphQL; users infer from the
  URL's shape, or don't.
- The Add flow offers one HTTP-shaped form plus raw JSON; a GraphQL stub can only be authored on
  the JSON tab, a gRPC stub only by knowing the path convention.
- gRPC descriptors (`*.dsc`) are managed by copying files into `<root-dir>/grpc/` by hand.
- WebSocket message-mappings are invisible in the UI — API-only.
- `extensions.tsx` already sketches a `Protocol` chip concept with sample data, never wired to
  real stubs.

With email and SMS channels arriving (ADR 0009), "everything is one undifferentiated JSON list"
stops scaling. The protocol must become visible **without changing what is stored** — imported
WireMock JSON must round-trip byte-identically (as-is guarantee).

## Decision

### The server computes the protocol; nothing is written

The admin mappings list gains a **read-only, computed** `protocol` field
(`http` | `grpc` | `graphql`), derived at query time:

- `grpc` — the stub's URL path matches a `Service/Method` of a loaded descriptor. The server
  already indexes descriptors (`ProtoDescriptors`); the check is a lookup, not a heuristic.
- `graphql` — the stub carries the `graphql-body-matcher` custom matcher.
- `http` — everything else.

The field never enters the stored mapping JSON, the import adapter, or the differential surface —
exports and round-trips are untouched. WebSocket message-mappings stay a distinct resource (they
are not request/response stubs) and are surfaced by listing the existing admin endpoint.

### The UI shows badges, filters, and channel-aware editors

- **Badge + facet** — a protocol chip per row in the stub tree and a protocol facet beside the
  existing method/status facets.
- **Add flow starts with a channel choice** — HTTP (today's form, unchanged), gRPC
  (service/method dropdown populated from loaded descriptors → `urlPath` + `equalToJson` +
  `jsonBody`), GraphQL (query/variables/operationName editor that emits the correct
  `customMatcher` JSON), WebSocket (message matcher + reply form against
  `/__admin/message-mappings`). Every channel keeps the JSON tab as the power-user escape hatch;
  the form is a projection of the same JSON, never a second format.
- **Descriptor management** — upload/list/delete `*.dsc` via new admin endpoints writing to
  `<root-dir>/grpc/`, surfaced in Settings; ends the manual file copy.

### Email/SMS are channels, not stub rows

Messages captured per ADR 0009 appear in their own dashboard section. Only *stub-like* artifacts
(e.g. a provider-profile override rule, which really is an HTTP stub) appear in the stub list —
with their channel badge.

## Consequences

- Zero storage/format change: the computed field is additive; WireMock import/export and the
  differential harness see identical bytes. This is asserted by a round-trip test.
- The gRPC detection is only as good as the loaded descriptors — a gRPC-shaped stub without its
  descriptor shows as `http`. Acceptable: without a descriptor it cannot serve gRPC anyway.
- The GraphQL form must emit exactly the matcher JSON the adapter already understands
  (`parameters.query` — the key learned in G14a); the form is validated against the adapter in
  unit tests, not by parallel logic.
- WebSocket UI reuses the existing endpoint contract; no new message-mapping semantics.
