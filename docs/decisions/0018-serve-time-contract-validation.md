# 0018 — Serve-time contract validation: the contract travels with the stub

## Status

Proposed. Design for issue #448 (Part 1) under the sandbox-parity wave; extends ADR 0011, whose
OpenAPI import (G19c) produced stubs that answer every request the same way.

## Context

A sandbox exists so that a partner's integration fails there for the reasons it would fail in
production. An imported stub carried no body matcher, so a request missing a required field or
sending the wrong type got the happy answer. What existed was after the fact
(`POST /__admin/requests/verify`, #287): a report over the journal that the partner never sees while
calling.

## Decision

1. **Opt-in, per import.** `POST /__admin/openapi/import?validate=true` writes each operation's request
   contract into every stub it generates for that operation (the example stub, its scenario states and a
   state-wired CRUD stub alike), so which of them wins cannot decide whether a request is checked. Without
   the flag an import is byte-identical to before.
2. **The contract travels with the stub**, as `request.validate` in the mapping JSON: the path template,
   the path/query/header parameters with their schemas, and the body schema of each `application/json`
   (or `+json`) media type, keyed by that type, every local `$ref` inlined. Nothing points back at the document, so a stub validates identically
   after an export, a restore or a reload on any of the four backends, with no new store and no per-backend
   code. The alternative, a per-tenant contract store referenced by id, was rejected: it is a second piece of
   state that can be lost while the stub that relies on it survives, which is the defect ADR 0015 avoided for
   relations.
3. **Validation does not take part in matching.** The stub still wins exactly as it did; validation runs
   on the winner, before anything the stub would do. A violating request is answered **400** with an RFC 9457
   problem document (`application/problem+json`) whose `violations` name each one by `location`, `pointer`
   and `rule` — the rule in JSON Schema's own vocabulary (`required`, `type`, `enum`, `format`,
   `additionalProperties`, `maximum`…), so a reader can search the contract for it. At most 20 are named.
4. **A rejected request does nothing else.** Its response is not rendered (so a `state` directive never
   writes), its scenario does not advance, and no post-serve action (webhook, broker publish) runs — the
   journal records it with the violations, the way the idempotent replay is journaled, because the request
   really did arrive.
5. **The engine stays pure.** Core declares the directive, the `IRequestValidator` seam and the gate; the
   JSON Schema evaluation lives in `Mockifyr.Adapters.OpenApi` (JsonSchema.Net, already a dependency of the
   edge), registered by the host.
6. **Fail-closed.** A `validate` declaration that cannot be read (a hand-written one without a path, a
   parameter without a name or location, a body without a schema), a schema the evaluator cannot use, and a
   host with no validator registered (the in-process library facade) all refuse the request naming the
   reason, rather than serve it as though it had been checked.
7. **OpenAPI 3.0's two spellings that mean something else to JSON Schema 2020-12** are translated:
   `nullable: true` becomes a `null` member of `type`, and the draft-4 boolean
   `exclusiveMinimum`/`exclusiveMaximum` becomes the numeric bound it modifies. `format` is asserted, not
   annotated.

## Not in this part (deferred-edges)

- A per-tenant default for validation, and an error body shaped like the real API's (a template per
  import): Part 2 of #448.
- OpenAPI 3.1 documents: the pinned reader (Microsoft.OpenApi 1.6) refuses them at import, so they are
  refused rather than imported unchecked.
- Cookie parameters, non-JSON bodies, and a schema that still refers to itself after inlining (a
  recursive model) are not checked.

## Consequences

- An imported stub with the flag carries its schemas, so its mapping JSON is larger.
- No oracle exists (the reference engine has no OpenAPI import); validation is self-tested against the
  contract's own words, through a real host.
