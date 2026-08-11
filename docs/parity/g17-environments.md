# G17 — Environments (tenant-scoped `{{key}}` config)

Environments have **no WireMock counterpart**, so there is no oracle to diff against. Per the standing
rule for such features, the validation method is stated up front: pure-logic unit coverage for the
substitution contract, behavioral self-tests for the two claims the issues make, and an end-to-end
script driving only the public HTTP surface. See `docs/decisions/0008-serve-time-environment-resolution.md`
for why the feature is server-side at all.

## What it is

A tenant owns **keys**; each key holds several named **values**, one of which is **active**. A stub
referencing `{{key}}` is stored with that expression verbatim and resolved when the stub is served —
so switching the active value changes every stub using the key, with no re-save.

## Where it runs (load-bearing)

- **Before Handlebars, and before the `response-template` transformer guard.** After the guard, the
  pass would silently skip every stub that did not opt into templating — which is most stubs, and the
  bug would look like "environments just don't work for my stub."
- **Response body + headers, proxy target, webhook URL/body/headers.** The proxy target needed
  explicit work: `ProxyDirective.BaseUrl` was previously *never* templated (both renderer branches
  copied it verbatim), so a `{{key}}` proxy target would have reached the outbound client as literal
  text. Webhooks needed `IServeEventTemplateRenderer` widened to carry a `TenantId`.

## Learned: a shared `{{…}}` namespace is safe only if the pass is selective

