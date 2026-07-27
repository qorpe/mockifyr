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

/// <summary>Lists all stubs for the tenant.</summary>
public sealed record GetStubsQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<StubMapping>>>;

/// <summary>Gets a single stub by id (<see cref="Error.NotFound"/> when absent).</summary>
public sealed record GetStubQuery(Guid Id, TenantId Tenant) : IQuery<Result<StubMapping>>;

/// <summary>Counts journaled requests matching a request-pattern JSON (verification).</summary>
public sealed record CountRequestsQuery(string PatternJson, TenantId Tenant) : IQuery<Result<int>>;

/// <summary>Lists the journaled requests that matched no stub.</summary>
public sealed record FindUnmatchedRequestsQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<CanonicalRequest>>>;

/// <summary>Lists the journaled serve events for a tenant (the request log).</summary>
public sealed record GetServeEventsQuery(TenantId Tenant, bool UnmatchedOnly = false, int? Limit = null)
    : IQuery<Result<IReadOnlyList<ServeEvent>>>;

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

/// <summary>Lists one collection, paginated (limit clamped to 1..500, default 100).</summary>
public sealed record ListResourcesQuery(string Collection, int? Limit, int? Offset, TenantId Tenant) : IQuery<Result<ResourcePage>>;

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

/// <summary>Issues a key for the tenant (optionally quota-limited per hour).</summary>
public sealed record IssueApiKeyCommand(string Name, int? QuotaPerHour, TenantId Tenant) : ICommand<Result<IssuedApiKey>>;

/// <summary>Lists the tenant's keys (hashes never leave the handler — prefixes only).</summary>
public sealed record GetApiKeysQuery(TenantId Tenant) : IQuery<Result<IReadOnlyList<ApiKeyWithUsage>>>;

/// <summary>Revokes one of the tenant's keys.</summary>
public sealed record RevokeApiKeyCommand(string Id, TenantId Tenant) : ICommand<Result>;
