# G16 — Persistence providers

Durability across restarts: stub mutations made over the admin API can be persisted so a fresh
process serves them again. Persistence is infrastructure — there is no WireMock *response* semantic to
diff — but the reloaded stub's **served response is still diffed against the oracle**, so parity is
proven rather than assumed. The stores stay tenant-scoped; a persistence provider is registered behind
the same seam.

## File-based persistence (G16a)

- **Group / item:** G16a — durability validated in-process; reloaded-response parity diffed against the oracle.
- **`IStubPersistence` seam.** A Core extension seam (`Save`/`Remove`/`Clear`, tenant-scoped) that the
  management-path handlers call alongside the in-memory store. The default is `NullStubPersistence`
  (no-op — purely in-memory, nothing survives a restart). A provider registered on top wins the DI
  resolution.
- **`--root-dir` turns it on.** `MockifyrHost` registers `FileSystemStubPersistence` when `--root-dir`
  is given — persisting to the **same** `<root>/mappings` directory that `DirectoryMappingsLoader`
  (G12f) reloads on startup. This is exactly WireMock's `--root-dir` model: the persistence directory
  *is* the load directory.
- **Id stability across restart.** The reader mints a fresh id when a mapping has none
  (`ReadId` → `Guid.NewGuid()`), so the provider **stamps the stub's id** (`id` + `uuid`) into the
  saved JSON before writing `<id>.json`. A reload therefore keeps the same id — a create-then-restart
  serves the identical stub with the identical id. `ReadWithSource` returns each mapping's own source
  JSON (even from a `{"mappings":[…]}` bundle) so imports persist each element faithfully.
- **Mutations covered.** Create, import (each bundle element), delete (removes the file), and
  mappings-reset (clears the provider's `<guid>.json` files, leaving any hand-authored files alone).
- **Validation.** Over the admin API: create a stub on a host with `--root-dir`, shut it down, confirm
  the file was written, start a **fresh** host on the same dir, and serve the reloaded stub — its
  response matches the oracle's for the same mapping. Delete + reset are confirmed to stay gone after a
  restart.
- **Deferred (explicitly tracked — not a silent gap):** multi-tenant persistence *reload* (non-default
  tenants are written to per-tenant subdirectories, but startup only reloads the default tenant's
  flat dir); WireMock's `persistent:false` opt-out (all admin mutations persist when a root-dir is
  set); and the other providers — **LiteDB (G16b), Postgres (G16c), Redis (G16d)** — plus
  **change-feed reload (G16e)**, each behind this same seam.
- **Regression cases:** `G16aPersistenceTests.CreatedStub_SurvivesRestart_AndMatchesOracle`,
  `G16aPersistenceTests.DeletedStub_And_Reset_StayGoneAfterRestart`.

## LiteDB persistence (G16b)

- **Group / item:** G16b — durability validated in-process; reloaded-response parity diffed against the oracle.
- **Second provider, same seam.** `LiteDbStubPersistence` implements the same `IStubPersistence`
  contract as the file provider — proving the seam is genuinely multi-provider (retrofit-free, as the
  architecture intended). Each stub is one document `{ Id, Tenant, Json }` in an embedded single-file
  [LiteDB](https://www.litedb.org/) database; the stored JSON is id-stamped (shared `PersistableJson`
  helper) so ids round-trip identically to the file backend. `LiteDbMappingsLoader` is the
  `IMappingsLoader` counterpart that reloads the tenant's documents on startup.
- **`--litedb <path>` turns it on.** `MockifyrHost` registers the provider + loader against a shared
  `LiteDatabase` — created by DI as a singleton so the container disposes it on shutdown (flushing the
  file before the next process opens it). Storing the raw id-stamped JSON keeps persistence faithful
  without a domain → JSON serializer, exactly like the file backend.
- **Validation.** Mirrors G16a over the admin API: create on a host with `--litedb`, shut it down,
  confirm the db file exists, start a fresh host on the same file, serve the reloaded stub — its
  response matches the oracle. Delete + reset stay gone after a restart.
