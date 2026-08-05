# Parity notes — G6 Verify + near-miss diagnostics

Verified WireMock verification behaviors against the oracle (`wiremock/wiremock:3.10.0`). See
[README](README.md) for the format.

## Request verification (G6)

- **Group / item:** G6 — validated against the oracle **semantically**. The admin JSON
  (`/__admin/requests*`) carries many **volatile fields** (`clientIp`, `loggedDate`, `absoluteUrl`,
  `port`, `scheme`, `loggedDateString`…), so verification is compared by **counts and identities**,
  not byte-for-byte.
- **`count(pattern)`** = the number of **journaled requests** matching a request pattern, using the
  **same matchers as stubs**. Verified against `POST /__admin/requests/count`:
  - `{"method":"POST","url":"/api"}` → 3 (all POSTs to `/api`, regardless of body).
  - `{"method":"POST","url":"/api","bodyPatterns":[{"contains":"hello"}]}` → 2.
  - `{"method":"DELETE","url":"/api"}` → 0.
  - **`{}` (empty pattern) matches every recorded request** → 4.
- **`unmatched`** = journaled requests that matched **no stub** (`GET /__admin/requests/unmatched`).
  Verified: a request to an unstubbed URL is the sole unmatched entry; the count agrees with
  Mockifyr's `FindUnmatchedRequests`.
- **Implementation.** The engine already journals every serve (`IRequestJournal`); verification is a
  read-only query that reuses the matcher evaluation (`StubEngine.CountRequestsMatching` /
  `FindRequestsMatching` / `FindUnmatchedRequests`). No new matching logic. The query request pattern
  is parsed by the same adapter (`WireMockMappingReader.ReadRequestPattern`).
- **Harness.** `VerifyScenarios` loads stubs, replays traffic into both journals, then compares
  `count(pattern)` per pattern and the unmatched count via the oracle's admin API vs Mockifyr's
  in-process verifier (`DifferentialRunner.RunVerifyAsync`).

## Near-miss diagnostics (G6)

- The closest stubs to an unmatched request are ranked by **ascending match distance** — the same
  distance matching already computes (`MatchResult.Distance`), so near-miss needs no extra machinery
  (`StubEngine.FindNearMisses`). Validated as **pure logic**: a URL-only mismatch is strictly closer
  than a method+URL mismatch and ranks first.
- **Deferred:** **cross-engine near-miss identity** comparison (the oracle's
  `/__admin/requests/unmatched/near-misses` JSON identifies stubs differently, and matching them
  across engines is a separate effort), and the `find` request-body/identity byte comparison — the
  count comparison already exercises the matching semantics.
- **Regression cases:** `G6VerifyTests.Verify_CountAndUnmatched`,
  `G6NearMissTests.NearMisses_AreRankedByAscendingDistance`.


## Near-miss diagnostics on the admin API (#288, post-1.0)

- **Group / item:** post-roadmap platform feature — **self-tested**. Only the *ranking* is comparable
  with the reference engine; the shape is not, because the two engines answer the question in different
  places.
- **The gap.** Ranking by distance has existed since G6, but only on the in-process library API and only
  as a number. Over HTTP there was no way to ask, and a bare distance does not answer the question
  anybody actually has, which is *which part of my stub disagreed with this request*.
- **Answered as an admin query, never in the 404.** `GET /__admin/requests/{id}/near-misses` explains a
  journaled request; `POST /__admin/near-misses/request` explains a hypothetical one, so a stub can be
  debugged before a client exists. The served 404 stays a bare 404 — asserted — which is what keeps the
  differential suite proving exactly what it proved before, and keeps diagnostics off the serve path.
- **Attributes are named in the dialect's own vocabulary.** `urlPath`, `headers['X-Api-Key']`,
  `bodyPatterns[0]`. A stub written with `urlPath` is told `urlPath`, not `url`: the point is that the
  reader can search their own mapping for the string we printed. The five URL spellings each report
  their own name, and a `urlPath*` matcher echoes the **path** rather than the full URL, because
  offering a query string that was never compared invites a hunt for a difference that does not exist.
- **`INamedTargetMatcher` is optional on purpose.** Header/query/cookie/form and the URL matchers
  implement it; anything else — including every custom matcher a user wrote under G10 — keeps working
  and reports by position (`headers[0]`). Putting the member on `IMatcher` would have broken every
  extension in the wild to improve an error message. Same shape as `IMultiTenantMappingsLoader`.
- **What the request carried is reported; what the stub expected is not restated.** The stub's own
  request block rides along as `expected`, so nothing had to teach 36 matcher implementations to
  describe themselves. A form parameter never borrows a cookie's value when the two share a name —
  asserted, because that would send a reader after a value the matcher never looked at.
- **Attribution is opt-in.** `FindNearMisses(tenant, request)` still returns ranking only; the detailed
  overload re-runs each matcher individually, and only for the three candidates that survive the
  ranking. A debugging cost belongs to whoever is debugging.
- **Tenant-scoped**, asserted: a diagnostic that leaked another tenant's stub ids and request patterns
  would be a data leak wearing a helpful face.
- **Validation.** `NearMissAdminTests` (9 wire cases: attribute-by-attribute explanation of the classic
  right-path-wrong-header failure, the expected stub riding along, the hypothetical request, ranking
  order, a matched request answering `wasMatched` instead of erroring, tenant isolation, both refusals,
  and the bare 404) and `NearMissAttributeTests` (22 unit cases covering every slot the dialect has).
  **Stryker: no survivors in the new logic**; the 18 remaining in `StubEngine.cs` are pre-existing and
  outside this change.
- **Still deferred:** the reference engine's verbose 404 diagnostic body. Serving one would change a
  response the differential suite pins, so it stays a documented difference rather than a divergence.
