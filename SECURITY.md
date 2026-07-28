# Security Policy

## Reporting a vulnerability

Please **do not open a public issue** for anything security-sensitive.

Use GitHub's private vulnerability reporting instead: **[Report a vulnerability](../../security/advisories/new)**
(repository → Security → Report a vulnerability). Reports go only to the maintainers, and triage
happens before anything is public.

If you cannot use GitHub, email **omer@omercelik.dev** with the details.

## What to include

- The affected surface (admin API, mock serving, dashboard, a persistence backend, sandbox keys, …)
  and the version or commit.
- Reproduction steps or a proof of concept — a failing request/response pair is ideal.
- Your assessment of impact, if you have one (tenant isolation, credential exposure, RCE, DoS, …).

## What to expect

- Acknowledgement within **72 hours**.
- An assessment and remediation plan within **7 days** for confirmed findings.
- A fix released and credited to you (unless you prefer otherwise) before any public disclosure;
  we ask for coordinated disclosure and will agree on a timeline with you.

## Supported versions

Security fixes land on the latest minor release line. Older tags are not patched — upgrade to the
newest release to receive fixes.

## Scope notes

- Mockifyr is a development/sandbox tool; deployments exposing `/__admin/*` publicly should always
  set `--admin-user`/`--admin-pass` and front the host with TLS. Reports assuming an intentionally
  open admin surface on a public network are still welcome, but hardening guidance in the
  [docs](https://mockifyr.omercelik.dev/securing-the-admin-api/) is the first line of defense.
- Findings against the tenant-isolation invariants (one tenant reading another's stubs, journal,
  messages, resources, or keys) are always in scope and treated as high severity.
