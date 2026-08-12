using Mediant.Abstractions;
using Mediant.Results;
using Mockifyr.Core;

namespace Mockifyr.Application;

// The management-path CQRS contracts (Mediant). Every operation is tenant-scoped — there is no
// tenant-less overload, mirroring the store contracts. The mock-serving hot path never comes here.

/// <summary>Creates a single stub from the imported stub-mapping JSON; returns its id.</summary>
public sealed record CreateStubCommand(string MappingJson, TenantId Tenant) : ICommand<Result<Guid>>;

/// <summary>Replaces the stub at <paramref name="Id"/> with the given stub-mapping JSON (the <c>PUT /__admin/mappings/{id}</c> admin endpoint).</summary>
public sealed record UpdateStubCommand(Guid Id, string MappingJson, TenantId Tenant) : ICommand<Result>;

/// <summary>Deletes a stub by id.</summary>
public sealed record DeleteStubCommand(Guid Id, TenantId Tenant) : ICommand<Result>;

/// <summary>Imports one or more mappings (a single stub or a <c>{"mappings":[…]}</c> bundle); returns the count.</summary>
public sealed record ImportMappingsCommand(string MappingJson, TenantId Tenant) : ICommand<Result<int>>;

/// <summary>Removes all stubs for the tenant (the <c>/__admin/mappings/reset</c> admin endpoint).</summary>
public sealed record ResetMappingsCommand(TenantId Tenant) : ICommand<Result>;

/// <summary>
/// Checks the tenant's stubs against an OpenAPI specification (<c>POST /__admin/openapi/verify</c>,
/// #287). Reports; never mutates.
/// </summary>
public sealed record VerifyContractQuery(string SpecText, TenantId Tenant)
    : IQuery<Result<Mockifyr.Adapters.OpenApi.ConformanceReport>>;

/// <summary>
/// Checks what clients actually sent against an OpenAPI specification (<c>POST
/// /__admin/requests/verify</c>, #287). Reads the journal; never changes it.
/// </summary>
public sealed record VerifyTrafficQuery(string SpecText, TenantId Tenant)
    : IQuery<Result<Mockifyr.Adapters.OpenApi.TrafficReport>>;

/// <summary>Reads the tenant's degradation profile (<c>GET /__admin/degradation</c>).</summary>
public sealed record GetDegradationQuery(TenantId Tenant) : IQuery<Result<DegradationProfile>>;

/// <summary>Sets the tenant's degradation profile (<c>PUT /__admin/degradation</c>).</summary>
public sealed record SetDegradationCommand(DegradationProfile Profile, TenantId Tenant) : ICommand<Result>;

/// <summary>Returns the tenant to full health (<c>DELETE /__admin/degradation</c>).</summary>
public sealed record ClearDegradationCommand(TenantId Tenant) : ICommand<Result>;

/// <summary>Reads the tenant's clock override (<c>GET /__admin/clock</c>).</summary>
public sealed record GetClockQuery(TenantId Tenant) : IQuery<Result<ClockOverride>>;

/// <summary>Sets the tenant's clock override (<c>PUT /__admin/clock</c>).</summary>
public sealed record SetClockCommand(ClockOverride Clock, TenantId Tenant) : ICommand<Result>;

/// <summary>Returns the tenant to real time (<c>DELETE /__admin/clock</c>).</summary>
public sealed record ClearClockCommand(TenantId Tenant) : ICommand<Result>;

/// <summary>
/// Discards the tenant's journaled requests (the <c>DELETE /__admin/requests</c> admin endpoint).
/// </summary>
public sealed record ResetRequestsCommand(TenantId Tenant) : ICommand<Result>;

/// <summary>Lists all stubs for the tenant.</summary>
public sealed record GetStubsQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<StubMapping>>>;

/// <summary>Gets a single stub by id (<see cref="Error.NotFound"/> when absent).</summary>
public sealed record GetStubQuery(Guid Id, TenantId Tenant) : IQuery<Result<StubMapping>>;

/// <summary>Counts journaled requests matching a request-pattern JSON (verification).</summary>
public sealed record CountRequestsQuery(string PatternJson, TenantId Tenant) : IQuery<Result<int>>;

/// <summary>Lists the journaled requests that matched no stub.</summary>
public sealed record FindUnmatchedRequestsQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<CanonicalRequest>>>;

/// <summary>
/// The stubs closest to a request, with per-attribute verdicts (#288). Backs both admin routes: the
/// one that asks about a journaled request and the one that asks about a hypothetical one.
/// </summary>
public sealed record FindNearMissesQuery(CanonicalRequest Request, TenantId Tenant)
    : IQuery<Result<IReadOnlyList<NearMiss>>>;

/// <summary>Lists the journaled serve events for a tenant (the request log).</summary>
public sealed record GetServeEventsQuery(TenantId Tenant, bool UnmatchedOnly = false, int? Limit = null)
    : IQuery<Result<IReadOnlyList<ServeEvent>>>;

