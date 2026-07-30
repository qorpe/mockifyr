# Versioning, compatibility and support

Mockifyr follows [semantic versioning](https://semver.org/). This document says what a version number
actually promises, so you can answer "will this upgrade break me?" without reading the diff.

## What a version number means

| Change | Version |
|--------|---------|
| A behavior you could reasonably depend on stops working the way it did | **major** |
| New capability, or a change that only adds a way to do something | **minor** |
| A fix that makes existing behavior match its documentation | **patch** |

While Mockifyr is on **0.x**, the minor number carries what would otherwise be a major: `0.21 → 0.22`
may contain a breaking change, and the release notes say so at the top when it does. From **1.0**
onwards the table above applies literally.

## The four surfaces, and what "breaking" means for each

Compatibility is not one promise; it is four, because the things you depend on have different
lifetimes.

### 1. The mapping JSON dialect

The stub format is what your files are written in and the most expensive thing to change, so it is
the most conservative surface.

**Breaking:** a mapping that used to load now fails, or matches differently. **Not breaking:** a new
matcher, a new response directive, or a new optional field — a stub that does not use it is
unaffected.

Fields Mockifyr does not model are preserved verbatim through export and backup, so a mapping written
for a newer version keeps its unknown fields when an older host has touched it.

### 2. The admin API

**Breaking:** a route is removed, its method changes, a response field disappears, or a status code
changes for an outcome you could depend on. **Not breaking:** a new route, a new response field, a
new optional query parameter, or a new error code for a case that previously failed some other way.

Error *codes* (`Backup.Invalid`, `ApiKey.NotFound`) are part of the contract; error *messages* are
written for humans and may be reworded in any release.

### 3. CLI flags and configuration

**Breaking:** a flag stops being accepted, or its default changes in a way that changes behavior.
**Not breaking:** a new flag, or a new alias for an existing one.

**Flags are never simply removed.** When one is renamed, the old name keeps working as an alias — as
`--max-request-journal-entries` does for `--journal-limit`, and `--no-request-journal` for
`--journal-disabled`. An alias is announced as deprecated in the release notes and removed no earlier
than the next major version. If you are reading the release notes, you get at least one full major
line of warning.

### 4. The dashboard

The dashboard is a client of the admin API and carries no separate compatibility promise: screens,
layout and wording change freely between releases. Anything you automate against should go through
the admin API, not the UI.

## Defaults that have changed

A changed default is the kind of "not technically breaking" change that still surprises people, so
each one is listed here.

| Version | Default | Before | Now | Why |
|---------|---------|--------|-----|-----|
| 0.18.0 | Request journal size | unbounded | 1000 entries per tenant, oldest evicted | An unbounded journal is a slow memory leak on a long-running host. Set `--journal-limit 0` for the old behavior. |

## Upgrading

Mockifyr is a single process with optional durable state, so an upgrade is usually just a new image
tag. Two habits make it uneventful:

- **Take a backup first.** `GET /__admin/backup` per tenant — see
  [deploying in production](https://mockifyr.qorpe.com/deploying-in-production/). Restoring into a
  fresh host on the new version is the rollback plan.
- **Read the release notes for the minor you are crossing**, not just the latest. Breaking changes and
  changed defaults are called out at the top of the notes for the release that made them.

Downgrading is supported through the same backup: the archive format carries a version number, and a
host refuses an archive written by a newer Mockifyr rather than guessing at its contents.

## Supported versions

Mockifyr is developed in the open by a small team. The honest support statement is in
[SUPPORT.md](SUPPORT.md); the short form is that fixes land on the **latest release**, and security
issues are handled under [SECURITY.md](SECURITY.md).
