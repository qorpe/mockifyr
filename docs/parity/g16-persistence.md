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
- **Deferred (explicitly tracked — not a silent gap):** multi-tenant persistence *reload* closed for this
  provider in [#467](https://github.com/qorpe/mockifyr/issues/467) (non-default tenants are written to
  per-tenant subdirectories and reloaded from them, see "A `--root-dir` host reloads the tenants it wrote"
  below; the database backends reload every tenant since #466); WireMock's `persistent:false` opt-out (all admin mutations persist when a root-dir is
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
  G16a. (LiteDB's own multi-tenant reload closed in #466: every row carries its tenant, and startup now
  loads them all.)
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

## Startup reload covers every tenant (#466)

- **The defect.** `ApplyStartupMappings` called `Load(TenantId.Default)` on every loader. G16g taught the
  *change-feed* reconciler to load every tenant through `IMultiTenantMappingsLoader`, and startup never
  learned it. So a host started on a Postgres or Redis database that already held stubs for non-default
  tenants served none of them: the rows were there, the tenants and their API keys came back (their startup
  loaders already read every row), and the tenants' imported API answered 404. As the report found, a plain
  `docker stop`/`start` hid it: the image's default `--root-dir /work` keeps file-system state in the
  container layer. Recreating the container (an image bump, `compose down`/`up`, a rescheduled pod) showed it.
- **The fix.** Startup makes the reconciler's split: a loader that spans tenants loads every tenant, each
  stub under its own; a single-tenant loader (a mappings directory) loads into the default tenant. Startup
  and a reload therefore cannot disagree about what a backend holds.
- **One more member of the class.** LiteDB stores `{ Id, Tenant, Json }`, so it spans tenants too, but its
  loader did not implement `IMultiTenantMappingsLoader`: the same defect on the third database backend, and
  a git-sync reconcile on a LiteDB host saw only the default tenant. `LiteDbMappingsLoader` now implements it.
- **What startup reload covers.** Postgres, Redis and LiteDB: every tenant. `--root-dir`: since #467, the
  default tenant's flat files and every tenant directory the file provider wrote (next section).
- **Validation.** For each backend, a host writes stubs for two non-default tenants and the default tenant
  through the admin API (`X-Mockifyr-Tenant`). It is replaced by a fresh host on the same backend, **without**
  `--change-feed`, so only startup can load them. Each tenant then serves its own stub and lists exactly one,
  and a tenant that never had stubs still answers 404. On `main` all three backends answered 404 for `acme`.
  This is coherence infrastructure, so there is no oracle.
- **The storage key (found verifying #466).** Postgres keyed a stub row by `id` alone (`id uuid PRIMARY KEY`,
  `ON CONFLICT (id) … SET tenant = @tenant`, `DELETE … WHERE id = @id`), and LiteDB keyed the document by the
  stub id and deleted by it, ignoring the tenant. The id is the client's choice, so two tenants may hold the
  same one (the same mapping file or export bundle imported into both). The second tenant's write moved the
  first's row to itself, and its delete removed the only row: both tenants served until a restart, and then
  the first had nothing. Once startup reloads every tenant this is a tenant-isolation leak in the
  persistence layer, reachable by an ordinary scoped write. Redis keeps one hash per tenant and was not
  affected; the resource and environment stores already keyed by tenant.
  - **Now** a stub is keyed by `(tenant, id)` on both: Postgres's primary key is `(tenant, id)`, and LiteDB's
    document key is `tenant|id` (the resource collection's pattern; a Guid has no `|`, so it is unambiguous).
    Save, edit and delete are scoped to the tenant.
  - **Existing data is migrated at startup, re-runnably.** Postgres drops a single-column primary key and adds
    `(tenant, id)` under an advisory lock, so two hosts starting together run it one after the other; LiteDB
    re-keys every document whose key is still a bare Guid. Every existing row already has a distinct id, so
    nothing collides, and a second start changes nothing.
  - **Rule for the class:** every tenant-scoped persisted record is keyed by its tenant together with its own
    id, on every backend. The global records (tenants, API keys) are keyed by an id that is unique host-wide
    by construction.
  - **Case-variant names on LiteDB (found verifying #468).** LiteDB 5 opens a file with the current culture and
    `IgnoreCase`, and that one collation governs the document key and every query comparison in the file. So the
    keys `acme|<id>` and `ACME|<id>` were one key, and `Tenant == "acme"` matched `ACME` too: one tenant's write,
    delete or reset took or removed a case-variant tenant's stubs after a restart. The same held for resources
    (tenant, collection and id), environments (tenant and key) and declared tenants. Postgres (`text =` under its
    deterministic default collation), Redis (binary keys) and the in-memory store are ordinal.
  - **Rule for the class (fail-closed):** no LiteDB-backed component reads or writes a file whose collation is
    not ordinal. Every LiteDB persistence and loader calls `LiteDbCollation.RequireOrdinal` before taking its
    collection and refuses a case-insensitive file. Only `LiteDbCollation.Open`, the host's entry point for
    `--litedb`, changes a file, because only it knows what the connection said about it. A new or empty file and
    `:memory:` are created ordinal through the connection string, so nothing is rebuilt; LiteDB reads that setting
    only at creation and refuses an existing file whose header names another collation. An existing
    case-insensitive file is rebuilt once (`Rebuild` with an ordinal collation, re-runnable, no document lost,
    since a case-insensitive file cannot hold two documents that differ only in case). A reflection sweep finds
    every type that takes a `LiteDatabase` and fails for one that accepts a case-insensitive file
    (`Every_component_built_on_a_LiteDB_file_refuses_a_case_insensitive_one_and_leaves_it_unchanged`), including
    components not written yet.
  - **A rebuild carries what the connection said about the file (found verifying #468, round 3).** In LiteDB 5
    `RebuildOptions.Password` is the password the *rebuilt* file gets, and `null` means none. A rebuild given only
    the collation therefore wrote an encrypted file back in plaintext, then failed to reopen it with the
    connection's password (`File is not encrypted`), so the host could not start. `Open` passes the connection's
    own password. A component never rebuilds, because it cannot know the password; that is why it refuses instead.
    `:memory:` could not be rebuilt at all, and is now created ordinal. Cases:
    `An_encrypted_file_is_rebuilt_ordinal_and_stays_encrypted_with_its_own_password`,
    `A_new_encrypted_file_is_created_ordinal_and_encrypted`, `A_memory_database_opens_ordinal`,
    `A_new_file_is_created_ordinal_whatever_collation_the_connection_string_names`, and through the real host
    `StartupReloadAllTenantsLiteDbTests.An_encrypted_file_with_the_default_collation_is_rebuilt_ordinal_on_startup_and_stays_encrypted`.
  - **Regression cases:** `Tenants_whose_names_differ_only_in_case_keep_their_own_stubs_through_a_write_a_reset_and_a_restart`
    on all three backends; `StartupReloadAllTenantsLiteDbTests.A_file_with_the_default_case_insensitive_collation_is_rebuilt_ordinal_on_startup_and_keeps_case_variant_tenants_apart`;
    `LiteDbOrdinalCollationTests` (stubs, resources, environments, declared tenants, and `istanbul`/`Istanbul`/`İSTANBUL`/`ıstanbul`
    under `tr-TR`). Disabling the rebuild turns the LiteDB facts red (8 of them) while Postgres and Redis stay green.
- **Regression cases:** `StartupReloadAllTenantsPostgresTests`, `StartupReloadAllTenantsRedisTests`,
  `StartupReloadAllTenantsLiteDbTests` (`A_new_host_serves_every_tenants_stubs_from_the_backend_without_the_change_feed`,
  `Two_tenants_holding_the_same_stub_id_both_serve_their_own_after_a_restart`,
  `A_tenant_that_edits_or_deletes_its_copy_of_a_shared_stub_id_leaves_the_other_tenants_copy_alone`), and the
  migrations: `StartupReloadAllTenantsPostgresTests.A_table_keyed_by_the_stub_id_alone_is_rekeyed_by_tenant_on_startup_and_its_rows_kept`,
  `StartupReloadAllTenantsLiteDbTests.A_document_keyed_by_the_stub_id_alone_is_rekeyed_by_tenant_on_startup_and_kept`.
  Planting the old key back turns the shared-id facts red on Postgres and LiteDB (`acme` answers 404); planting
  either migration out turns its fact red.

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

## A `--root-dir` host reloads the tenants it wrote (#467)

- **The defect.** `FileSystemStubPersistence` writes a non-default tenant's stubs to `mappings/<tenant>/<id>.json`,
  and `DirectoryMappingsLoader` read only the flat `mappings/*.json`, into the default tenant. So a tenant's stub
  created over the admin API on a `--root-dir` host was persisted, and a fresh host never served it.
- **The decision (owner, 2026-11-25, option (a)).** Reading `mappings/<name>/` as tenant `<name>` changes what a
  subdirectory of a user's mappings tree means, which until now was "ignored". So a subdirectory is a tenant only
  when **every** entry in it is a file this provider writes: `<guid>.json` (the canonical `D` form, lower- or
  upper-case hex, with a lower-case `.json`) holding **one** stub whose `id` is that guid. Anything else is skipped
  whole, logged once (`mockifyr: mappings/<name>: <reason> — skipped, not read as a tenant.`), and listed in
  `DirectoryMappingsLoader.SkippedDirectories`. That covers notes beside the stubs, a stub under any other name, a
  stub whose id is not its file name or that carries none, a bundle, a file that does not parse, a nested directory,
  an OS file such as `.DS_Store`, and a directory named like the default tenant. It is an allow-list of what the
  provider writes, so a hand-made layout nobody has named yet is skipped too. A directory is all or nothing: one
  foreign file and none of its stubs load. No ADR, because no layout changed: the provider has written this layout
  since G16a, and only the reading is new.
- **A tenant is one path segment, never a path (verifier, PR #483).** The tenant came straight from the tenant header
  into `Path.Combine`, so `a/b` was written nested (and lost on restart, the loader refusing a directory that holds one),
  `../escape` was written outside `mappings/`, and a rooted name such as `/tmp/x` replaced the base altogether. The
  environments provider joined the tenant the same way, and its `Clear` deletes recursively, so `..` would have deleted
  the whole root directory; the tenant declarations joined it into a file name. All three now go through
  `TenantPathSegment`: printable ASCII that is a portable file-name character stays as it is (so every directory an
  earlier host wrote for a plain name is the same directory), and every other character — `/`, `\`, `%`, the
  characters Windows refuses, control characters, anything non-ASCII — is written as its UTF-8 bytes in upper-case
  `%XX`. A name of dots only, and a trailing dot or space, are escaped too; an empty or ill-formed name has no segment.
  On the way back a directory is a tenant only when the encoder would have written exactly its name, so `a%2fb`,
  `%41` or an unescaped `a:b` is skipped (listed, for stubs), never read as a tenant it resembles.
  - **The resources provider was the last member (verifier, PR #483, second round).** It escaped the tenant directory
    with its own encoder (`ResourceJson.SafeName`, which escapes everything outside `[A-Za-z0-9._-]`) and its loader
    read the escaped name back as the tenant, so `Acme Corp` was written as `Acme%20Corp/` and reloaded as tenant
    `Acme%20Corp`: the documents answered 404 after a restart. The tenant directory now goes through `TenantPathSegment`
    too; `SafeName` stays for the collection and document id, which the loader takes from the stored document. A
    source sweep (`No_path_in_the_source_is_built_from_a_tenant_but_through_the_one_encoder`) fails on any
    `Path.Combine`/`Path.Join` in `src/` that takes a tenant other than through the encoder, so a fifth provider with an
    encoder of its own is caught by the build rather than by the next review.
  - **Upgrade: a directory an earlier release wrote.** Before #467 the stub and environment providers named a tenant's
    directory by its raw value, and the resources provider by `SafeName`. On load, a directory this release would have
    written is read as before. One it would not have written is read as the tenant the EARLIER writer meant, when that
    writer could have produced exactly that name, and is then **renamed once** to this release's name, with a line
    saying so. The rename is what makes a delete or reset after the upgrade hold: read in place, the next write would
    land in the new directory and a deleted document would come back from the old one on the next restart. A stub
    directory is renamed only after every file in it has been proven the provider's, so a hand-made directory is still
    never touched.
    - On a git-synced root the rename is a change in the working tree like any other, and the next push carries it,
      so a remote written by an earlier release converges on this release's names.
    - The raw-name reading accepts one segment only (not empty, `.`, `..`, a separator or a rooted name).
    - The `SafeName` reading accepts a name only when it reads as printable ASCII and writing that value with the old
      escape gives the same name back. The old escape is not reversible above `0xFF` (`İ`, U+0130, was `%130`, which
      also reads as U+0013 and `0`), so such a directory is skipped, not guessed, and the line names it.
    - When the tenant already has a directory under this release's name, the earlier one is skipped, not merged, and
      the line says to move its contents. Merging silently could bring back what one of the two had deleted.
    - **Residual ambiguity, stated:** a name that both writers could have produced for two different tenants is read
      by this release's rule. That needs a tenant whose raw name contains an upper-case `%XX` escape (an earlier
      environment directory `a%2Ab` is now tenant `a*b`, not `a%2Ab`), or a resources tenant made of Latin-1 code units
      that happen to be another name's UTF-8 bytes. The tenant header is ASCII-only, so the second needs a credential
      scope or OIDC claim.
  - **Not covered: letter case.** Two tenants that differ only in case (`Acme`, `acme`) share one directory on a
    case-insensitive file system (macOS, Windows). Escaping upper-case letters would move every existing directory, so
    it is stated rather than changed; on Linux, the image's platform, they are two directories.
  - `TenantPathSegmentTests` holds 22 hostile names (`a/b`, `a\b`, `.`, `..`, `../x`, `/tmp/x`, `C:\x`, a space,
    `İstanbul`, `%`, a trailing dot or space, a NUL, an emoji, …) round-tripping through the encoder, the stub provider
    and its loader, the environments provider (whose `Clear("..")` deletes only its own directory) and the tenant
    declarations, plus ten directory names the decoder refuses. `RootDirTenantRestartTests` drives a real host through a
    restart for seven of them. With the providers as they were, seven of these facts are red. Stryker scores the encoder
    100 % (its first run, 83 %, found the dots-only rule and the strict decoder unreachable, so they were removed: the
    one canonical re-encoding check refuses everything they did).
- **Validation of the resources member and the upgrade.** `RootDirTenantRestartTests` drives a real host: a resource
  under seven tenant names survives a restart, and seven directories in an earlier release's layout (resources under
  the old escape, environments and stubs under the raw name) are read as their tenant, renamed, and a delete after the
  upgrade holds across the next restart. With the loaders as they were, 14 of these facts and the not-merged fact are
  red; the `plain` rows stay green. `TenantPathSegmentTests` holds the two earlier-name readings' accepted and refused
  names.
- **Where it applies.** `DirectoryMappingsLoader` implements `IMultiTenantMappingsLoader`, so startup
  (`ApplyStartupMappings`) and git sync's reload after a pull both read the tenants. The second is also a fix: a
  pull reconciled only the default tenant from disk, so a non-default tenant held in memory was pruned on every
  pull. `Load(tenant)`, the single-tenant seam, is unchanged.
- **Validation.** No oracle: tenancy is Mockifyr's own.
  - `DirectoryMappingsTenantsTests` covers the provider's own tenants reloaded with their ids, the ten skipped
    shapes above (each beside a valid file that must not load either), a skip listed once across reloads, and an
    empty or missing directory.
  - `G16aPersistenceTests.A_tenants_stub_survives_a_restart_and_a_handmade_directory_does_not_become_a_tenant` runs
    a real host. A tenant's stub is created over the admin API, and a fresh host serves it to that tenant only,
    while a hand-made directory beside it is no tenant.
  - `GitSyncTests.PushThenPull_CarriesANonDefaultTenantsStub_AndThePullKeepsIt` covers git sync.
  - With the interface removed (`main`'s behaviour), the wire fact and the git fact are red. With the allow-list
    loosened, five of the skipped shapes load as tenants.

