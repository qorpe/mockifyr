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

/// <summary>Counts journaled requests matching the given request pattern.</summary>
public sealed class CountRequestsHandler(StubEngine engine) : IQueryHandler<CountRequestsQuery, Result<int>>
{
    public ValueTask<Result<int>> Handle(CountRequestsQuery query, CancellationToken cancellationToken)
    {
        var pattern = MappingJsonReader.ReadRequestPattern(query.PatternJson);
        return ValueTask.FromResult<Result<int>>(engine.CountRequestsMatching(query.Tenant, pattern));
    }
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
