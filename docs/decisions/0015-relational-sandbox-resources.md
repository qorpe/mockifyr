# 0015 — Relations in the sandbox resource model

## Status

Proposed. Design for issue #350 under the G22 epic (#345); extends ADR 0011, which defined the
resource model without any concept of one document belonging to another.

## Context

ADR 0011 gave a sandbox document three coordinates: tenant, collection, id. That was enough for a
flat API and it is not enough for the APIs people actually hand to an integrator, which are almost
always hierarchical: an order belongs to a customer, an account to a client, a transaction to an
account.

The absence is not a missing feature. It is a defect with a visible wrong answer, and the path that
produces it is the one the quick-start recommends. Importing a spec containing `/customers`,
`/customers/{customerId}` and `/customers/{customerId}/orders` today yields:

```
POST /customers          {"name":"A"}    → 201, collection "customers", id 1
POST /customers          {"name":"B"}    → 201, collection "customers", id 2
POST /customers/1/orders {"total":100}   → 201, collection "orders",    id 1
POST /customers/2/orders {"total":250}   → 201, collection "orders",    id 2

GET  /customers/1/orders → [100, 250]    ← A sees B's order
GET  /customers/2/orders → [100, 250]    ← and B sees A's
POST /customers/99/orders → 201          ← customer 99 does not exist
DELETE /customers/1       → orders remain ← orphaned
```

Two lines produce all four. `OpenApiStubGenerator.CollectionName` takes the **last segment** of the
path, so `/customers/{customerId}/orders` becomes a flat global `orders`; and `state.list` calls
`store.List(tenant, collection)`, which is the whole collection. Nothing records who owns a
document, because `ResourceDocument` has nowhere to put it.

The first of those is the sharpest: a sandbox whose whole purpose is to isolate consumers from each
other leaks one modelled customer's data to another. It is not a tenant-isolation failure — tenancy
holds — but to the person reading the response it is indistinguishable from one.

## Decision

### A relation is one named key, declared once per collection

Not per stub. Four stubs (create/read/update/list) each restating the relation is four chances to
write three of them correctly, and the fourth is the one that leaks. The declaration lives on the
collection:

```json
PUT /__admin/resources/schemas/orders
{
  "belongsTo": [
    { "collection": "customers", "via": "customerId", "onDelete": "restrict" },
    { "collection": "products",  "via": "productId",  "onDelete": "restrict" }
  ]
}
```

This is the shape both comparable tools use. json-server relates a comment to a post through a
`postId` field in the comment and answers `?postId=1`, `_embed=comments`, `_sort`; PostgREST relates
through a real foreign-key column, embeds with `select=name,orders(*)` and filters embedded columns
by dotted path. We are not inventing a relational vocabulary; we are adopting the one integrators
already read.

### Where the key lives: the contract decides, not us

The obvious implementation is to stamp the parent id into the stored document — `POST
/customers/1/orders {"total":100}` persisting `{"total":100,"customerId":"1"}`. That makes ownership
and reference one mechanism, and it is what json-server's data looks like.