The original UI-only design (#157) resolved in the browser specifically to avoid this collision. The
substitution is safe here because it replaces **only bare identifiers that resolve to a key the tenant
has defined**. Everything else — `{{now}}`, `{{request.path}}`, `{{random 'X.y'}}`, `{{#each}}`,
`{{typo}}` — passes through byte-identical. Consequences worth knowing:

- An **undefined** reference is never blanked by this pass. On a non-templated stub it survives as
  literal `{{typo}}` in the response, which is diagnosable; the dashboard also warns while editing.
- A **substituted value is not rescanned**, so a value that itself looks like `{{other}}` does not
  chain-resolve. One pass, no recursion, no cycles.
- Lookup is **case-sensitive**: `{{BaseUrl}}` does not resolve `baseUrl`, it falls through to
  Handlebars. This keeps the pass predictable rather than helpfully wrong.

## Learned: the only real collision is a helper-named key, so it is refused at write time

A key named `now` would shadow `{{now}}` in every stub of that tenant, and nothing in the stub would
explain why the timestamp stopped appearing. Rather than manage that at read time, the admin API
**rejects** the create (`Environment.ReservedKey`, HTTP 400) against a list mirroring the built-in
helper names. The dashboard repeats the list to turn the 400 into inline feedback, but the server
remains authoritative. This is what keeps the bare-identifier surface unambiguous.

## Tenant scoping (issue #166)

Enforced in the store, not in the dashboard: every `IEnvironmentStore` / `IEnvironmentResolver` method
takes a `TenantId` and there is no tenant-less overload. Consequences that are tested explicitly:

- A key defined in tenant A is absent from tenant B's list, and B's stub referencing the same name
  resolves nothing — it does **not** inherit A's value.
- `DELETE` and the active-value `PUT` return **404** for a key the calling tenant does not own, rather
  than succeeding silently or reaching across.
- The same key name holds independent values per tenant. The Postgres schema states this directly:
  `PRIMARY KEY (tenant, key)`.
- `RenderContext.Tenant` is `required` — a future call site that forgets the scope is a **compile
  error**, not a runtime leak.

## Migration from the #157 shape

Old `localStorage` environments (`{name, baseUrl}`) convert to one key per environment with a single
value named `default`, preserving what `{{name}}` meant. They migrate into **the tenant the operator
is currently in** — the legacy data carried no tenant, so writing it to every tenant would recreate
the leak #166 reports. The legacy blob is removed only after the server accepts the writes, so a
failed migration retries rather than losing data.

## Export/import (issue #198)

The dashboard export includes the tenant's environments so a re-import restores what the stubs'
`{{key}}` references depend on. Decisions worth remembering:

- **Format**: with no environments the export stays a bare mapping array (unchanged, maximally
  interoperable); with environments it switches to the `{"mappings":[…]}` wrapper plus a sibling
  `environments` array in exactly the shape the admin API serves — minus `resolved`, which is
  computed, not state. The wrapper was already an accepted import shape, so old and new exports both
  round-trip.
- **Restore path is the server**, not the UI: `ImportMappingsCommand` reads the section
  (`EnvironmentJsonReader`) and stores each key through the same validation as the admin PUT
  (`EnvironmentKeyRules`, one definition for both paths) — a `curl` import restores environments
  identically to the dashboard.
- **Semantics**: an imported key **replaces** an existing key of the same name (an import restores
  the exported state — merge would silently keep values the export never had). An entry that fails
  validation (reserved name, malformed key, no usable values) is **skipped without failing the
  import**; the mappings still load. Hostile shapes (section not an array, non-object entries,
  non-string key, half-formed value items) are dropped, never stored half-formed, never a 500.
- **Ordering**: environments restore before mappings so one bundle is self-consistent at first serve.
- **Mutation testing**: `EnvironmentJsonReader` at 100 % (37/37 killed). Learned: Stryker's
  condition-rewriting mutants cannot compile when a `TryGetProperty` out-var binds inside a compound
  condition or ternary — "safe mode" then voids the whole method's score. The reader binds out-vars
  in standalone statements for that reason; keep the pattern when touching it.

## Deferred (tracked)

- Change-feed reload (G16e/f) does not yet cover environments: `IEnvironmentStore.GetTenants()` exists
  for it, but no reconciler subscribes. Multi-instance hosts see key changes after a restart.
- Values are stored in plaintext; a secret-typed value (masked in the dashboard) is not modelled.

## Regression cases

- `EnvironmentSubstitutionTests` — the selectivity contract, the reserved/well-formed pairing, and
  `EnvironmentKey.Resolve()` including the deleted-active-value case.
- `G17EnvironmentTests` — dynamic resolution (active-value switch reaching a saved stub), per-key
  independence, verbatim storage, non-templated stubs, and the full tenant-isolation matrix.
- `G17EnvironmentExportImportTests` — bundle restore (keys/values/active), backward compatibility,
  overwrite-by-key, skip-on-invalid, hostile section shapes, and tenant scoping of imports.

## Secret values (#348)

`EnvironmentValue` was `(Name, Value)` with no notion of sensitivity, and a sandbox handed to partners
is exactly where a webhook signing secret or a partner token ends up. It gains an optional `Secret`
flag; the literal is withheld from every surface that reports one and still resolved when a stub is
served, because a secret nobody can use is not a feature.

**Two leak points, not one.** The obvious one is `values[].value`. The second is `resolved` — the
literal the admin projection computes from the active value — and reporting that while hiding the
first would have been redaction in name only.

**The hazard redaction itself creates.** Withholding on read means the dashboard holds a key whose
secrets have no literal. Hand that straight back on save and, taken at face value, it stores empty
strings: opening the screen and pressing save would destroy a credential nobody touched. So a
submitted value that is marked secret and carries no literal means *unchanged*, resolved against what
is stored (`EnvironmentSecrets.Merge`). An explicit literal still replaces it, so rotation works. A
value that is new and secret with no literal is **dropped** rather than stored empty — an empty secret
is a stub that signs with nothing and reports success, which is the failure that looks like it worked.

The same rule covers a case that is not hypothetical: restoring a redacted bundle stores a secret
marker with no literal, because the export refused to carry one. Carrying that forward on the next
save would turn "we could not restore this" into a key that silently resolves to `""`.

**Bundles are where the leak actually happens.** Redaction that stopped at the API would stop short of
the artefact people attach to tickets and commit to repositories, so an export carries the marker and
never the literal, and an import reads a value-less secret as absent rather than as `""`.

**The dashboard would have destroyed secrets.** Found by driving the real screen rather than by
reading it: the UI typed `value` as required, filtered out any row whose value was blank before
saving, and sent the list back verbatim. A secret therefore vanished from the payload *and* lost its
marker — so the server saw a value that was neither present nor flagged and dropped it. Pressing save
on an untouched key would have deleted the credential. The type is now `value?` + `secret?`, a kept
secret is submitted as the marker alone, and the editor shows a masked input reading "unchanged — type
to replace". Verified in a browser end to end: open the key, save without touching anything, and the
stub still renders the literal.

**Validation.** `SecretEnvironmentValueTests` (10 unit cases on the merge rule) and
`SecretEnvironmentWireTests` (5 wire cases across the admin API, the served response, the save
round-trip, rotation and an export bundle). The assertions are phrased as *the value we want* wherever
possible: "the secret does not appear" passes just as well when the key is missing, the endpoint 404s,
or a typo makes the request fetch nothing — which is how a redaction test comes to guard nothing.
**Stryker: no survivors in the new logic** (`EnvironmentSecrets`, `ResolvesToSecret`). The file's
remaining survivors are in the substitution scanner and the reserved-helper list, both untouched here
and predating this change.

**A test that was right and stopped being right.** `BackupJsonTests.The_token_never_appears_in_an_archive`
forbade the string `"secret"` anywhere in an archive — equivalent to its intent while "secret" could
only mean an API key's. It now asserts against the API-key entries themselves, because a bare
substring ban would fail for a change that leaks nothing.