/// <summary>Resolves one journaled serve event by id (indexed — no scan), tenant-gated.</summary>
public sealed record GetServeEventQuery(Guid Id, TenantId Tenant)
    : IQuery<Result<ServeEvent?>>;

/// <summary>A scenario's current state and the states it can be in (G12c admin).</summary>
public sealed record ScenarioView(string Name, string State, IReadOnlyList<string> PossibleStates);

/// <summary>Lists the tenant's scenarios with their current state.</summary>
public sealed record GetScenariosQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<ScenarioView>>>;

/// <summary>Sets a scenario's state directly (the <c>PUT /__admin/scenarios/{name}/state</c> admin endpoint).</summary>
public sealed record SetScenarioStateCommand(string Name, string State, TenantId Tenant) : ICommand<Result>;

/// <summary>Resets every scenario to <c>Started</c>.</summary>
public sealed record ResetScenariosCommand(TenantId Tenant) : ICommand<Result>;

// Environment keys (G17, issues #165/#166). Every operation carries the tenant: environments are
// tenant-owned, and cross-tenant access must be impossible at the API level, not merely hidden in
// the dashboard.

/// <summary>Lists the tenant's environment keys with their values and which one is active.</summary>
public sealed record GetEnvironmentsQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<EnvironmentKey>>>;

/// <summary>
/// Creates or replaces an environment key (<c>PUT /__admin/environments/{key}</c>). Rejects a key that
/// is malformed or collides with a built-in templating helper.
/// </summary>
public sealed record PutEnvironmentKeyCommand(EnvironmentKey Key, TenantId Tenant) : ICommand<Result>;

/// <summary>Selects which value is active for a key (<c>PUT /__admin/environments/{key}/active</c>).</summary>
public sealed record SetEnvironmentActiveValueCommand(string Key, string ActiveValue, TenantId Tenant) : ICommand<Result>;

/// <summary>Deletes an environment key (<c>DELETE /__admin/environments/{key}</c>).</summary>
public sealed record DeleteEnvironmentKeyCommand(string Key, TenantId Tenant) : ICommand<Result>;

/// <summary>Deletes every environment key owned by the tenant.</summary>
public sealed record ResetEnvironmentsCommand(TenantId Tenant) : ICommand<Result>;

// Sandbox resources (G19a, ADR 0011): tenant- and collection-scoped JSON documents behind
// /__admin/resources. Bodies are opaque JSON text — validated well-formed and size-capped at the
// management edge, never parsed by Core.

/// <summary>One page of a collection listing plus the collection's total count.</summary>
public sealed record ResourcePage(IReadOnlyList<ResourceDocument> Documents, int Total);

/// <summary>A seed item: an optional explicit id (absent ids are generated) and the document body.</summary>
public sealed record SeedResourceItem(string? Id, string Body);

/// <summary>Lists the tenant's collections with document counts.</summary>
public sealed record GetResourceCollectionsQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<ResourceCollectionInfo>>>;

/// <summary>
/// Lists one collection, paginated (limit clamped to 1..500, default 100) and optionally filtered,
/// sorted and projected (#353). <paramref name="Query"/> defaults to selecting everything, so a caller
/// that passes none behaves exactly as before.
/// </summary>
public sealed record ListResourcesQuery(
    string Collection,
    int? Limit,
    int? Offset,
    TenantId Tenant,
    ResourceQuery? Query = null) : IQuery<Result<ResourcePage>>;

/// <summary>Reads one document, or a not-found error.</summary>
public sealed record GetResourceQuery(string Collection, string Id, TenantId Tenant) : IQuery<Result<ResourceDocument>>;

/// <summary>Creates or replaces one document (last-write-wins; ADR 0011 addendum).</summary>
public sealed record PutResourceCommand(string Collection, string Id, string Body, TenantId Tenant) : ICommand<Result<ResourceDocument>>;

/// <summary>Deletes one document, or a not-found error.</summary>
public sealed record DeleteResourceCommand(string Collection, string Id, TenantId Tenant) : ICommand<Result>;

/// <summary>Clears one collection, or — with a null collection — every collection of the tenant.</summary>
public sealed record ResetResourcesCommand(string? Collection, TenantId Tenant) : ICommand<Result>;

/// <summary>Seeds a collection from a JSON array; transactional — on any invalid item nothing lands.</summary>
public sealed record SeedResourcesCommand(string Collection, IReadOnlyList<SeedResourceItem> Items, TenantId Tenant) : ICommand<Result<int>>;

// Named datasets (#351): a scenario across collections, loaded and reset as one thing.

/// <summary>Lists the tenant's datasets, name-ordered.</summary>
public sealed record GetDatasetsQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<DatasetDefinition>>>;

/// <summary>Declares or replaces a dataset. Nothing is loaded by declaring one.</summary>
public sealed record PutDatasetCommand(DatasetDefinition Dataset, TenantId Tenant) : ICommand<Result>;

/// <summary>Removes a dataset definition. Documents an earlier load created are left alone.</summary>
public sealed record DeleteDatasetCommand(string Name, TenantId Tenant) : ICommand<Result>;

