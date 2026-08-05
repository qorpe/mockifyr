# Parity notes — G2 Response + templating

Verified WireMock response behaviors discovered while building the response vertical against the
oracle (`wiremock/wiremock:3.10.0`). See [README](README.md) for the format.

### Static response bodies (G2a)

- **Group / item:** G2a — fuzz-validated against the oracle.
- **`jsonBody`** renders the inline JSON as the response body, **emitted compact**
  (`{"a":1,"b":[2,3],"c":"x"}`) with **no** automatic `Content-Type` header in 3.10.0. Our adapter
  re-serializes the parsed `jsonBody` element compactly (`System.Text.Json`), matching byte-for-byte.
- **`base64Body`** renders the base64-decoded **bytes** verbatim, including non-text bytes
  (validated with `hi\0!`).
- **Body precedence.** A literal `body` string wins over `jsonBody`, which wins over `base64Body`;
  only one is emitted.
- **Multi-value response headers** (`"headers": { "X": ["a","b"] }`) are emitted as repeated header
  lines and diff green.
- **Custom status codes** (e.g. `418`) pass through.
- **`statusMessage` (reason phrase) is parsed but not yet differentially asserted** — the harness
  response snapshot captures only the numeric status, not the HTTP reason phrase. Tracked for a
  harness extension.
- **Deferred:** `bodyFileName` (needs the `__files` store + templating, arrives with G2b), and
  gzip/`Content-Encoding` (transport-level; the harness client auto-decompresses).
- **Regression case:** `G2StaticResponseTests.StaticResponse_Bodies`.

### Response templating (G2b)

- **Group / item:** G2b — fuzz-validated against the oracle.
- **Library:** WireMock uses jknack Handlebars; we use **Handlebars.Net** (confined to
  `Mockifyr.Templating`).
- **Activation.** Templating runs only when the response declares the `response-template`
  transformer (`"transformers": ["response-template"]`); otherwise the body/headers are emitted
  verbatim (a `{{...}}` in a non-transformed body is left literal — verified).
- **Request model verified:** `{{request.method}}`, `{{request.url}}` (path + query),
  `{{request.path}}` (path only), `{{request.pathSegments.[n]}}` (0-indexed), `{{request.query.name}}`
  (first value), `{{request.headers.Name}}`, and `{{request.body}}`.
- **More request-model fields (feature-audit backfill), byte-diffed:** `{{request.host}}` and
  `{{request.port}}` (from the `Host` header), `{{request.scheme}}`, `{{request.baseUrl}}`
  (`scheme://host[:port]`), `{{request.cookies.name}}` (cookie value), and `{{request.bodyAsBase64}}`.
  `{{request.id}}` is a random per-request UUID (racy, deferred).
  Validated with an explicit `Host` header + `Cookie`; regression `Templating_RequestModelFields`.
- **Multipart `request.parts` (G15f), byte-diffed.** For a `multipart/form-data` upload the parsed parts
  are exposed as `{{request.parts.<name>}}`, each carrying `.body` (part body as text), `.name`, and
  `.headers.<Header>` (first value; hyphenated names via jknack/Handlebars bracket-literal notation,
  e.g. `{{request.parts.meta.headers.[Content-Type]}}`). A later part with a duplicate name wins, matching
  WireMock's map keyed by name. The parts come from `MultipartBodyParser` (already used for multipart
  *matching* in G1k), so matching and templating share one parse. Regression
  `G15fMultipartTemplatingTests` — the oracle proves the rendered body; Mockifyr matches it byte-for-byte.
- **No HTML escaping.** WireMock emits `{{ }}` output raw (`<a>&"` stays as-is); we disable the
  Handlebars.Net text encoder (`TextEncoder = null`) to match.
- **Response headers are templated too**, not just the body.
- **Missing model values render empty** (`{{request.query.none}}` → ``).
- **Deferred:** the built-in **helpers** (jsonPath, xPath, date, random, etc.), which are their own
  roadmap items (G2c–G2h).
- **Regression case:** `G2StaticResponseTests.Templating_ResponseTemplate`.

### Named path variables — `request.path.<name>` (backfill)