- **Deferred (tracked):** the remaining providers — **Postgres (G16c), Redis (G16d)** — and
  **change-feed reload (G16e)**, plus the multi-tenant-reload / `persistent:false` items noted under
  G16a.
- **Regression cases:** `G16bLiteDbPersistenceTests.CreatedStub_SurvivesRestart_AndMatchesOracle`,
  `G16bLiteDbPersistenceTests.DeletedStub_And_Reset_StayGoneAfterRestart`.

## PostgreSQL persistence (G16c)

- **Group / item:** G16c — durability validated against a real Postgres container; reloaded-response parity diffed against the oracle.
- **Third provider, a SQL backend.** `PostgresStubPersistence` implements the same `IStubPersistence`
  seam via Npgsql. Each stub is a row `(id uuid, tenant text, json text)`; the stored JSON is
  id-stamped (shared `PersistableJson`) so ids round-trip identically to the file/LiteDB backends.
  `Save` is an `INSERT … ON CONFLICT (id) DO UPDATE` upsert; connections open per operation from
  Npgsql's pool (thread-safe). `PostgresMappingsLoader` reloads the tenant's rows on startup.
- **Schema.** A shared `PostgresSchema.Ensure` runs `CREATE TABLE IF NOT EXISTS` from both the provider
  and the loader constructors, so whichever is resolved first (the loader runs at startup, before any
  mutation) finds the table in place.
- **`--postgres <connection-string>` turns it on.** Unlike the file/LiteDB backends, the durable store
  is an external database that outlives the app process — so a "restart" is just a fresh host pointed
  at the same connection string; the data was never in-process.
- **Validation.** A real `postgres:16-alpine` container (Testcontainers) alongside the WireMock oracle:
  create on a host with `--postgres`, shut it down, start a fresh host on the same database, serve the
  reloaded stub — its response matches the oracle. Delete + reset stay gone after a restart.
- **Deferred (tracked):** **Redis (G16d)** and **change-feed reload (G16e)**; connection-string
  secrets/config hardening is a deploy concern.
- **Regression cases:** `G16cPostgresPersistenceTests.CreatedStub_SurvivesRestart_AndMatchesOracle`,
  `G16cPostgresPersistenceTests.DeletedStub_And_Reset_StayGoneAfterRestart`.

## Redis persistence (G16d)

- **Group / item:** G16d — durability validated against a real Redis container; reloaded-response parity diffed against the oracle.
- **Fourth provider, a key-value backend.** `RedisStubPersistence` implements the same
  `IStubPersistence` seam via StackExchange.Redis. Each tenant's stubs live in one Redis hash
  (`mockifyr:stubs:{tenant}`) keyed by stub id, the value being the id-stamped WireMock JSON (shared
  `PersistableJson`) so ids round-trip identically to the file/LiteDB/SQL backends. `Save` → `HSET`,
  `Remove` → `HDEL`, `Clear` → `DEL` the tenant's hash. `RedisMappingsLoader` `HGETALL`s the tenant's
  hash on startup.
- **`--redis <connection-string>` turns it on.** The `IConnectionMultiplexer` (thread-safe, long-lived)
  is a DI-created singleton so the container disposes it on shutdown. Like Postgres, the store is
  external and outlives the app process — a "restart" is a fresh host on the same connection string.
- **Validation.** A real `redis:7-alpine` container (Testcontainers) alongside the WireMock oracle:
  create on a host with `--redis`, shut it down, start a fresh host on the same instance, serve the
  reloaded stub — its response matches the oracle. Delete + reset stay gone after a restart.
- **Deferred (tracked):** **change-feed reload (G16e)** — the last G16 slice: a live host reloading its
  store when another writer changes it (multi-instance coherence).
- **Regression cases:** `G16dRedisPersistenceTests.CreatedStub_SurvivesRestart_AndMatchesOracle`,
  `G16dRedisPersistenceTests.DeletedStub_And_Reset_StayGoneAfterRestart`.

## Change-feed reload (G16e)