/// <summary>
/// Loads a dataset, atomically. An earlier load of the same dataset is unloaded first, so loading
/// twice leaves one copy rather than two — the gesture people actually repeat between test runs.
/// </summary>
public sealed record LoadDatasetCommand(string Name, TenantId Tenant) : ICommand<Result<int>>;

/// <summary>Removes exactly what the last load of this dataset created.</summary>
public sealed record UnloadDatasetCommand(string Name, TenantId Tenant) : ICommand<Result<int>>;

/// <summary>Lists the tenant's declared relations (ADR 0015), collection-name ordered.</summary>
public sealed record GetRelationsQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<ResourceSchema>>>;

/// <summary>
/// Declares or replaces one collection's relations (ADR 0015). Replaces rather than merges: a
/// declaration a reader cannot see in full is a declaration they will get wrong.
/// </summary>
public sealed record PutRelationCommand(
    string Collection,
    IReadOnlyList<ResourceRelation> BelongsTo,
    TenantId Tenant) : ICommand<Result<ResourceSchema>>;

/// <summary>Removes one collection's relations; the documents themselves are untouched.</summary>
public sealed record DeleteRelationCommand(string Collection, TenantId Tenant) : ICommand<Result>;

/// <summary>
/// Imports an OpenAPI 3.x document (JSON or YAML) as ordinary mappings; with <c>Stateful</c>,
/// resource-shaped path pairs emit a G19b state-wired CRUD set. Transactional: on any refusal
/// nothing is created (ADR 0011 addendum). Returns how many stubs were imported.
/// </summary>
public sealed record ImportOpenApiCommand(string SpecText, bool Stateful, TenantId Tenant) : ICommand<Result<int>>;

// Sandbox access (G19d, ADR 0011): operator-issued API keys that scope traffic to a tenant.

/// <summary>The one-time issue result: the token appears here and never again.</summary>
public sealed record IssuedApiKey(ApiKey Key, string Token);

/// <summary>A key with its current-window usage, for the admin listing.</summary>
public sealed record ApiKeyWithUsage(ApiKey Key, int Used);

/// <summary>Issues a key for the tenant (optionally quota-limited per hour, expiring, or read-only).</summary>
public sealed record IssueApiKeyCommand(
    string Name,
    int? QuotaPerHour,
    TenantId Tenant,
    DateTimeOffset? ExpiresAt = null,
    ApiKeyScope Scope = ApiKeyScope.ReadWrite) : ICommand<Result<IssuedApiKey>>;

/// <summary>Lists the tenant's keys (hashes never leave the handler — prefixes only).</summary>
public sealed record GetApiKeysQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<ApiKeyWithUsage>>>;

/// <summary>
/// Revokes one of the tenant's keys, recording who decided and why (#355). The key stays listed,
/// marked revoked — deleting it would erase the only record that the decision was ever made.
/// </summary>
public sealed record RevokeApiKeyCommand(
    string Id, TenantId Tenant, string By = "unknown", string? Reason = null) : ICommand<Result>;

/// <summary>
/// Issues a successor to a key and puts the predecessor on a clock (#355).
/// </summary>
/// <remarks>
/// Rotation without overlap is an outage: the old credential stops the instant the new one starts, so
/// a partner cannot deploy the new one first — and a rotation that causes an outage does not happen.
/// The predecessor expires after <paramref name="OverlapMinutes"/> instead, so the sequence is issue,
/// deploy, and let it lapse. An overlap of zero revokes it immediately, for the case rotation is a
/// response to a leak.
/// </remarks>
public sealed record RotateApiKeyCommand(
    string Id, TenantId Tenant, int OverlapMinutes, string By = "unknown")
    : ICommand<Result<IssuedApiKey>>;

// Admin audit trail (#247): read-only from the management API — entries are appended by the host's
// audit middleware, never by an API caller, so a trail cannot be edited through the surface it audits.

/// <summary>Lists the tenant's audit entries, newest first (limit clamped to 1..1000, default 200).</summary>
public sealed record GetAuditEntriesQuery(TenantId Tenant, int? Limit = null)
    : IQuery<Result<IReadOnlyList<AuditEntry>>>;

// Backup and restore (#252): one archive of everything a tenant's operator authored, and a restore
// path a runbook can follow. Tenant-scoped like every other operation here — a host-wide archive
// would have to cross the tenant boundary the rest of this API is built to hold.

/// <summary>What a restore actually changed, so the caller is told rather than left to assume.</summary>
public sealed record RestoreSummary(int Mappings, int Environments, int Resources, int ApiKeys, int Scenarios);

/// <summary>Gathers the tenant's stubs, environments, sandbox documents, API keys and scenario states.</summary>
public sealed record CreateBackupQuery(TenantId Tenant) : IQuery<Result<BackupArchive>>;

/// <summary>
/// Restores an archive into <paramref name="Tenant"/>, replacing that tenant's stubs, environments,
/// sandbox documents and API keys. A section absent from the archive is left untouched.
/// </summary>
public sealed record RestoreBackupCommand(string ArchiveJson, TenantId Tenant) : ICommand<Result<RestoreSummary>>;
