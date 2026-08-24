# Parity Knowledge Base

This directory is the **memory of what we have learned about real WireMock's behavior** —
verified against the running oracle (Java WireMock), not assumed.

## Why this exists

Parity is not "is our matcher correct?" — it is "does our behavior match WireMock's,
including the quirks of the underlying Java libraries" (Handlebars.java, Jayway JsonPath,
Saxon/JAXP XPath, XMLUnit, the JSON comparator). Every time a differential diff reveals a
non-obvious WireMock behavior, we write it down here so the next roadmap item is built on
evidence instead of guesswork. The repo gets smarter over time.

## The register

**[deferred-edges.md](deferred-edges.md) is the single answer to "is anything still open?"** The
per-group files below record deferrals *as of the group that wrote them*, so many of them say
"deferred" about something delivered two releases later — reading them end to end gives the wrong
count, which is exactly how a review of the 1.x line got that answer wrong. Every open gap lives in
the register with a verdict; every group file keeps its narrative.

When a change closes a gap, update **both**.

## How to use it

- One file per roadmap group: `g1-matching.md`, `g2-templating.md`, `g3-webhooks.md`, …
- Each entry records: the behavior, the exact input that triggered it, WireMock's output,
  the source library if known, and the failing case id in the regression suite.
- Add an entry the moment a diff surprises you — during the "feed the docs" step of the
  development loop (see [../../CLAUDE.md](../../CLAUDE.md#3-the-development-loop-per-roadmap-item)).

## Entry template

```markdown
### <short title>

- **Group / item:** G1e (equalToJson)
- **Input:** <the request + stub that triggered it>
- **WireMock behavior:** <what the oracle actually does>
- **Source:** <Jayway JsonPath / Handlebars.java / XMLUnit / ... if known>
- **Our handling:** <how Mockifyr matches it>
- **Regression case:** <id in the differential suite>
```

Files are created as their groups are implemented; none exist yet because no code has been
written.
- [deployment.md](deployment.md) — container/K8s deployment parity notes (ports, healthchecks, persistence mounts).
