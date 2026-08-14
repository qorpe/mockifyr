# The deferred-edge register

Every gap that is still open, in one place, each with a verdict. This exists because the per-group
parity documents record deferrals *as of the group that wrote them* — many say "deferred" about
something delivered two releases later, so reading them end to end gives the wrong count. That is not
a hypothetical: it happened while reviewing the 1.x line, and the answer to "is anything open?" was
wrong until this page existed.

**Verdicts**

| | Meaning |
|---|---|
| **Out of scope** | Not a gap. The reference engine rejects it too, or it cannot be expressed here. Implementing it would be a divergence, not parity. |
| **Tracked** | A real gap with an issue. It will be done when it is worth doing. |
| **Accepted** | A real limitation we do not intend to close, with a reason. |

Last reviewed: **1.7.0**.

## Out of scope — verified against the reference engine

Each of these was driven through the oracle. It rejects them, so building them would move Mockifyr
*away* from the behaviour it is tested against.

| Item | Evidence |
|------|----------|
| `clientIp` matcher | Oracle rejects the mapping (commercial cloud feature) |
| `equalToNumber`, `greaterThanNumber` and siblings | Same |
| `add`, `subtract`, `multiply`, `divide`, `round`, `abs` helpers | Oracle answers **500** for each; `math` is the supported spelling on both sides |
| `removeProxyRequestHeaders` | The oracle still forwards the header, so honouring the field would diverge |
| JSON Schema Draft 4 (`V4`) | Unsupported by the schema library; no oracle-equivalent behaviour to match |
| Byte-level fault fidelity | A socket-level behaviour an in-process differential comparison cannot express |

## Tracked — real gaps with issues

| Gap | Why it is open | Issue |
|---|---|---|
| Resource embedding (`?expand=customer`) | ADR 0015 was its precondition and is met. Deliberately not bundled with relations: those were a defect fix and this is a feature, and shipping them together would have made the fix impossible to review. | [#378](https://github.com/qorpe/mockifyr/issues/378) |

Two entries left this section by shipping: **resource querying**
([#353](https://github.com/qorpe/mockifyr/issues/353)) and the **relations dashboard screen**, both in the
G22 epic; before them, change-feed reload for environments and sandbox resources
([#279](https://github.com/qorpe/mockifyr/issues/279)) — see `g16-persistence.md` for what that fix turned
up along the way.

A third left it by being re-filed rather than done: embedding was tracked against
[#350](https://github.com/qorpe/mockifyr/issues/350), which shipped relations and was closed, so the row
pointed at an issue that no longer accepted work. That is the one failure a register cannot have — a gap
that reads as tracked and is not — so it now has [#378](https://github.com/qorpe/mockifyr/issues/378) of
its own. Worth stating as a rule: when an issue closes, every register row citing it is either done or
needs somewhere else to live.

A row belongs here when a gap is real, reachable by a documented deployment, and not yet closed. An empty
section is the honest state, not an oversight — the sections below are where the standing limitations
live.

## Accepted — limitations we do not intend to close

Each of these is a decision, not a backlog item. If one blocks you, say so on an issue: demand is what
should move it.

**Recording.** `filters`, `allowNonProxied`, `__files` extraction, response transformers on generated
stubs, and repeat-requests-become-a-scenario. Recording is a bootstrapping convenience; a stub set is
expected to be curated after capture, and each of these adds shape to output that a human is about to
edit anyway.

**Audit trail.** Entries record the operation, not a before/after diff of the changed stub — a diff
would mean materialising every prior state, and the export bundle already answers "what does it look
like now". Entries are not hash-chained: the `admin.audit` log line is the tamper-evident copy, and
chaining is only worth it if the in-memory trail ever becomes the system of record.

**Backup.** No host-wide archive (each tenant is backed up on its own, which keeps the tenant boundary
intact) and no incremental backups. The archive is not encrypted at rest — it is a file you store
wherever your secrets already live.

**Cryptography.** Asymmetric signatures, the full Berlin Group signing string, wrapped-key JWE, and
per-tenant keys. The shipped shape covers the integration patterns that prompted G20; each of these is
a larger scheme that deserves its own vertical rather than an extension of this one.

**OIDC.** Token refresh (an expired session returns the user to sign-in), back-channel logout, and
mapping claims to anything finer than a tenant.

**Matching and templating details.** `equalToIgnoreCase` as a key (use `equalTo` with
`caseInsensitive`); an empty request body counting as absent; multi-value header matching not claimed
(only query parameters are verified); `matchesJsonPath` filter functions such as `.length()`; explicit
XML `namespaceAwareness` modes and mixed content; `now`-relative date matching, `expectedOffset` and
truncation options; `systemValue` being deny-by-default with no allowlist; Faker expressions taking
arguments and locale selection; JWT limited to HS256/RS256 with no configurable secret, `nbf`, or
array/object claims; no `soapXPath`.

**Deployment.** No NetworkPolicy example, PodDisruptionBudget or HPA guidance in the chart — cluster
policy differs enough between organisations that a shipped example would be wrong more often than
right.

**Equal priorities.** Tie-breaking among stubs with the same priority is load-path dependent. Give
stubs distinct priorities when order matters.

## How to use this page

When a change closes one of these, delete the row here **and** update the per-group parity document.
When a new deferral is created, add it here with a verdict — a deferral recorded only in a group
document is how this page came to be needed.
