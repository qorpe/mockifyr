# Support

## Where to ask

| You want to | Go to |
|-------------|-------|
| Ask how something works, or whether Mockifyr can do X | [Discussions](../../discussions) |
| Report a bug | [Issues](../../issues/new) |
| Request a feature | [Issues](../../issues/new) |
| Report a security vulnerability | **Not** an issue — see [SECURITY.md](SECURITY.md) |

Before opening a bug, the [known limitations](https://mockifyr.qorpe.com/limitations/) page is worth a
look: some behavior that reads like a bug is a documented, deliberate edge.

## What to expect

Mockifyr is developed in the open by a small team, so the honest answer about response times is:

- **Security reports** are the priority and have a stated timeline in [SECURITY.md](SECURITY.md)
  (acknowledgement within 72 hours).
- **Bugs** are usually triaged within a few working days. A report with a reproduction — the mapping
  JSON, the request, and what you expected — gets fixed far faster than a description, because the
  first thing that happens is someone tries to reproduce it.
- **Feature requests** are read and labelled, but land against the [roadmap](docs/roadmap.md) rather
  than in arrival order.

There is no paid support tier and no SLA. If you are evaluating Mockifyr for something that needs one,
say so in a discussion — it is useful signal.

## What is in scope

**In scope:** the engine, the admin API, the dashboard, the container image and Helm chart, the
persistence providers, and the documented behavior of every CLI flag.

**Out of scope:** your test suite, your CI, and your network. We will happily help interpret what
Mockifyr reported — a near-miss diagnosis, an audit entry, a 429 — but debugging the system under test
is yours.

## Which version fixes land on

Fixes land on the **latest release**. There are no long-term-support branches: with a single-process
tool whose state is portable through [backup and restore](https://mockifyr.qorpe.com/deploying-in-production/),
upgrading is normally a new image tag, and [VERSIONING.md](VERSIONING.md) states exactly what an
upgrade can and cannot change.

If an upgrade is genuinely not possible for you, open a discussion — a backport is a conversation, not
a policy.