- **Group / item:** G16e — multi-instance coherence validated with two live hosts sharing Redis. Closes the **G16** group.
- **The problem.** With a shared external store, a second instance loads the current state on startup
  (G16b–d) but does not see *later* changes another instance makes — its in-memory store drifts.
- **Redis pub/sub reload.** Every `RedisStubPersistence` mutation *announces* on a pub/sub channel
  (`mockifyr:changes`) — a publish with no subscribers is a cheap no-op, so it is always safe to emit.
  `--change-feed` opts a host into a `RedisChangeFeedReloader` (an `IHostedService`) that subscribes to
  the channel and, on any announcement, **reloads** the default tenant from the mappings loaders and
  reconciles the store: upsert what's persisted first (no empty window where a live request could miss
  a match), then prune what's gone. So a stub created (or deleted) on one instance is served (or
  stopped) by the others without a restart.
- **Validation.** Two live Mockifyr hosts share one `redis:7-alpine` container with `--change-feed`:
  a create on host A propagates to host B (B starts serving `/cf`), and a delete on A propagates too
  (B stops serving). Propagation is asynchronous (pub/sub), so the assertions poll within a timeout.
  Coherence is infrastructure — served-response parity is already oracle-covered (G16d) — so no oracle
  is needed here.
- **Regression case:** `G16eChangeFeedTests.Mutation_On_One_Instance_Propagates_To_Another`.

## Postgres change-feed reload (G16f)

- **Group / item:** G16f — the same multi-instance coherence as G16e, over PostgreSQL `LISTEN`/`NOTIFY`
  instead of Redis pub/sub.
- **`LISTEN`/`NOTIFY` reload.** Every `PostgresStubPersistence` mutation runs `NOTIFY mockifyr_changes`
  on its connection right after the write (a `NOTIFY` with no listener is a cheap no-op, so it is always
  safe to emit). `--change-feed` opts a Postgres-backed host into a `PostgresChangeFeedReloader` (an
  `IHostedService`) that holds a dedicated connection, `LISTEN mockifyr_changes`, and — driven by a
  background loop calling Npgsql's `WaitAsync` (notifications are only delivered while a wait is in
  flight) — reconciles the store on every notification via the **shared** `ChangeFeedReconciler` (upsert
  then prune, the same logic G16e uses). So a mutation on one instance is served (or stopped) by the
  others without a restart.
- **Validation.** Two live Mockifyr hosts share one `postgres:16-alpine` container with `--change-feed`:
  a create on host A propagates to host B, and a delete on A propagates too. Propagation is asynchronous,
  so the assertions poll within a timeout. Like G16e this is coherence infrastructure (served-response
  parity is oracle-covered by G16c), so no oracle is needed.
- **Regression case:** `G16fPostgresChangeFeedTests.Mutation_On_One_Instance_Propagates_To_Another`.

## Multi-tenant change-feed reload (G16g)

- **Group / item:** G16g — generalizes the change-feed reconcile from the default tenant to **every**
  tenant. Closes the last persistence edge.
- **The gap.** G16e/G16f reconciled only `TenantId.Default`, so a stub persisted for another tenant by a
  peer instance would not appear (and a non-default tenant emptied elsewhere would not be pruned).
- **All-tenant reconcile.** `IStubStore` gained `GetTenants()` (the tenants currently holding stubs) and
  a loader may implement the optional `IMultiTenantMappingsLoader.LoadAllTenants()` (the DB/KV backends —
  Postgres `SELECT tenant, json FROM stubs`; Redis a `SCAN` over `mockifyr:stubs:*`, the tenant being the
  key suffix). The shared `ChangeFeedReconciler` now gathers persisted stubs across all tenants (multi-
  tenant loaders enumerate every tenant; single-tenant loaders like a mappings directory contribute the
  default tenant only), then reconciles the **union** of the reloaded tenants and the store's current
  tenants — upsert-then-prune **per tenant**, so cross-tenant state stays isolated.