We reject it as an unconditional rule, because json-server's documents are authored by the user
while ours arrive from a request against an imported OpenAPI document. If the spec's `Order` schema
declares no `customerId`, stamping one in means the sandbox returns a field the contract does not
have — and `POST /__admin/openapi/verify` (#287) would then report our own sandbox as drifted from
the document we generated it from. A tool that fails its own conformance check has chosen elegance
over correctness.

The rule is therefore conditional:

- **The contract declares the field** → the key lives in the body, exactly as json-server and
  PostgREST do. It is visible, filterable, and available to a later `expand`.
- **The contract does not** → the key lives as an optional **parent pointer in the document's
  metadata**, and the body round-trips byte-for-byte as ADR 0011 promised.

One accessor resolves "who owns this document" across both, so scoping, integrity and cascade are
single-path code and the storage choice never reaches a caller.

### `onDelete` defaults to `restrict`

Deleting a Stripe customer does not delete their charges: it cancels active subscriptions, and the
customer stays retrievable as `{"deleted": true}` so history survives. Cascade is not the industry
default, and making it ours would mean an imported spec silently acquires destructive behaviour the
API it models does not have — a *less* faithful sandbox, which is the opposite of the point.

Declarable values are `restrict` (default; `409` while children exist, naming the collection and
count), `cascade`, and `orphan` (delete the parent, leave children with a dangling key — legal, and
what some real APIs do). The default is also the safest failure mode for a partner-facing sandbox:
nothing is destroyed by surprise.

### A relation is enforced when its key is present

Not "required unless declared optional". If a document carries `customerId`, that customer must
exist; if it carries none, nothing is checked. The alternative — a declared relation being mandatory —
makes two collections that reference each other impossible to populate, because neither can be
created first. Presence-triggered enforcement keeps mutual references and self-references
(`employees.managerId → employees`) workable, which is why cycles in the relation graph are legal
rather than rejected. Cascade terminates through a visited set, not through a forbidden declaration.

### Depth is a chain, not a special case

REST guidance converges on one or two levels of nesting, but specs in the wild go deeper. Ownership
is one pointer per document, so `/clients/1/accounts/2/transactions` is resolved by walking the
chain rather than by a rule per level. Storage imposes no depth limit; the guidance to keep specs
shallow is the spec author's concern, not ours to enforce.

### Lookup by field is the primitive underneath all of it

`IResourceStore` reads two ways today: one document by id, or an entire collection. Scoping a
relation is a field lookup (`customerId = 1`). So is resolving a session by its token, which is the
one genuinely missing piece of request-to-request correlation — `POST /auth` returning a token that
a later request presents can only be answered today by making the token the document id, which
works exactly once and fails for every other field.

They are the same primitive, so it is built once and both are closed by it. This is why #353
(resource querying) folds into this work rather than following it: building a query surface twice,
once privately for relations and once publicly, is how two subsystems come to disagree about what
`=` means.

### Explicitly out of scope

Joins, cross-collection transactions, a query language, and schema migrations. A sandbox should
behave like the API it stands in for, not become a database that is harder to reason about than the
service it replaces. This paragraph exists so a later reader can tell a deliberate boundary from an
oversight.

### Backward compatibility

Binding under `VERSIONING.md` since 1.0, and satisfied by construction rather than by care:

- The parent pointer is **optional**. Documents persisted before this change have none and stay
  valid on all four backends (FileBased, LiteDB, Postgres, Redis), through backup/restore (#252)
  and through export/import bundles.
- `state.list` scopes **only** when a relation is declared. A collection with no schema answers
  byte-for-byte as it does today.
- Referential integrity applies **only** to declared relations. An undeclared collection accepts
  what it accepts today.

## Consequences

- The sandbox models hierarchical APIs correctly, which is most of the APIs an integrator is handed.
  The defect above disappears as a consequence of the model rather than as a patch to `list`.
- An OpenAPI import carries relations without the user writing anything: `/customers/{customerId}/orders`
  already *is* the declaration "orders belong to customers, keyed by customerId", and the importer
  reads it.
- `IResourceStore` gains a field-lookup entry point, implemented by four persistence providers. The
  in-memory hot path (ADR 0006) remains the source of truth, so the lookup is in-process and the
  providers persist rather than query.
- Resource querying (#353) and the correlation gap close with this work rather than after it.
- `expand` (embedding a related document in a response) becomes possible and is **not** included
  here — recorded in `docs/parity/deferred-edges.md` as tracked, with this ADR as its precondition.
- No new dependency; no Core dependency; no change to the mapping-JSON dialect the oracle validates,
  so the differential suites stay green untouched — the same regression proof ADR 0011 relied on.

## Binding acceptance criteria

Part of the definition of done, in addition to `docs/testing.md`.

| Area | Criterion |
|---|---|
| The defect | The trace at the top of this ADR is a test: two customers, two orders, each list scoped, `POST /customers/99/orders` refused, `DELETE /customers/1` governed by the declared rule |
| Contract fidelity | After import + seed + serve, `POST /__admin/openapi/verify` reports **no** drift — the check that would catch us stamping an undeclared field |
| Compatibility | A document written by the previous version loads, serves and re-exports unchanged on all four providers; an unscoped collection's `list` output is byte-identical to 1.x |
| Tenancy | A relation never spans tenants: a parent id that exists in another tenant is a miss, not a hit |
| Integrity | A key is checked **when present**, so mutually referencing collections stay creatable; cycles in the relation graph are legal (`employees.managerId → employees` is a real model) and cascade terminates through a visited set rather than through a rejected declaration |
| Mutation | Stryker at 100 % on the resolution, integrity and cascade logic; any survivor documented as an equivalent mutant |
| Edge sweep | Missing parent, deleted parent mid-request, unicode and hostile ids, a 10k-child cascade, concurrent delete-parent/create-child |