- **Group / item:** response-templating backfill (deferred from G1b/G2b) — validated against the oracle.
- **WireMock's `request.path` is a dual model.** Bare (`{{request.path}}`) it renders the full request
  path; it also exposes members: **named variables** from a matched `urlPathTemplate`
  (`{{request.path.id}}` for `/users/{id}`) and **zero-based indexed segments**
  (`{{request.path.[0]}}` → first segment). A missing member renders empty. Confirmed identical on the
  oracle for multi-var templates, single-var-among-literals, bare path, and indexed access.
- **Named vars come only from the template.** When a stub matches by a non-template URL matcher
  (`urlPath`, `urlPattern`, …), `{{request.path.id}}` is empty — but indexed segments still work (they
  come from the path itself, not the template). Verified against the oracle.
- **Implementation.** The adapter records the raw `urlPathTemplate` on `RequestPattern`; the engine
  carries it into `RenderContext`; the renderer extracts variables by aligning template segments with
  the actual path segments. Handlebars.Net renders any enumerable by listing its items, so a plain
  dictionary can't be the dual model — a custom `IObjectDescriptorProvider` (`PathModel`) makes the
  type **non-enumerable** (bare → `ToString`) with a member accessor over `{named vars} ∪ {indexed
  segments}`; the bracket form `[n]` is normalized to `n`. **Deferred:** named vars in the webhook
  `originalRequest` model (response-side only for now).
- **Regression case:** `G2StaticResponseTests.Templating_PathVariables`.

### Data helpers (G2c)

- **Group / item:** G2c — validated against the oracle.
- **Helpers:** `jsonPath`, `xPath`, `regexExtract`, `formData`, `parseJson`. Value-returning helpers
  (`jsonPath`, `xPath`) emit the extracted value inline; the others *assign* a variable into the
  root scope and render nothing.