- **Validation.** One live host on Postgres `--change-feed`; a tenant-aware peer writer persists stubs for
  two non-default tenants (`acme`, `globex`). The host reloads all tenants and serves each under its
  `X-Mockifyr-Tenant` header; emptying `acme` prunes it while `globex` is untouched. (Admin tenant
  resolution is still a placeholder—default tenant—so non-default tenants are written via the persistence
  seam, which is how a tenant-aware peer would.) Coherence infrastructure, so no oracle.
- **Regression case:** `G16gMultiTenantReloadTests.Reload_Reconciles_All_Tenants_Independently`.

## Environments and sandbox resources on the change feed (#279)

- **Group / item:** post-1.0 — the half of the feed that was missing. Self-tested against both shared
  backends; coherence is infrastructure, so no oracle applies.
- **The gap.** The feed carried *stubs only*. Environment keys (G17) and sandbox documents (G19a) were
  persisted to the same shared backend and reloaded at startup, but never propagated: a second replica
  kept serving the old value until it restarted. The deployment guidance recommends scaling out behind
  one Postgres or Redis, so this was reachable by following our own advice — and it presents as
  non-deterministic traffic, since an operator flipping a key sees the change honoured by some replicas
  and not others.
- **What was actually missing was the announcement, not the reconcile.** Extending `ChangeFeedReconciler`
  was the easy half. `PostgresEnvironmentPersistence`, `PostgresResourcePersistence` and their Redis
  counterparts emitted **nothing** on write — only the stub providers announced. The announcement now
  lives in one place (`ChangeFeedAnnouncement`) rather than on the stub classes, because it stopped being
  about stubs; all three kinds share one channel, since a change to any of them is rare next to the
  request traffic it affects and three channels would buy precision nobody is measuring.
- **Reload restores, it does not re-write.** Replaying another instance's document through
  `IResourceStore.Put` would advance its version and stamp a local `UpdatedAt`, so the same document
  would report **different versions on two replicas of one backend** — a difference a client can read.
  Hence `IResourceStore.Restore`, which writes a document exactly as persisted, alongside `GetTenants()`
  so a tenant emptied elsewhere can be pruned (`IEnvironmentStore` already had `GetTenants` from G16g).
  The per-collection bound still applies to a restore: a document arriving over the feed is still a
  document arriving.
- **A host must ignore its own announcements — found by testing, not by reasoning.** The wire test
  asserting a writer reads back its own version failed: a host hears its own change, and the reload it
  triggers can read the backend *before* the next local write lands and then restore that older view over
  it. The operator gets their own change handed back at the previous version, and nothing announces again
  to correct it. Every announcement now carries a `ChangeFeedIdentity` payload (`pg_notify`'s payload
  parameter; the Redis message) and a host skips its own. The identity is **per host, not per process** —
  two hosts in one process (the test suite, and anyone embedding Mockifyr twice) would otherwise go deaf
  to each other, which is the same bug wearing the fix's clothes. An unidentified announcement is always
  processed: missing a real change is the worse failure.
- **Reloads are serialized per host.** Both transports can deliver announcements concurrently; two
  overlapping reloads read the backend at different instants and the later read can finish first, leaving
  the host holding the older view with nothing left to announce.
- **A pull is not an announcement.** Git sync reconciles *stubs alone* (`ReloadStubs`): the remote tree is
  mapping files and carries no opinion about environment keys or sandbox documents, so reconciling them
  against it would prune state the remote never described.
- **Validation.** One suite run against **both** backends (`redis:7-alpine` pub/sub and
  `postgres:16-alpine` `LISTEN`/`NOTIFY`), because the defect was per-provider — proving one backend
  would have proven half a fix. Two live hosts, one backend: a key written on A is resolved by B; flipping
  its active value moves both; deleting it prunes it from B; a document written on A is readable on B at
  **A's** version and survives unrelated reloads at that version; a delete on A prunes it from B; and two
  tenants holding the same collection and id stay apart through reload and prune. Every cross-instance
  assertion re-reads after polling, and the delete cases assert the item *arrived* first — otherwise
  "gone" and "never propagated" are indistinguishable from the far end. All twelve fail against the
  pre-fix engine.
