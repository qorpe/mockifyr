using System.Linq;
using System.Text.Json;
using Mediant.Abstractions;
using Mediant.Results;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;

namespace Mockifyr.Application;

// Handlers for the management-path operations. They depend only on Core contracts (the stub store)
// and the engine's read-only verification queries; Mediant registers them by assembly scan.

/// <summary>Creates a stub and returns its id, or a validation error if the JSON yields none.</summary>
public sealed class CreateStubHandler(IStubStore store, IMatcherRegistry matchers, IStubPersistence persistence)
    : ICommandHandler<CreateStubCommand, Result<Guid>>
{
    public ValueTask<Result<Guid>> Handle(CreateStubCommand command, CancellationToken cancellationToken)
    {
        IReadOnlyList<(StubMapping Stub, string Source)> stubs;
        try
        {
            stubs = MappingJsonReader.ReadWithSource(command.MappingJson, command.Tenant, matchers);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // JsonException = malformed JSON; InvalidOperationException = a well-formed but wrong-typed
            // field (e.g. a string where a numeric status is expected). Both are client input errors.
            return ValueTask.FromResult<Result<Guid>>(Error.Validation("Stub.Invalid", "The stub JSON is malformed."));
        }

        if (stubs.Count == 0)
        {
            return ValueTask.FromResult<Result<Guid>>(Error.Validation("Stub.Invalid", "No stub could be read from the JSON."));
        }

        store.Put(stubs[0].Stub);
        persistence.Save(stubs[0].Stub, stubs[0].Source);
        return ValueTask.FromResult<Result<Guid>>(stubs[0].Stub.Id);
    }
}

