# Request matching — and "why didn't it match?"

## A mock that says no

A mock that answers everything with 200 hides client bugs until production. Mockifyr stubs
validate requests the way the real API would. The demo's order stub demands three things:

```json
{
  "request": {
    "method": "POST",
    "urlPath": "/api/orders",
    "headers":      { "X-Partner-Key": { "equalTo": "secret" } },
    "bodyPatterns": [ { "matchesJsonPath": { "expression": "$.sku", "equalTo": "WIDGET-1" } } ]
  }
}
```

Right key + right body → **201**, and the response is templated from the request
(`{{jsonPath request.body '$.sku'}}` copies your `sku` into the answer). Wrong key → **404**.

The matcher vocabulary covers URL (path/pattern/template), method, headers, query, cookies,
and bodies via `equalToJson`, `matchesJsonPath`, JSON Schema, XML/XPath, regex, logic
combinators and priorities.

## Near-miss: the diagnosis

A 404 tells the client nothing — deliberately: the served response never leaks diagnostics.
The *diagnosis* lives on the admin surface. Ask the engine about the failing request:

```
POST /__admin/near-misses/request        (or GET /__admin/requests/{id}/near-misses)
```

and it answers with the closest stubs, **attribute by attribute**:

```json
{
  "expected": { …the stub's own request block, verbatim… },
  "attributes": [
    { "attribute": "urlPath",                  "matched": true,  "actual": "/api/orders" },
    { "attribute": "method",                   "matched": true,  "actual": "POST" },
    { "attribute": "headers['X-Partner-Key']", "matched": false, "actual": "WRONG" },
    { "attribute": "bodyPatterns[0]",          "matched": true }
  ]
}
```

Read it like an MRI: the request "hurts" (404) — this shows exactly which attribute caused
it and what the request actually carried there. The attribute names are the mapping JSON's
own vocabulary, so you can grep your own stub file for the string in the report.

Two properties worth knowing:

- Diagnosis is **side-effect-free**: no journal entry, no scenario advance, and the served
  404 stays byte-identical whether or not anyone is diagnosing.
- The journal's **Unmatched** tab lists every request that found no stub — each one can be
  diagnosed after the fact by id.