- **`jsonPath request.body '$.x'`** returns: the **raw scalar** for strings/numbers/booleans
  (`neo`, `42`, `true`), an **empty string** for a missing path or a JSON `null`, and an empty
  string when the body is not valid JSON.
  - An **array** result is emitted **compact** (`[1,2,3]`, `[{"a":1},{"b":2}]`).
  - An **object** result is **pretty-printed with Jackson's `DefaultPrettyPrinter`** — multi-line
    objects with a two-space indent and a `" : "` separator, but **single-line `[ 2, 3 ]` arrays**
    (Jackson's `FixedSpaceIndenter`). Exact bytes:
    `{\n  "a" : 1,\n  "b" : [ 2, 3 ],\n  "c" : "x"\n}`. Reproduced by a hand-written writer over the
    Newtonsoft `JToken`; the top-level-array vs nested-array asymmetry (compact vs spaced) is a real
    WireMock quirk, not ours.
- **`xPath request.body 'expr'`** returns:
  - a **text node / attribute** value (`/r/a/text()` → `one`, `/r/a/@id` → `7`);
  - an **element** node serialized as **XML** — a leaf inline (`<a>one</a>`), a subtree indented two
    spaces with `\n` newlines (`<r>\n  <a>one</a>\n</r>`). .NET's `XElement.ToString()` matches Java's
    transformer output byte-for-byte here (verified `\n`, not `\r\n`);
  - a **string()/number** function result as its string form (`count(...)` → `2`, integral doubles
    printed without a decimal point);
  - an **empty string** for an empty node-set or unparseable XML.
  - When **multiple nodes** match, only the **first** is emitted.
- **`regexExtract request.body 'regex'`** returns the **whole first match**; with a **variable name**
  third argument it assigns the **capture groups** (group 1..n) to an indexable variable
  (`{{p.0}}` = group 1) and renders nothing. On **no match**: `default='…'` is emitted if present,
  otherwise WireMock's literal error string `[ERROR: Nothing matched <regex>]`.
- **`formData request.body 'form'`** parses an `x-www-form-urlencoded` body into a variable;
  `{{form.field}}` is the **first** value, a missing field renders empty, and values are **not**
  decoded unless `urlDecode=true` (which decodes `%XX` **and** `+` → space).
- **`parseJson request.body 'obj'`** parses a JSON string into a navigable variable
  (`{{obj.a.b}}`, `{{obj.arr.0}}`). Backed by a Newtonsoft `JToken`, which Handlebars.Net navigates
  natively (`JObject` is an `IDictionary`, `JArray` an `IList`).
- **`parseJson` block form** — `{{#parseJson 'obj'}}<json>{{/parseJson}}` assigns the parsed block
  body to the named variable and renders nothing. The block body is **rendered first** (so it may
  itself be templated, e.g. `{{{request.body}}}`), then parsed — confirmed against the oracle for
  both a literal-JSON block and a templated block. Registered as a second `parseJson` helper (a
  Handlebars.Net block helper) alongside the inline form; the two coexist and dispatch on `#`.
- **Deferred (documented, no oracle claim yet):**
  - **Multi-value `formData` indexing** (`{{form.key.0}}`, `{{form.key.1}}` for a repeated key).
    Handlebars.Net renders any `IList` bound to a bare `{{form.key}}` as a comma-joined string
    (`1,2`) instead of the first value, so WireMock's `ListOrSingle` dual render/index type needs a
    custom member resolver — the same class of problem as the deferred `request.path` object.
  - **`parseJson` on scalar JSON** (booleans render `True` via Newtonsoft, not `true`).
  - **`jsonPath` on a container that nests objects inside arrays** (only scalar-array and
    flat/array-valued objects are pinned).
- **Regression case:** `G2StaticResponseTests.Templating_DataHelpers`.

### Date helpers (G2d)

- **Group / item:** G2d — validated against the oracle over **fixed** input instants (so the diff is
  clock-independent; `now`-based rendering is racy against a second clock and is excluded).
- **Helpers:** `parseDate` (string → instant) composed into `date` (instant → string), the WireMock
  form `{{date (parseDate '…') …}}`. Handlebars.Net passes the parsed value through the subexpression
  as a real object, so the composition needs no stringify/re-parse.
- **`parseDate`** reads **ISO-8601** by default (`2021-05-15T10:30:00Z`), or a **Java
  `SimpleDateFormat`** input pattern via `format=` (`parseDate '15/05/2021' format='dd/MM/yyyy'`).
- **`date` format pattern is Java `SimpleDateFormat`.** Most letters are shared with .NET
  (`y M d H h m s`) but three differ and are rewritten: `E`→day name (`ddd`/`dddd`), `a`→AM/PM
  (`tt`), `S`→fractional second (`f`). Verified: `EEE, dd MMM yyyy HH:mm:ss` → `Sat, 15 May 2021
  10:30:00`; `hh:mm a` → `10:30 PM`; `…ss.SSS` → `….000`. Names use the invariant (English) culture.
- **Default format** (no `format=`) renders ISO-8601 UTC with a literal `Z`: `2021-05-15T10:30:00Z`.
- **`format='epoch'`** → **milliseconds** since the epoch; **`format='unix'`** → **seconds**.
- **`offset=`** is `"<n> <unit>"` where **unit is plural** — `seconds`/`minutes`/`hours`/`days`/
  `months`/`years`, forwards or backwards (`-1 hours`). A **singular** unit throws on the oracle
  (`No enum constant DateTimeUnit.DAY`), so only plural is supported.
- **`timezone=` is ignored on a parsed instant.** The oracle applies **no shift** for
  `Australia/Sydney` or `America/New_York` (a parsed instant is already absolute); we match by
  ignoring the option, pinned by the `date-timezone-ignored` case.
- **Deferred (racy / documented, no oracle claim):** the **unparseable-date fallback** (WireMock falls
  back to *now*); Java pattern letters outside the shared/rewritten set (zone `Z`/`X`, era `G`,
  week/day-of-year…), which are emitted as literals rather than throwing.
- **Regression case:** `G2StaticResponseTests.Templating_DateHelpers`.

### The `now` helper (backfill)

- **Group / item:** date-helper backfill (deferred from G2d as racy) — validated **structurally** (its
  output is clock-dependent, so it can't be byte-diffed; the same method the random helpers use).
- **`now`** renders the current instant with the same surface as `date`: default ISO
  (`yyyy-MM-dd'T'HH:mm:ss'Z'`), `offset=` (`"10 years"`, `"-3 hours"`, …), and `format=` (Java pattern
  plus the `epoch`/`unix` tokens). It reuses `date`'s offset/format machinery — only the instant (now)
  differs.
- **How it's validated (the racy-feature method).** The same stub is rendered by the oracle and
  Mockifyr; each output must satisfy a contract — correct **format** (an ISO / epoch-millis / date
  regex) and a **value inside the request's time window** (`[before − 15 min, after + 15 min]`, widened
  for container-clock skew; `offset=10 years` shifts the window). The **oracle** satisfying it proves
  the contract is real WireMock behavior; **Mockifyr** satisfying the same contract is the parity
  claim. Four variants: default ISO, `epoch`, `offset='10 years'`, and a `yyyy-MM-dd` day format.
- **Deferred (tracked):** `timezone=` and `truncate=` on `now` (day/week/month truncation).
- **Regression case:** `G2iNowHelperTests.NowHelper_StructurallyMatchesTheOracle`.

### Random helpers (G2e)

- **Group / item:** G2e — validated **structurally** against the oracle.
- **Why structural.** `randomValue`/`pickRandom`/`randomInt`/`randomDecimal` are non-deterministic,
  so a byte diff is impossible. Each case instead carries a **contract** (charset + length, set
  membership, or numeric range). Every case is probed many times: the **oracle** output must satisfy
  the contract (which proves the contract is real WireMock behavior, not a self-assertion) and
  Mockifyr's output must satisfy the **same** contract. Driven by `RandomScenarios` +
  `G2StaticResponseTests.Templating_RandomHelpers`.
- **`randomValue` character alphabets** (sampled from the oracle): `ALPHANUMERIC` = `[a-z0-9]`,
  `ALPHABETIC` = `[a-z]`, `NUMERIC` = `[0-9]`, `HEXADECIMAL` = `[0-9a-f]` — all **lowercase** by
  default; `uppercase=true` uppercases the alphabet (`ALPHANUMERIC` → `[A-Z0-9]`). `length=` is exact.
- **`randomValue type='ALPHANUMERIC_AND_SYMBOLS'`** (charset **learned by sampling the oracle** over
  many long draws): lowercase + digits + the printable-ASCII symbol set
  `` !"#$%&'()*+,-./:;<=>?@[\]^_`{|} `` — i.e. every printable ASCII char in `[33,125]` **except** the
  space, the upper-case letters `A–Z`, and `~` (126). 67 characters total; membership is contracted
  (not a regex, since the set is full of metacharacters).
- **`randomValue type='UUID'`** is an RFC 4122 **v4** UUID (`…-4xxx-[89ab]xxx-…`), matched by
  `Guid.NewGuid()`; `length`/`uppercase` don't apply to it.
- **`pickRandom 'a' 'b' 'c'`** returns one of the given literals (a single-element list is
  deterministic).
- **`randomInt lower=L upper=U`** is a **half-open** range `[L, U)` — sampling `lower=5 upper=6`
  always yielded `5`, never `6` — which is exactly `Random.Next(L, U)`. Unbounded `randomInt` is a
  32-bit int (can be negative).
- **`randomDecimal lower=L upper=U`** lands in `[L, U]`; emitted with the invariant culture so the
  decimal point is stable.
- **Deferred (documented):** the distribution/format of **unbounded** `randomDecimal` (only bounded
  ranges are contracted); `uppercase=true` combined with `ALPHANUMERIC_AND_SYMBOLS` (untested — only
  the default lowercase form is contracted).
- **Regression case:** `G2StaticResponseTests.Templating_RandomHelpers`.

### JSON-manipulation helpers (G2f)

- **Group / item:** G2f — validated against the oracle.
- **Helpers:** `jsonArrayAdd`, `jsonMerge`, `jsonRemove`, `toJson`. All four take JSON **strings**
  (`jsonPath`/`request.body` or a literal); passing the object form of a `jsonPath` result is
  rejected by the oracle (`Base JSON must be a string`).
- **Output shape asymmetry.** `jsonArrayAdd`/`jsonMerge`/`jsonRemove` emit **compact** JSON, while
  `toJson` emits **Jackson-pretty** JSON — the same serialization as a `jsonPath` object, now shared
  via `JacksonJson.Write`. Notably `toJson` of an **array** is spaced (`[ 1, 2, 3 ]`), unlike a
  `jsonPath` top-level array which is compact (`[1,2,3]`).
- **`jsonArrayAdd base item`** parses `item` as JSON (`'4'` → the number `4`, `'{"k":9}'` → an
  object) and appends it. **`maxItems=`** caps the array by dropping the **oldest** elements from the
  front (`[1,2,3]` + `4` with `maxItems=3` → `[2,3,4]`).
- **`jsonMerge a b`** is a **deep** merge: existing keys keep their position with the value from `b`,
  new `b` keys are appended (`{x:1,z:0}` ⊕ `{x:9,y:2}` → `{"x":9,"z":0,"y":2}`; nested objects merge).
  Matched by Newtonsoft's `JObject.Merge`.
- **`jsonRemove json path`** deletes the JSONPath-selected node (top-level or nested, e.g. `$.a.b`).
- **`toJson value`** pretty-prints a value (accepts a parsed variable from `parseJson` or a JSON
  string).
- **Array-valued key merge — B replaces A (pinned).** WireMock's `jsonMerge` **replaces** an
  array-valued key with B's array (it does not concatenate/union), including nested arrays — verified
  against the oracle (`{"a":[1,2]}` ⊕ `{"a":[3,4]}` → `{"a":[3,4]}`). Mockifyr's `MergeArrayHandling =
  Replace` matches, now pinned by the `jsonMerge` scenario.
- **Deferred (documented):** `jsonArrayAdd` with a non-JSON string item.
- **Regression case:** `G2StaticResponseTests.Templating_JsonHelpers`.

### Format / math / array / string helpers (G2g)

- **Group / item:** G2g — validated against the oracle. These come from the **jknack Handlebars
  built-ins** WireMock registers (not WireMock's own extension helpers), so the helper names are
  `upper`/`lower`/`capitalize`/`join`/… (the camelCase `toUpperCase`/`stringJoin` do **not** exist —
  the oracle 500s with `could not find helper`).
- **`math a op b`.** Operators `+ - * /` only. With **two integers** the result is a **long**; for
  division the true quotient is **rounded half-up** (Java `Math.round`): `10/3`→`3`, `7/2`→`4`,
  `9/2`→`5`. If **either operand is a decimal** the result is a **double** rendered Java-style with a
  trailing `.0` for integral values (`4*2.5`→`10.0`, `1.5+2`→`3.5`).
- **`numberFormat value fmt`.** A Java `DecimalFormat` **pattern** maps directly onto a .NET custom
  numeric format (`0.00`→`1234.57`, `#,##0.0`→`1,234.6`). The named formats **`currency`**
  (US: `$1,234.50`) and **`percent`** (`0.4567`→`46%`, i.e. ×100 rounded, no fraction digits) are
  special-cased.
- **`size x`** = element count for a JSON array / property count for a JSON object, else string
  length (`size 'hello'`→`5`). **`join arr sep`** joins a JSON array's elements. Both take the string
  a `jsonPath` result renders to and parse it back (Mockifyr's `jsonPath` returns the string form).
- **String helpers:** `upper`, `lower`, `trim`, `substring` (2-arg = from index; 3-arg = `[start,
  end)`), `replace` (all literal occurrences), `capitalize` (first letter of **each** whitespace word,
  rest unchanged — `hello world`→`Hello World`).
- **Deferred (documented):** the modulo `%` and power `^` `math` operators (the oracle **rejects the
  mapping at registration** — no oracle to diff against), and the helpers absent from open-source
  WireMock (`abs`/`round`/`ceil`/`floor`/`split`/`truncate`), plus the helpers `stripes`/
  `contains`. `numberFormat` half-rounding edge cases are avoided (both engines agree on non-halves).
- **Regression case:** `G2StaticResponseTests.Templating_FormatHelpers`.

### More helper long-tail (G2 backfill — from the WireMock feature audit)

- **Group / item:** more built-in helpers surfaced by a top-down audit of WireMock's helper surface,
  byte-diffed against the oracle.
- **`base64 value`** encodes UTF-8 → base64 (`hello world`→`aGVsbG8gd29ybGQ=`); `decode=true` reverses
  it; `padding=false` drops the trailing `=`.
- **`urlEncode value`** is **form** URL encoding — a space becomes `+` (`a b&c=d`→`a+b%26c%3Dd`), i.e.
  .NET `WebUtility.UrlEncode`; `decode=true` reverses it.
- **`formatJson json`** pretty-prints in Jackson layout (`"k" : v`, `[ a, b ]`) — reuses the shared
  `JacksonJson` writer. **`formatXml xml`** pretty-prints with a **2-space** indent and a **trailing
  newline**.
- **`{{#assign 'name'}}…{{/assign}}`** renders the block and stores the result under a root-scope
  variable (the block form; renders nothing) — the sibling of the `parseJson` block.
- **`isOdd n`/`isEven n`** are jknack's **CSS-class** helpers (not block conditionals): they return
  `odd`/`even` (or an optional override label, `{{isOdd 7 'YES'}}`) on a parity match, else the empty
  string. Learned from the oracle after a first block-form attempt diverged.
- **Regression case:** `G2StaticResponseTests.Templating_MoreHelpers`.
- **Batch 3 (more audit backfill):** `range start end` (an **inclusive** integer sequence, iterable
  with `{{#each}}`, works with negatives), `array a b c` (an iterable of the args), `lookup coll key`
  (element by index for a list/`array`, or value by key for a map/`parseJson` object), and
  `truncateDate date '<unit>'` (floors an instant — `first second of minute` / `first minute of hour`
  / `first hour of day` / `first day of month` / `first day of year`). Two supporting fixes fell out:
  `size` now counts a live collection (not just a JSON string), and **`parseJson` with one argument**
  now **returns** the parsed value (for subexpressions like `lookup (parseJson '…') 'k'`) while the
  two-argument form still assigns to a variable. Regression `Templating_Batch3Helpers`.
- **`arrayAdd coll item`** appends `item` to a live `array`/`range` list (or a `parseJson` array);
  `position=N` inserts at that index. Byte-diffed. **Investigated and deferred** (each probed against the
  oracle): `jsonSort` — the oracle **500s** on every form tried (scalar array, `by=`, `order=`), so
  there is nothing valid to diff; `soapXPath` — returns empty for the SOAP forms tried (envelope /
  namespace handling is unclear); `arrayRemove` — the oracle removes the **last** element regardless of
  the index argument (`arrayRemove [a,b,c] 0|1|2` all → `[a,b]`), a quirk we don't reproduce.

### System helpers (G2h)

- **Group / item:** G2h — validated against the oracle. Two helpers exist in open-source WireMock
  3.10: `systemValue` and `hostname` (`systemProperty`/`env` do **not** exist).
- **`systemValue key='…' type='ENVIRONMENT'|'PROPERTY'`** is **secure by default**: with no allowlist
  configured, WireMock denies **every** key and renders the literal `[ERROR: Access to <key> is
  denied]` (verified for env vars, JVM properties, and missing keys; the deny happens before any
  existence check, and `type` doesn't change it). Mockifyr matches this deny-by-default byte-for-byte;
  configuring a permitted-key allowlist to read real values is a deploy/config concern deferred to
  **G12** (and a permitted env/JVM value could not be diffed anyway — the oracle is a JVM, Mockifyr
  is .NET).
- **`hostname`** resolves the local host name (the oracle returns its container's, e.g.
  `e7de4ebfb3f7`). It is **host-specific**, so it can't be byte-diffed — validated **structurally**
  (both sides return a non-empty hostname-shaped string, `^[A-Za-z0-9._-]+$`) via `RandomScenarios` +
  `Templating_RandomHelpers`.
- **Regression cases:** `G2StaticResponseTests.Templating_SystemHelpers` (systemValue) and the
  `hostname` case in `Templating_RandomHelpers`.

> **G2 (Response + templating) is complete** with G2h. The response vertical now covers static
> responses, the templating engine, and all built-in helper families (data, date, random, JSON,
> format/math/array, system).

## `jsonBody` is serialized verbatim (relaxed escaping)

A `response.jsonBody` object is serialized to the response body **without** escaping `'`, `<`, `>`,
`&`, or non-ASCII — matching the reference oracle (Jackson), which emits them literally. The default
`System.Text.Json` encoder escapes them to `\uXXXX`, which diverges from the oracle and, worse, breaks
Handlebars template expressions embedded in a `jsonBody`: WireMock's common
`{{jsonPath request.body '$.field'}}` uses **single-quoted** helper arguments, and `'` → `'`
makes the argument unparseable, so the value rendered empty (and `now format='…'` / `randomValue
type='…'` silently fell back to defaults).

- **Fix:** serialize `jsonBody` with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`.
- **Regression:** `G2jTemplatedJsonBodyTests` — a templated `jsonBody` echoing `request.body` via a
  single-quoted `jsonPath` argument is byte-diffed against the oracle.

## Global response templating (`--global-response-templating`, #148)

- **The flag mirrors the reference host's:** every response renders through the templating engine
  regardless of the per-stub `transformers` list. This matches the export shape of hosts that run
  with global templating on — their mappings carry `{{…}}` bodies with **no** transformers entry,
  which the per-stub opt-in mode would serve as literal text.
- **Verified differentially:** the oracle container is started with `--global-response-templating`
  (the harness's `WireMockOracle` now accepts extra command args) and a mapping whose body/headers
  are templated but transformer-less renders **byte-identically** on both sides.
- **Off by default:** without the flag the per-stub opt-in behavior is byte-for-byte unchanged
  (covered by the existing G2 suite; the new test also pins the literal-text case).
- **Placement:** the flag configures `TemplatingResponseRenderer(globalTemplating: true)` at the
  host edge; the engine and the per-stub semantics are untouched.
- **Regression case:** `G2iGlobalTemplatingTests`.

## `bodyFileName` — file-backed response bodies (post-1.0)

The longest-standing gap in the response surface, and the one that blocked migrating an existing
mapping set wholesale. A stub may now name a file instead of inlining its body; the host serves it
from `<root-dir>/__files`.

The design questions were answered by the oracle rather than by reading anyone's documentation — the
harness maps files into the container's `__files` and hands Mockifyr the same content, so
`bodyFileName` is diffable like any other behaviour:

- **A file-backed body is templated when `response-template` is declared, and not otherwise.** The
  transformer applies to what the stub serves, not to where the bytes came from.
- **An inline `body` wins over `bodyFileName`** when a stub carries both.
- **A missing file is a 500, not an empty body.** This one changed the implementation: the first cut
  degraded to an empty body, the oracle answered 500, and the oracle is right — an empty 200 reads as
  a matching problem when it is a misconfigured deployment. It is exactly the silent-failure shape 1.0
  set out to remove. Mockifyr's 500 names the missing file; the oracle's HTML error page is its own
  presentation, not dialect, so the **status** is what is compared.

Design notes:

- **Core does no I/O.** `ResponseDefinition.BodyFileName` is a name and `IResponseBodyFiles` is a seam;
  the host binds it to a directory, a library embedding could bind it to anything, and without a
  `--root-dir` there is no store — which is why the import warning for this field existed before and
  is gone now.
- **Resolved per request**, so editing a fixture changes the next response with no reload. That is the
  reference behaviour and what anyone iterating on a body expects.
- **The name is treated as hostile.** It comes from a stub, and a stub can be authored by anyone who
  can reach the admin API. The resolved path must sit inside the root or nothing is served: `../`,
  `nested/../../`, absolute paths, and the sibling-directory-sharing-a-prefix trick (`/data-evil`
  passing a naive `StartsWith("/data")`) each have their own test. Without that check a mock host is
  an arbitrary file-disclosure endpoint.

Validation: 5 differential tests against the oracle (plain, templated, un-templated, precedence,
missing-file) plus 13 unit tests on the store, most of them names trying to escape it.

**Stryker: 12/17.** The five survivors are behaviour-equivalent and are documented rather than chased:
the empty-name early-out (the boundary check refuses it anyway — the guard exists so an empty name
does not log a traversal refusal), the `Console.WriteLine` refusal notice and its message string (log
text, not outcome), and `File.Exists(...) ? read : null` (without the check a missing file throws
`FileNotFoundException` and a directory throws `UnauthorizedAccessException`, both of which the catch
already turns into the same `null`). Each changes cost or log output, none changes what is served.

Still deferred: `__files` extraction during recording, and `bodyFileName` in recorded stubs.

## The `math` helper — two corrections found by probing (post-1.0)

The limitations page listed `add`, `subtract`, `multiply`, `divide`, `round` and `abs` as helpers
Mockifyr lacks. Probing the oracle showed it **rejects every one of them** too (500) — so that was
never a gap, and implementing them would have been a divergence rather than parity. The page said the
opposite of the truth; it now says what was measured.

The same probe found two real defects, both in `math`:

- **`%` is supported by the oracle** (`{{math 7 '%' 2}}` → `1`), and Mockifyr rejected it — with a
  code comment asserting the oracle did not support it. It does. Implemented, with the sign following
  the dividend (`-7 % 2` → `-1`), which is what both runtimes do.
- **Integer division rounds half away from zero.** Mockifyr used `Floor(x + 0.5)`, which agrees for
  every positive operand and is wrong for every negative half: `-9/2` answered `-4` where the oracle
  answers `-5`. The differential suite had no negative-division case, which is why this survived from
  G2 to 1.1.

Both are now pinned by 16 differential cases covering positives, negatives, halves, and decimal
operands (a decimal operand does not round at all).

Stated divergence: division or modulo by zero makes the oracle answer **500**; Mockifyr renders an
empty value instead, so a template mistake cannot take down the request path.


## Tenant clock control (#290, post-1.0)

- **Group / item:** post-roadmap platform feature — **self-tested**; the reference engine has no clock
  surface, so per the G18 honesty rule there is no oracle and the validation method is stated here.
- **The problem.** Serve-time `now` was the host's clock, so every date-dependent integration was
  testable only in real time: a token that expires in an hour, a statement that closes at month end, a
  trial that ends in fourteen days. The workarounds — make the test wait, or hardcode dates that rot —
  are both worse than the feature.
- **Shape.** `PUT /__admin/clock` with `{"frozenAt": "<ISO-8601>"}` or `{"offsetSeconds": <signed>}`,
  `GET` to read it back (`mode` is `real` / `frozen` / `offset`), `DELETE` to return to real time.
  Tenant-scoped, like everything else.
- **The two modes are exclusive, and a body carrying both is refused** (`Clock.Ambiguous`, 422). "Frozen
  *and* drifting" has one meaning to whoever wrote it and another to whoever reads it next; stepping a
  frozen clock forward is a new `frozenAt`, which says what it means.
- **One instant for the whole render.** The renderer publishes the tenant's instant once and `now`, the
  date helpers and a minted JWT all read it — a response whose body said one time while its own token
  said another would be worse than having no clock control. Webhook templates read the same instant, so
  a callback agrees with the response that triggered it.
- **Deliberately in-memory.** Not persisted, not on the change feed: a host that came back from a
  restart still believing it is 2027 would be a bug report rather than a convenience.
- **What the clock does NOT move:** the request journal, the audit trail, the message inbox, API-key
  timestamps and the rate-limit window. Those record when something *actually* happened, and a forensic
  record that follows a test's fiction is worthless. Asserted, not merely intended.
- **Implementation note.** Handlebars helpers are registered once on a shared engine — that is what
  makes the compiled-template cache (#266) worth having — so the instant travels through a
  `[ThreadStatic]` scope rather than a helper parameter. A render is synchronous end to end, so there is
  no continuation to flow across, and an `AsyncLocal` write would copy the execution context on a path
  measured in microseconds. The `finally` is load-bearing: a render that throws must not leave the next
  request on that thread in 2027.
- **This makes a racy helper deterministic.** `now` was previously validated only structurally (correct
  format, plausible value) because it cannot be byte-diffed against an oracle whose clock moves. With a
  frozen clock its output is exact, and the wire suite asserts two reads a real second apart agree.
- **Performance.** `MatchAndRenderTemplate` measured **1.184 µs** with the scope in place against the
  **1.21 µs** recorded in `docs/parity/performance.md` — no regression on the default path, which is the
  one a host that never sets a clock pays for.
- **Regression cases:** `TenantClockTests` (10 wire cases incl. tenant isolation, JWT agreement, the
  journal keeping real time, both refusals, and the untouched default), `ClockOverrideTests` (7) and
  `ClockStoreTests` (9). **Stryker 100 %** on `ClockOverride` and `InMemoryClockStore`.