/// <summary>
/// Replaces an existing stub via the admin route <c>PUT /__admin/mappings/{id}</c>. The route id is
/// authoritative: the parsed stub's id is forced to it so <see cref="IStubStore.Put"/> upserts in place
/// rather than appending a duplicate. Returns a validation error for malformed/empty JSON, matching create.
/// </summary>
public sealed class UpdateStubHandler(IStubStore store, IMatcherRegistry matchers, IStubPersistence persistence)
    : ICommandHandler<UpdateStubCommand, Result>
{
    public ValueTask<Result> Handle(UpdateStubCommand command, CancellationToken cancellationToken)
    {
        IReadOnlyList<(StubMapping Stub, string Source)> stubs;
        try
        {
            stubs = MappingJsonReader.ReadWithSource(command.MappingJson, command.Tenant, matchers);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ValueTask.FromResult(Result.Failure(Error.Validation("Stub.Invalid", "The stub JSON is malformed.")));
        }

        if (stubs.Count == 0)
        {
            return ValueTask.FromResult(Result.Failure(Error.Validation("Stub.Invalid", "No stub could be read from the JSON.")));
        }

        var updated = stubs[0].Stub with { Id = command.Id };
        store.Put(updated);
        persistence.Save(updated, stubs[0].Source);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Deletes a stub by id; idempotent — deleting a missing stub still succeeds (verified by the differential suite).</summary>
public sealed class DeleteStubHandler(IStubStore store, IStubPersistence persistence)
    : ICommandHandler<DeleteStubCommand, Result>
{
    public ValueTask<Result> Handle(DeleteStubCommand command, CancellationToken cancellationToken)
    {
        store.Remove(command.Tenant, command.Id);
        persistence.Remove(command.Tenant, command.Id);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>
/// Imports one stub or a bundle of them, returning how many were loaded. A bundle wrapper may carry
/// an <c>environments</c> section (issue #198) — those keys are restored first, so the imported
/// stubs' <c>{{key}}</c> references resolve from the same bundle. Each imported key goes through the
/// same validation as the admin PUT and <b>replaces</b> an existing key of the same name (an import
/// restores the exported state); an entry that fails validation is skipped without failing the
/// import — the mappings, which never depend on it having been stored, still load.
/// </summary>
public sealed class ImportMappingsHandler(
    IStubStore store, IMatcherRegistry matchers, IStubPersistence persistence,
    IEnvironmentStore environments, IEnvironmentPersistence environmentPersistence)
    : ICommandHandler<ImportMappingsCommand, Result<int>>
{
    public ValueTask<Result<int>> Handle(ImportMappingsCommand command, CancellationToken cancellationToken)
    {
        IReadOnlyList<(StubMapping Stub, string Source)> stubs;
        IReadOnlyList<EnvironmentKey> importedKeys;
        try
        {
            stubs = MappingJsonReader.ReadWithSource(command.MappingJson, command.Tenant, matchers);
            importedKeys = EnvironmentJsonReader.Read(command.MappingJson);
        }
        catch (JsonException)
        {
            return ValueTask.FromResult<Result<int>>(Error.Validation("Mappings.Invalid", "The mappings JSON is malformed."));
        }

        foreach (var key in importedKeys)
        {
            if (EnvironmentKeyRules.Validate(key) is not null)
            {
                continue;
            }

            environments.Put(command.Tenant, key);
            environmentPersistence.Save(command.Tenant, key);
        }

        foreach (var (stub, source) in stubs)
        {
            store.Put(stub);
            persistence.Save(stub, source);
        }

        return ValueTask.FromResult<Result<int>>(stubs.Count);
    }
}

/// <summary>Removes every stub for the tenant.</summary>
public sealed class ResetMappingsHandler(IStubStore store, IStubPersistence persistence)
    : ICommandHandler<ResetMappingsCommand, Result>
{
    public ValueTask<Result> Handle(ResetMappingsCommand command, CancellationToken cancellationToken)
    {
        foreach (var stub in store.GetStubs(command.Tenant).ToList())
        {
            store.Remove(command.Tenant, stub.Id);
        }

        persistence.Clear(command.Tenant);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Lists all stubs for the tenant.</summary>
public sealed class GetStubsHandler(IStubStore store) : IQueryHandler<GetStubsQuery, Result<IReadOnlyList<StubMapping>>>
{
    public ValueTask<Result<IReadOnlyList<StubMapping>>> Handle(GetStubsQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(store.GetStubs(query.Tenant)));
}

/// <summary>Gets a single stub by id, or a not-found error.</summary>
public sealed class GetStubHandler(IStubStore store) : IQueryHandler<GetStubQuery, Result<StubMapping>>
{
    public ValueTask<Result<StubMapping>> Handle(GetStubQuery query, CancellationToken cancellationToken)
    {
        var stub = store.GetStubs(query.Tenant).FirstOrDefault(s => s.Id == query.Id);
        return stub is null
            ? ValueTask.FromResult<Result<StubMapping>>(Error.NotFound("Stub.NotFound", $"No stub with id {query.Id}."))
            : ValueTask.FromResult<Result<StubMapping>>(stub);
    }
}

/// <summary>
/// Runs a conformance check of the tenant's stubs against a specification (#287).
/// </summary>
/// <remarks>
/// The stubs are handed over as the JSON they were written in, not as compiled matchers: the question
/// is whether what the stub <em>says</em> still agrees with the document, and that is what a human
/// compares too.
/// </remarks>
public sealed class VerifyContractHandler(IStubStore store)
    : IQueryHandler<VerifyContractQuery, Result<Mockifyr.Adapters.OpenApi.ConformanceReport>>
{
    public ValueTask<Result<Mockifyr.Adapters.OpenApi.ConformanceReport>> Handle(
        VerifyContractQuery query, CancellationToken cancellationToken)
    {
        var stubs = store.GetStubs(query.Tenant)
            .Where(stub => stub.Source is not null)
            .Select(stub => new Mockifyr.Adapters.OpenApi.StubUnderTest(stub.Id, stub.Source!))
            .ToList();

        try
        {
            return ValueTask.FromResult<Result<Mockifyr.Adapters.OpenApi.ConformanceReport>>(
                Mockifyr.Adapters.OpenApi.ContractConformance.Verify(query.SpecText, stubs));
        }
        catch (Mockifyr.Adapters.OpenApi.OpenApiImportException ex)
        {
            // Typed refusals live here rather than at the HTTP edge, the same way the import handler
            // does it: a document that cannot be parsed fails identically whichever facade asked.
            return ValueTask.FromResult<Result<Mockifyr.Adapters.OpenApi.ConformanceReport>>(
                Error.Validation($"OpenApi.{ex.Error}", ex.Message));
        }
    }
}

/// <summary>
/// Checks the tenant's journaled traffic against a specification (#287) — the consumer side of
/// conformance.
/// </summary>
public sealed class VerifyTrafficHandler(StubEngine engine)
    : IQueryHandler<VerifyTrafficQuery, Result<Mockifyr.Adapters.OpenApi.TrafficReport>>
{
    public ValueTask<Result<Mockifyr.Adapters.OpenApi.TrafficReport>> Handle(
        VerifyTrafficQuery query, CancellationToken cancellationToken)
    {
        var requests = engine.GetServeEvents(query.Tenant, new ServeEventQuery())
            .Select(e => new Mockifyr.Adapters.OpenApi.RecordedRequest(
                e.Request.Method,
                e.Request.Url,
                e.Request.Body.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(e.Request.Body),
                [.. e.Request.Headers.Select(h => h.Key)]))
            .ToList();

        try
        {
            return ValueTask.FromResult<Result<Mockifyr.Adapters.OpenApi.TrafficReport>>(
                Mockifyr.Adapters.OpenApi.TrafficConformance.Verify(query.SpecText, requests));
        }
        catch (Mockifyr.Adapters.OpenApi.OpenApiImportException ex)
        {
            return ValueTask.FromResult<Result<Mockifyr.Adapters.OpenApi.TrafficReport>>(
                Error.Validation($"OpenApi.{ex.Error}", ex.Message));
        }
    }
}

/// <summary>Reads the tenant's degradation profile (#289).</summary>
public sealed class GetDegradationHandler(IDegradationStore store)
    : IQueryHandler<GetDegradationQuery, Result<DegradationProfile>>
{
    public ValueTask<Result<DegradationProfile>> Handle(
        GetDegradationQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(store.Get(query.Tenant)));
}

/// <summary>
/// Degrades the tenant (#289). Setting a healthy profile is the same as clearing, so a client that
/// always PUTs its whole configuration does not leave tenants marked as degraded while behaving
/// normally.
/// </summary>
public sealed class SetDegradationHandler(IDegradationStore store) : ICommandHandler<SetDegradationCommand, Result>
{
    public ValueTask<Result> Handle(SetDegradationCommand command, CancellationToken cancellationToken)
    {
        store.Set(command.Tenant, command.Profile);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Returns the tenant to full health (#289).</summary>
public sealed class ClearDegradationHandler(IDegradationStore store)
    : ICommandHandler<ClearDegradationCommand, Result>
{
    public ValueTask<Result> Handle(ClearDegradationCommand command, CancellationToken cancellationToken)
    {
        store.Clear(command.Tenant);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Reads the tenant's clock override (#290).</summary>
public sealed class GetClockHandler(IClockStore store) : IQueryHandler<GetClockQuery, Result<ClockOverride>>
{
    public ValueTask<Result<ClockOverride>> Handle(GetClockQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(store.Get(query.Tenant)));
}

/// <summary>
/// Sets the tenant's clock override (#290). Setting real time is the same as clearing, so a client
/// that always PUTs its configuration does not accumulate no-op overrides.
/// </summary>
public sealed class SetClockHandler(IClockStore store) : ICommandHandler<SetClockCommand, Result>
{
    public ValueTask<Result> Handle(SetClockCommand command, CancellationToken cancellationToken)
    {
        store.Set(command.Tenant, command.Clock);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Returns the tenant to real time (#290).</summary>
public sealed class ClearClockHandler(IClockStore store) : ICommandHandler<ClearClockCommand, Result>
{
    public ValueTask<Result> Handle(ClearClockCommand command, CancellationToken cancellationToken)
    {
        store.Clear(command.Tenant);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>
/// Clears the tenant's request journal. A suite sharing one host calls this between tests so a count
/// asserts about the test that is running — the reference engine answers <c>DELETE /__admin/requests</c>
/// the same way.
/// </summary>
public sealed class ResetRequestsHandler(IRequestJournal journal) : ICommandHandler<ResetRequestsCommand, Result>
{
    public ValueTask<Result> Handle(ResetRequestsCommand command, CancellationToken cancellationToken)
    {
        journal.Clear(command.Tenant);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Counts journaled requests matching the given request pattern.</summary>
public sealed class CountRequestsHandler(StubEngine engine) : IQueryHandler<CountRequestsQuery, Result<int>>
{
    public ValueTask<Result<int>> Handle(CountRequestsQuery query, CancellationToken cancellationToken)
    {
        var pattern = MappingJsonReader.ReadRequestPattern(query.PatternJson);
        return ValueTask.FromResult<Result<int>>(engine.CountRequestsMatching(query.Tenant, pattern));
    }
}

/// <summary>
/// Ranks the stubs closest to a request and explains, attribute by attribute, where each one parted
/// company with it (#288).
/// </summary>
public sealed class FindNearMissesHandler(StubEngine engine)
    : IQueryHandler<FindNearMissesQuery, Result<IReadOnlyList<NearMiss>>>
{
    public ValueTask<Result<IReadOnlyList<NearMiss>>> Handle(
        FindNearMissesQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(engine.FindNearMisses(query.Tenant, query.Request, detailed: true)));
}

/// <summary>Lists the journaled requests that matched no stub.</summary>
public sealed class FindUnmatchedRequestsHandler(StubEngine engine)
    : IQueryHandler<FindUnmatchedRequestsQuery, Result<IReadOnlyList<CanonicalRequest>>>
{
    public ValueTask<Result<IReadOnlyList<CanonicalRequest>>> Handle(
        FindUnmatchedRequestsQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(engine.FindUnmatchedRequests(query.Tenant)));
}

/// <summary>Lists the journaled serve events for a tenant (the request log).</summary>
public sealed class GetServeEventsHandler(StubEngine engine)
    : IQueryHandler<GetServeEventsQuery, Result<IReadOnlyList<ServeEvent>>>
{
    public ValueTask<Result<IReadOnlyList<ServeEvent>>> Handle(
        GetServeEventsQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(
            engine.GetServeEvents(query.Tenant, new ServeEventQuery { UnmatchedOnly = query.UnmatchedOnly, Limit = query.Limit })));
}

/// <summary>Resolves one journaled serve event by id via the journal's index.</summary>
public sealed class GetServeEventHandler(StubEngine engine)
    : IQueryHandler<GetServeEventQuery, Result<ServeEvent?>>
{
    public ValueTask<Result<ServeEvent?>> Handle(GetServeEventQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(
            engine.GetServeEvents(query.Tenant, new ServeEventQuery { Id = query.Id }).FirstOrDefault()));
}

/// <summary>Projects the tenant's scenarios (from the bound stubs) with their current state.</summary>
public sealed class GetScenariosHandler(IStubStore store, IScenarioStateStore states)
    : IQueryHandler<GetScenariosQuery, Result<IReadOnlyList<ScenarioView>>>
{
    public ValueTask<Result<IReadOnlyList<ScenarioView>>> Handle(GetScenariosQuery query, CancellationToken cancellationToken)
    {
        var scenarios = store.GetStubs(query.Tenant)
            .Where(stub => stub.Scenario is not null)
            .GroupBy(stub => stub.Scenario!.ScenarioName)
            .Select(group => new ScenarioView(
                group.Key,
                states.GetState(query.Tenant, group.Key),
                PossibleStates(group)))
            .ToList();

        return ValueTask.FromResult(Result.Success<IReadOnlyList<ScenarioView>>(scenarios));
    }

    // The default "Started" state plus every state the scenario's stubs require or transition to (verified by the differential suite).
    private static IReadOnlyList<string> PossibleStates(IEnumerable<StubMapping> stubs)
    {
        var states = new HashSet<string>(StringComparer.Ordinal) { "Started" };
        foreach (var stub in stubs)
        {
            if (stub.Scenario!.RequiredState is { } required)
            {
                states.Add(required);
            }

            if (stub.Scenario!.NewState is { } next)
            {
                states.Add(next);
            }
        }

        return [.. states.OrderBy(s => s, StringComparer.Ordinal)];
    }
}

/// <summary>Sets a scenario's state directly.</summary>
public sealed class SetScenarioStateHandler(IScenarioStateStore states) : ICommandHandler<SetScenarioStateCommand, Result>
{
    public ValueTask<Result> Handle(SetScenarioStateCommand command, CancellationToken cancellationToken)
    {
        states.SetState(command.Tenant, command.Name, command.State);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Resets every scenario for the tenant to <c>Started</c>.</summary>
public sealed class ResetScenariosHandler(IStubStore store, IScenarioStateStore states)
    : ICommandHandler<ResetScenariosCommand, Result>
{
    public ValueTask<Result> Handle(ResetScenariosCommand command, CancellationToken cancellationToken)
    {
        foreach (var name in store.GetStubs(command.Tenant)
                     .Select(stub => stub.Scenario?.ScenarioName)
                     .Where(name => name is not null)
                     .Distinct())
        {
            states.SetState(command.Tenant, name!, "Started");
        }

        return ValueTask.FromResult(Result.Success());
    }
}

// Environment key handlers (G17, issues #165/#166). Every one reads the tenant off the command and
// passes it to the store, so a request for tenant A can only ever touch tenant A's keys.

/// <summary>Lists the tenant's environment keys.</summary>
public sealed class GetEnvironmentsHandler(IEnvironmentStore store)
    : IQueryHandler<GetEnvironmentsQuery, Result<IReadOnlyList<EnvironmentKey>>>
{
    public ValueTask<Result<IReadOnlyList<EnvironmentKey>>> Handle(GetEnvironmentsQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(store.GetKeys(query.Tenant)));
}

/// <summary>
/// Creates or replaces an environment key. Validation is the load-bearing part: a key that is
/// malformed would be stored but never substituted, and a key named after a built-in helper would
/// silently shadow it in every stub of the tenant — so both are refused rather than accepted.
/// </summary>
public sealed class PutEnvironmentKeyHandler(IEnvironmentStore store, IEnvironmentPersistence persistence)
    : ICommandHandler<PutEnvironmentKeyCommand, Result>
{
    public ValueTask<Result> Handle(PutEnvironmentKeyCommand command, CancellationToken cancellationToken)
    {
        if (EnvironmentKeyRules.Validate(command.Key) is { } error)
        {
            return ValueTask.FromResult(Result.Failure(error));
        }

        store.Put(command.Tenant, command.Key);
        persistence.Save(command.Tenant, command.Key);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>
/// The single definition of what makes an environment key storable, shared by the admin PUT and the
/// bundle import (#198) so an imported key can never bypass a rule the editor enforces.
/// </summary>
internal static class EnvironmentKeyRules
{
    public static Error? Validate(EnvironmentKey key)
    {
        if (!ReservedEnvironmentKeys.IsWellFormed(key.Key))
        {
            return Error.Validation(
                "Environment.InvalidKey",
                "A key must start with a letter or underscore and contain only letters, digits, underscores or hyphens.");
        }

        if (ReservedEnvironmentKeys.IsReserved(key.Key))
        {
            return Error.Validation(
                "Environment.ReservedKey",
                $"'{key.Key}' is a built-in templating helper; a key of that name would shadow it in every stub.");
        }

        if (key.Values.Count == 0)
        {
            return Error.Validation("Environment.NoValues", "A key must define at least one value.");
        }

        if (key.Values.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != key.Values.Count)
        {
            return Error.Validation("Environment.DuplicateValue", "Value names must be unique within a key.");
        }

        if (key.Resolve() is null)
        {
            return Error.Validation(
                "Environment.UnknownActiveValue", $"'{key.ActiveValue}' does not name any of the key's values.");
        }

        return null;
    }
}

/// <summary>
/// Switches which value is active for a key. This is the operation issue #165 is really about: it
/// changes what every stub referencing the key resolves to, on the next request, with no re-save.
/// </summary>
public sealed class SetEnvironmentActiveValueHandler(IEnvironmentStore store, IEnvironmentPersistence persistence)
    : ICommandHandler<SetEnvironmentActiveValueCommand, Result>
{
    public ValueTask<Result> Handle(SetEnvironmentActiveValueCommand command, CancellationToken cancellationToken)
    {
        var existing = store.GetKeys(command.Tenant).FirstOrDefault(k => string.Equals(k.Key, command.Key, StringComparison.Ordinal));
        if (existing is null)
        {
            return ValueTask.FromResult(Result.Failure(Error.NotFound(
                "Environment.UnknownKey", $"No environment key named '{command.Key}'.")));
        }

        var updated = existing with { ActiveValue = command.ActiveValue };
        if (updated.Resolve() is null)
        {
            return ValueTask.FromResult(Result.Failure(Error.Validation(
                "Environment.UnknownActiveValue", $"'{command.ActiveValue}' does not name any of the key's values.")));
        }

        store.Put(command.Tenant, updated);
        persistence.Save(command.Tenant, updated);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Deletes an environment key from the tenant that owns it.</summary>
public sealed class DeleteEnvironmentKeyHandler(IEnvironmentStore store, IEnvironmentPersistence persistence)
    : ICommandHandler<DeleteEnvironmentKeyCommand, Result>
{
    public ValueTask<Result> Handle(DeleteEnvironmentKeyCommand command, CancellationToken cancellationToken)
    {
        // Remove reports whether THIS tenant owned the key, so a delete aimed at another tenant's key
        // is a 404 rather than a silent success that suggests it worked.
        if (!store.Remove(command.Tenant, command.Key))
        {
            return ValueTask.FromResult(Result.Failure(Error.NotFound(
                "Environment.UnknownKey", $"No environment key named '{command.Key}'.")));
        }

        persistence.Remove(command.Tenant, command.Key);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Deletes every environment key owned by the tenant.</summary>
public sealed class ResetEnvironmentsHandler(IEnvironmentStore store, IEnvironmentPersistence persistence)
    : ICommandHandler<ResetEnvironmentsCommand, Result>
{
    public ValueTask<Result> Handle(ResetEnvironmentsCommand command, CancellationToken cancellationToken)
    {
        store.Clear(command.Tenant);
        persistence.Clear(command.Tenant);
        return ValueTask.FromResult(Result.Success());
    }
}

// ---- Sandbox resources (G19a, ADR 0011) --------------------------------------------------------

/// <summary>Lists the tenant's collections with counts.</summary>
public sealed class GetResourceCollectionsHandler(IResourceStore store)
    : IQueryHandler<GetResourceCollectionsQuery, Result<IReadOnlyList<ResourceCollectionInfo>>>
{
    public ValueTask<Result<IReadOnlyList<ResourceCollectionInfo>>> Handle(GetResourceCollectionsQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(store.GetCollections(query.Tenant)));
}

/// <summary>Pages through one collection. An unknown collection is an honest empty page, not an error.</summary>
public sealed class ListResourcesHandler(IResourceStore store)
    : IQueryHandler<ListResourcesQuery, Result<ResourcePage>>
{
    public ValueTask<Result<ResourcePage>> Handle(ListResourcesQuery query, CancellationToken cancellationToken)
    {
        if (ResourceRules.ValidateCollection(query.Collection) is { } error)
        {
            return ValueTask.FromResult<Result<ResourcePage>>(error);
        }

        var limit = Math.Clamp(query.Limit ?? 100, 1, 500);
        var offset = Math.Max(query.Offset ?? 0, 0);
        var documents = store.List(query.Tenant, query.Collection);
        var page = documents.Skip(offset).Take(limit).ToArray();
        return ValueTask.FromResult<Result<ResourcePage>>(new ResourcePage(page, documents.Count));
    }
}

/// <summary>Reads one document.</summary>
public sealed class GetResourceHandler(IResourceStore store)
    : IQueryHandler<GetResourceQuery, Result<ResourceDocument>>
{
    public ValueTask<Result<ResourceDocument>> Handle(GetResourceQuery query, CancellationToken cancellationToken)
    {
        if (ResourceRules.ValidateCollection(query.Collection) is { } error)
        {
            return ValueTask.FromResult<Result<ResourceDocument>>(error);
        }

        var document = store.Get(query.Tenant, query.Collection, query.Id);
        return document is null
            ? ValueTask.FromResult<Result<ResourceDocument>>(Error.NotFound(
                "Resource.NotFound", $"No document '{query.Id}' in collection '{query.Collection}'."))
            : ValueTask.FromResult<Result<ResourceDocument>>(document);
    }
}

/// <summary>Creates or replaces one document after the shared validation.</summary>
public sealed class PutResourceHandler(IResourceStore store, ResourceOptions options, IResourcePersistence persistence)
    : ICommandHandler<PutResourceCommand, Result<ResourceDocument>>
{
    public ValueTask<Result<ResourceDocument>> Handle(PutResourceCommand command, CancellationToken cancellationToken)
    {
        var error = ResourceRules.ValidateCollection(command.Collection)
            ?? ResourceRules.ValidateId(command.Id)
            ?? ResourceRules.ValidateBody(command.Body, options);
        if (error is not null)
        {
            return ValueTask.FromResult<Result<ResourceDocument>>(error);
        }

        // Persisted after the store accepts it, so what survives a restart is exactly what the store
        // holds — including the CreatedAt/version bookkeeping a replace works out.
        var document = store.Put(command.Tenant, command.Collection, command.Id, command.Body);
        persistence.Save(command.Tenant, document);
        return ValueTask.FromResult<Result<ResourceDocument>>(document);
    }
}

/// <summary>Deletes one document; unknown ids are an honest 404, mirroring a real API.</summary>
public sealed class DeleteResourceHandler(IResourceStore store, IResourcePersistence persistence)
    : ICommandHandler<DeleteResourceCommand, Result>
{
    public ValueTask<Result> Handle(DeleteResourceCommand command, CancellationToken cancellationToken)
    {
        if (ResourceRules.ValidateCollection(command.Collection) is { } error)
        {
            return ValueTask.FromResult(Result.Failure(error));
        }

        if (!store.Delete(command.Tenant, command.Collection, command.Id))
        {
            return ValueTask.FromResult(Result.Failure(Error.NotFound(
                "Resource.NotFound", $"No document '{command.Id}' in collection '{command.Collection}'.")));
        }

        // Only after the store agrees it existed: persisting a delete for a document that was not
        // there would be a write nobody asked for.
        persistence.Remove(command.Tenant, command.Collection, command.Id);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>Clears one collection, or the tenant's whole resource state.</summary>
public sealed class ResetResourcesHandler(IResourceStore store, IResourcePersistence persistence)
    : ICommandHandler<ResetResourcesCommand, Result>
{
    public ValueTask<Result> Handle(ResetResourcesCommand command, CancellationToken cancellationToken)
    {
        if (command.Collection is null)
        {
            store.ResetAll(command.Tenant);
            persistence.Clear(command.Tenant, collection: null);
            return ValueTask.FromResult(Result.Success());
        }

        if (ResourceRules.ValidateCollection(command.Collection) is { } error)
        {
            return ValueTask.FromResult(Result.Failure(error));
        }

        store.Reset(command.Tenant, command.Collection);
        persistence.Clear(command.Tenant, command.Collection);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>
/// Seeds a collection from a JSON array. Transactional per the ADR 0011 addendum: every item is
/// validated before anything is stored, so a bad element means nothing landed.
/// </summary>
public sealed class SeedResourcesHandler(
    IResourceStore store, IResourceIdGenerator ids, ResourceOptions options, IResourcePersistence persistence)
    : ICommandHandler<SeedResourcesCommand, Result<int>>
{
    public ValueTask<Result<int>> Handle(SeedResourcesCommand command, CancellationToken cancellationToken)
    {
        if (ResourceRules.ValidateCollection(command.Collection) is { } collectionError)
        {
            return ValueTask.FromResult<Result<int>>(collectionError);
        }

        foreach (var item in command.Items)
        {
            var error = (item.Id is { } id ? ResourceRules.ValidateId(id) : null)
                ?? ResourceRules.ValidateBody(item.Body, options);
            if (error is not null)
            {
                return ValueTask.FromResult<Result<int>>(error);
            }
        }

        foreach (var item in command.Items)
        {
            var document = store.Put(
                command.Tenant, command.Collection, item.Id ?? ids.NextId(command.Collection), item.Body);
            persistence.Save(command.Tenant, document);
        }

        return ValueTask.FromResult<Result<int>>(command.Items.Count);
    }
}

/// <summary>
/// Imports an OpenAPI document by generating mapping JSON at the edge and feeding it through the
/// SAME reader as any bundle — dialect compliance by construction. Fully transactional: every
/// mapping parses before anything is stored.
/// </summary>
public sealed class ImportOpenApiHandler(
    IStubStore store,
    IMatcherRegistry matchers,
    IStubPersistence persistence,
    IResourceSchemaStore schemas)
    : ICommandHandler<ImportOpenApiCommand, Result<int>>
{
    public ValueTask<Result<int>> Handle(ImportOpenApiCommand command, CancellationToken cancellationToken)
    {
        List<(StubMapping Stub, string Source)> stubs = [];
        IReadOnlyList<ResourceSchema> relations = [];
        try
        {
            var generated = Mockifyr.Adapters.OpenApi.OpenApiStubGenerator.GenerateWithRelations(
                command.SpecText, command.Stateful);
            relations = generated.Relations;
            foreach (var mappingJson in generated.Mappings)
            {
                stubs.AddRange(MappingJsonReader.ReadWithSource(mappingJson, command.Tenant, matchers));
            }
        }
        catch (Mockifyr.Adapters.OpenApi.OpenApiImportException exception)
        {
            return ValueTask.FromResult<Result<int>>(Error.Validation("OpenApi." + exception.Error, exception.Message));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return ValueTask.FromResult<Result<int>>(Error.Validation(
                "OpenApi.Invalid", "A generated mapping did not read back: " + exception.Message));
        }

        foreach (var (stub, source) in stubs)
        {
            store.Put(stub);
            persistence.Save(stub, source);
        }

        // The relations the path shapes declared (ADR 0015). Applied after the mappings for the same
        // reason the mappings are applied only once they all parse: an import that half-landed would
        // leave a sandbox whose stubs and whose relations disagree.
        foreach (var relation in relations)
        {
            schemas.Put(command.Tenant, relation);
        }

        return ValueTask.FromResult<Result<int>>(stubs.Count);
    }
}

// ---- Sandbox access (G19d, ADR 0011) -----------------------------------------------------------

/// <summary>
/// Issues a key: 256-bit CSPRNG token, salted hash stored, token returned EXACTLY once. The name
/// is a display label (bounded); the quota, when present, must be positive.
/// </summary>
public sealed class IssueApiKeyHandler(IApiKeyStore store, IApiKeyPersistence persistence)
    : ICommandHandler<IssueApiKeyCommand, Result<IssuedApiKey>>
{
    public ValueTask<Result<IssuedApiKey>> Handle(IssueApiKeyCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        if (name.Length is 0 or > 64 || name.Any(char.IsControl))
        {
            return ValueTask.FromResult<Result<IssuedApiKey>>(Error.Validation(
                "ApiKey.InvalidName", "A key name must be 1..64 characters with no control characters."));
        }

        if (command.QuotaPerHour is <= 0)
        {
            return ValueTask.FromResult<Result<IssuedApiKey>>(Error.Validation(
                "ApiKey.InvalidQuota", "A quota must be a positive number of requests per hour."));
        }

        var (token, salt, hash) = ApiKeyMaterial.Generate();
        var key = new ApiKey(
            Guid.NewGuid().ToString("D"), command.Tenant, name, salt, hash,
            ApiKeyMaterial.DisplayPrefix(token), DateTimeOffset.UtcNow, command.QuotaPerHour);

        store.Put(key);
        persistence.Save(key);
        return ValueTask.FromResult<Result<IssuedApiKey>>(new IssuedApiKey(key, token));
    }
}

/// <summary>Lists the tenant's keys with their current-window usage.</summary>
public sealed class GetApiKeysHandler(IApiKeyStore store, FixedWindowRateLimiter limiter)
    : IQueryHandler<GetApiKeysQuery, Result<IReadOnlyList<ApiKeyWithUsage>>>
{
    public ValueTask<Result<IReadOnlyList<ApiKeyWithUsage>>> Handle(GetApiKeysQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success<IReadOnlyList<ApiKeyWithUsage>>(
            [.. store.GetKeys(query.Tenant).Select(key => new ApiKeyWithUsage(key, limiter.Used(key.Id)))]));
}

/// <summary>
/// Revokes a key. Tenant-checked: one tenant can never revoke another's credential, and a
/// cross-tenant id answers the same 404 as an unknown one (no existence oracle).
/// </summary>
public sealed class RevokeApiKeyHandler(IApiKeyStore store, IApiKeyPersistence persistence)
    : ICommandHandler<RevokeApiKeyCommand, Result>
{
    public ValueTask<Result> Handle(RevokeApiKeyCommand command, CancellationToken cancellationToken)
    {
        var key = store.Get(command.Id);
        if (key is null || key.Tenant != command.Tenant)
        {
            return ValueTask.FromResult(Result.Failure(Error.NotFound(
                "ApiKey.NotFound", $"No API key '{command.Id}'.")));
        }

        store.Remove(command.Id);
        persistence.Remove(command.Id);
        return ValueTask.FromResult(Result.Success());
    }
}

/// <summary>
/// Reads the tenant's audit entries (#247). Tenant-scoped like every other query here: one tenant's
/// administrative history is not another's to read, and the limit is clamped so a caller cannot ask
/// for a response big enough to hurt the host.
/// </summary>
public sealed class GetAuditEntriesHandler(IAuditLog log)
    : IQueryHandler<GetAuditEntriesQuery, Result<IReadOnlyList<AuditEntry>>>
{
    public ValueTask<Result<IReadOnlyList<AuditEntry>>> Handle(
        GetAuditEntriesQuery query, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result.Success(
            log.Read(query.Tenant, Math.Clamp(query.Limit ?? 200, 1, 1000))));
}

/// <summary>
/// Gathers everything a tenant's operator authored into one archive (#252).
/// </summary>
/// <remarks>
/// Stubs travel as their authored source, so a restore reproduces what was written rather than a
/// re-serialization of it. Scenario states are read for the scenarios the stubs actually declare —
/// there is no scenario registry to enumerate, and a state without a stub to drive it is meaningless.
/// </remarks>
public sealed class CreateBackupHandler(
    IStubStore stubs,
    IEnvironmentStore environments,
    IResourceStore resources,
    IApiKeyStore apiKeys,
    IScenarioStateStore scenarios)
    : IQueryHandler<CreateBackupQuery, Result<BackupArchive>>
{
    public ValueTask<Result<BackupArchive>> Handle(CreateBackupQuery query, CancellationToken cancellationToken)
    {
        var tenantStubs = stubs.GetStubs(query.Tenant);
        var mappings = tenantStubs
            .Select(stub => stub.Source)
            .OfType<string>()
            .ToList();

        var documents = new List<ResourceDocument>();
        foreach (var collection in resources.GetCollections(query.Tenant))
        {
            documents.AddRange(resources.List(query.Tenant, collection.Name));
        }

        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in tenantStubs
            .Select(stub => stub.Scenario?.ScenarioName)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal))
        {
            states[name] = scenarios.GetState(query.Tenant, name);
        }

        return ValueTask.FromResult(Result.Success(new BackupArchive(
            query.Tenant,
            DateTimeOffset.UtcNow,
            mappings,
            environments.GetKeys(query.Tenant),
            documents,
            apiKeys.GetKeys(query.Tenant),
            states)));
    }
}

/// <summary>
/// Restores an archive into a tenant (#252).
/// </summary>
/// <remarks>
/// <para>
/// Replace, not merge: each section the archive carries is cleared first, so a restored host is the
/// host that was backed up rather than a union with whatever was there. Merging would leave stubs the
/// backup does not know about still serving — the opposite of what a restore is for. A section the
/// archive omits is left alone, which is what makes a partial archive usable.
/// </para>
/// <para>
/// The archive's own tenant name is ignored in favour of the caller's: restoring production's archive
/// into a staging tenant is a normal drill, and an archive that could re-target itself would be a
/// cross-tenant write from a file.
/// </para>
/// </remarks>
public sealed class RestoreBackupHandler(
    IStubStore stubs,
    IStubPersistence stubPersistence,
    IMatcherRegistry matchers,
    IEnvironmentStore environments,
    IEnvironmentPersistence environmentPersistence,
    IResourceStore resources,
    IApiKeyStore apiKeys,
    IApiKeyPersistence apiKeyPersistence,
    IScenarioStateStore scenarios)
    : ICommandHandler<RestoreBackupCommand, Result<RestoreSummary>>
{
    public ValueTask<Result<RestoreSummary>> Handle(RestoreBackupCommand command, CancellationToken cancellationToken)
    {
        if (BackupJson.Read(command.ArchiveJson) is not { } archive)
        {
            return ValueTask.FromResult<Result<RestoreSummary>>(Error.Validation(
                "Backup.Invalid",
                "This is not a Mockifyr backup archive, or it was written by a newer version."));
        }

        // Parse every mapping before writing anything: a restore that fails halfway leaves a tenant in
        // a state neither the archive nor the operator can describe.
        List<(StubMapping Stub, string Source)> parsed = [];
        foreach (var mapping in archive.Mappings)
        {
            try
            {
                parsed.AddRange(MappingJsonReader.ReadWithSource(mapping, command.Tenant, matchers));
            }
            catch (JsonException)
            {
                return ValueTask.FromResult<Result<RestoreSummary>>(Error.Validation(
                    "Backup.InvalidMapping", "The archive contains a mapping that is not valid JSON."));
            }
        }

        foreach (var existing in stubs.GetStubs(command.Tenant).ToList())
        {
            stubs.Remove(command.Tenant, existing.Id);
        }

        stubPersistence.Clear(command.Tenant);
        foreach (var (stub, source) in parsed)
        {
            stubs.Put(stub);
            stubPersistence.Save(stub, source);
        }

        environments.Clear(command.Tenant);
        environmentPersistence.Clear(command.Tenant);
        foreach (var key in archive.Environments)
        {
            environments.Put(command.Tenant, key);
            environmentPersistence.Save(command.Tenant, key);
        }

        resources.ResetAll(command.Tenant);
        foreach (var document in archive.Resources)
        {
            resources.Put(command.Tenant, document.Collection, document.Id, document.Body);
        }

        foreach (var existing in apiKeys.GetKeys(command.Tenant))
        {
            apiKeys.Remove(existing.Id);
            apiKeyPersistence.Remove(existing.Id);
        }

        foreach (var key in archive.ApiKeys)
        {
            // Re-tenanted to the restore target, so the key selects the tenant it was restored into.
            var restored = key with { Tenant = command.Tenant };
            apiKeys.Put(restored);
            apiKeyPersistence.Save(restored);
        }

        foreach (var (scenario, state) in archive.Scenarios)
        {
            scenarios.SetState(command.Tenant, scenario, state);
        }

        return ValueTask.FromResult(Result.Success(new RestoreSummary(
            parsed.Count,
            archive.Environments.Count,
            archive.Resources.Count,
            archive.ApiKeys.Count,
            archive.Scenarios.Count)));
    }
}
