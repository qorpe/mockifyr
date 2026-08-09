# From an OpenAPI spec to a living sandbox

## The problem this solves

Integration teams wait for environments. The real API is shared, rate-limited, or not built
yet — and a hand-written mock only answers the happy path. Mockifyr turns the one artifact
you always have — the **OpenAPI document** — into a working, stateful, shareable sandbox in
about a minute.

## What "stateful" means — a stub vs. the sandbox

A classic stub is a **photograph**: same request, same canned answer, forever.

```
POST /api/orders  →  201 {"orderId": "ORD-7001"}     (always, nothing is stored)
```

The sandbox is a **film**. Its CRUD stubs are wired to a real document store:

```
POST   /payments   {"id":"PAY-2001", ...}  →  201 + Location   (actually WRITES)
GET    /payments/PAY-1001                  →  200 {…}          (actually READS)
GET    /payments                           →  {"count":4, …}   (your write is in the list)
DELETE /payments/{id}  … then GET it       →  404              (it is really gone)
```

Try it yourself: run `payments-create` twice, then `payments-list` — you'll see two new
documents. Run a plain stub twice — nothing accumulates anywhere. That's the difference.

## How the import works

`POST /__admin/openapi/import?stateful=true` (or the dashboard's *Add stub → OpenAPI* tab):

- Every operation in the spec becomes a stub. Declared examples are served as-is;
  example-less schemas get synthesized sample data.
- Resource-shaped path pairs (`/payments` + `/payments/{id}`) are detected and wired into
  **live CRUD** against the document store — no hand-written wiring.
- The store itself is schema-less: whatever JSON your app sends is what gets stored.
  The OpenAPI schema shapes the *generated examples*; it does not police the store.
  Validation belongs to matchers (request-time) or the conformance reports (after the fact).

## Data lifecycle

- Seed realistic data per collection: `POST /__admin/resources/{collection}/seed` (JSON array),
  or manage documents on the dashboard's **Resources** page.
- Documents survive restarts on every persistence backend (file, LiteDB, Postgres, Redis) —
  deletes and resets included. In-memory mode is runtime-only by definition.
- Guard rails: per-collection document limit and max body size; non-JSON is refused with 422.

## Sharing it: API keys and quotas

See [auth.md](auth.md) for the full picture. The short version: issue an `mfk_` key with an
hourly quota; the key **is** the tenant (no header needed); over-quota answers an honest
**429 + Retry-After**; an invalid key answers **401**, never a silent fallthrough.