- **Mutation testing.** Stryker on `InMemoryResourceStore`: 34 mutants, **0 survivors** (100%).
- **Regression cases:** `RedisChangeFeedEnvironmentResourceTests` and
  `PostgresChangeFeedEnvironmentResourceTests` (six cases each), plus `ResourceStoreRestoreTests` for the
  store logic beneath them.

## Validated Git sync over the root-dir (post-G16 / issue #143, ADR 0007)

- **Group / item:** post-roadmap platform feature — **self-tested** (WireMock has no Git surface, so
  there is no oracle to diff; parity of the *served* stubs is already covered by G16a).
- **Shape.** `--git-remote <url>` (+ `--git-branch`, default `main`) turns the `--root-dir` working
  copy into a Git-synced stub set. Sync is explicit: `GET /__admin/git/status`,
  `POST /__admin/git/push` (`{"message": …}` optional), `POST /__admin/git/pull`. The host shells out
  to the plain `git` binary — every provider (GitHub/GitLab/Bitbucket/self-hosted, HTTPS or SSH)
  behaves identically. HTTPS tokens come from `MOCKIFYR_GIT_TOKEN` (+ optional
  `MOCKIFYR_GIT_USERNAME`) via an inline credential helper — never argv, never disk, and error
  output is scrubbed of the token and URL userinfo.
- **Safety invariants (each is a regression case in `GitSyncTests`):**
  - *Pull validates before it applies*: every `mappings/**/*.json` blob in `FETCH_HEAD` is parsed
    with the strict admin-path reader **before** the working tree moves; one bad file rejects the
    pull wholesale (`Git.InvalidMappings` lists the files) and neither the tree nor the served
    stubs change.
  - *Fast-forward only*: push checks the remote **before** committing, so `Git.RemoteAhead`
    ("pull first") leaves the working copy untouched and a pull stays possible; pull keeps
    non-overlapping local edits (git's no-clobber guarantee) and refuses an update that would
    touch a locally modified file (`Git.LocalOverlap`); divergent histories refuse
    (`Git.Diverged`); the unborn-HEAD first sync refuses a dirty tree (`Git.DirtyWorkingTree`).
    Nothing is ever auto-merged or force-pushed.
  - *Atomic serve-state swap*: an applied pull reconciles the store through the shared
    change-feed reconciler (upsert-then-prune), so no live request window misses a stub.
- **Regression cases:** `GitSyncTests` — two-host push→pull round-trip (same stub id served),
  wholesale invalid-tree rejection, remote-ahead refusal (working copy untouched, pull still
  possible), non-overlapping local edits surviving a pull, overlapping-edit refusal,
  dirty/ahead/behind status, token/userinfo scrubbing, and the unconfigured default
  (`Git.NotConfigured`).

### Dashboard configuration (#151, amendment)

- `POST /__admin/git/configure {remoteUrl, branch?}` connects an unpinned host from Settings; the
  configuration persists in the working copy's own `.git/config` (restart-safe, no extra store).
  The working copy resolves host-side: `--root-dir`, else `<cwd>/mockifyr-data` (`--git-work-dir`
  overrides), and a flag-less host **adopts** an existing Git working copy at the default location
  on startup. Connecting a pure in-memory host **snapshots every tenant's stubs** into the working
  copy and activates file persistence (`SwitchableStubPersistence`) — nothing is lost, and the
  first push publishes the current state. Flag-pinned hosts refuse (`Git.FlagPinned`), as do
  DB-persistence hosts without a root-dir (`Git.PersistenceConflict`).
- **Repo detection never climbs.** Working-copy checks test for `<dir>/.git` directly — `rev-parse
  --git-dir` climbs to a parent repository, which for a nested default working copy would have
  adopted (and mutated the origin of!) the enclosing project repo. Caught by the host-level
  self-test.
- **Regression cases:** `GitSyncTests` (configure/snapshot/restart-resolution/refusals/invalid
  input) and `GitSyncConfigureHostTests` (flag-less host: HTTP configure → push → restart adoption
  serves the synced stub).
