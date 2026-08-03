namespace Mockifyr.Core;

// Extension seams. The built-in features are themselves implementations of these interfaces
// (dogfooding), so the extension mechanism is real from G1 rather than bolted on later. They
// are made public/registerable at roadmap item G10. See ARCHITECTURE.md section 9.

/// <summary>Base marker for an extension with an optional lifecycle.</summary>
public interface IExtension
{
    /// <summary>Called once when the engine starts.</summary>
    void Start() { }

    /// <summary>Called once when the engine stops.</summary>
    void Stop() { }
}

/// <summary>Transforms a rendered response. The built-in <c>response-template</c> is one of these.</summary>
public interface IResponseTransformer : IExtension
{
    /// <summary>Unique transformer name, referenced from stub JSON.</summary>
    string Name { get; }

    /// <summary>Whether the transformer applies to every response by default.</summary>
    bool ApplyGlobally => true;

    /// <summary>Transforms the response for the given serve event.</summary>
    CanonicalResponse Transform(CanonicalResponse response, ServeEvent serveEvent);
}

/// <summary>Transforms a <see cref="ResponseDefinition"/> before it is rendered.</summary>
public interface IResponseDefinitionTransformer : IExtension
{
    /// <summary>Unique transformer name.</summary>
    string Name { get; }

    /// <summary>Transforms the definition for the given serve event.</summary>
    ResponseDefinition Transform(ResponseDefinition definition, ServeEvent serveEvent);
}

/// <summary>A single templating helper (e.g. <c>jsonPath</c>, <c>now</c>, <c>randomValue</c>).</summary>
public interface ITemplateHelper
{
    /// <summary>The helper name as used in a template.</summary>
    string Name { get; }
}

/// <summary>
/// A user-supplied template helper (G10): its <see cref="ITemplateHelper.Name"/> plus a render
/// function over the positional arguments. The templating edge adapts it to the underlying engine, so
/// the public API stays engine-agnostic.
/// </summary>
public sealed record TemplateHelperExtension(string Name, Func<IReadOnlyList<object?>, string> Render) : ITemplateHelper;

/// <summary>Contributes one or more <see cref="ITemplateHelper"/> instances.</summary>
public interface ITemplateHelperProvider : IExtension
{
    /// <summary>The helpers this provider contributes.</summary>
    IReadOnlyList<ITemplateHelper> GetHelpers();
}

/// <summary>Contributes extra data to the template model beyond the request.</summary>
public interface ITemplateModelProvider : IExtension
{
    /// <summary>Contributes model data for the given serve event.</summary>
    IReadOnlyDictionary<string, object?> GetModelData(ServeEvent serveEvent);
}

/// <summary>Resolves named custom matchers referenced from stub JSON.</summary>
public interface IMatcherRegistry
{
    /// <summary>Registers a named matcher factory.</summary>
    void Register(string name, IMatcher matcher);

    /// <summary>Resolves a matcher by name, or null when unknown.</summary>
    IMatcher? Resolve(string name);
}

/// <summary>
/// A request into a custom admin endpoint under <c>/__admin/ext/&lt;prefix&gt;/…</c>. Transport-agnostic:
/// the facade lowers an HTTP request to this shape so the extension never sees an <c>HttpContext</c>.
/// </summary>
/// <param name="Method">The HTTP method (e.g. <c>GET</c>).</param>
/// <param name="Subpath">The path <em>after</em> the extension's route prefix, leading slash included
/// (e.g. <c>/status</c>); empty when the prefix itself was requested.</param>
/// <param name="Query">The raw query string including the leading <c>?</c>, or empty.</param>
/// <param name="Body">The request body bytes (empty when none).</param>
public sealed record AdminApiRequest(string Method, string Subpath, string Query, byte[] Body);

/// <summary>A response from a custom admin endpoint.</summary>
/// <param name="Status">The HTTP status code.</param>
/// <param name="ContentType">The <c>Content-Type</c> to write.</param>
/// <param name="Body">The response body bytes.</param>
public sealed record AdminApiResponse(int Status, string ContentType, byte[] Body)
{
    /// <summary>A JSON response (UTF-8, <c>application/json</c>).</summary>
    public static AdminApiResponse Json(string json, int status = 200) =>
        new(status, "application/json", System.Text.Encoding.UTF8.GetBytes(json));
}

/// <summary>
/// Adds custom endpoints under <c>/__admin/ext/&lt;RoutePrefix&gt;/*</c>. The facade routes any request
/// whose first path segment under <c>/__admin/ext/</c> equals <see cref="RoutePrefix"/> to
/// <see cref="HandleAsync"/>; the extension owns everything below that prefix.
/// </summary>
public interface IAdminApiExtension : IExtension
{
    /// <summary>The route prefix this extension serves (one path segment, no slashes).</summary>
    string RoutePrefix { get; }

    /// <summary>Handles a request addressed to this extension's prefix.</summary>
    Task<AdminApiResponse> HandleAsync(AdminApiRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Loads stub mappings from a source (e.g. a mappings directory) into a tenant.</summary>
public interface IMappingsLoader : IExtension
{
    /// <summary>Loads mappings for the given tenant.</summary>
    IReadOnlyList<StubMapping> Load(TenantId tenant);
}

/// <summary>
/// An optional capability for an <see cref="IMappingsLoader"/> whose backing store spans tenants (a
/// database or key-value backend): loads every persisted mapping across <em>all</em> tenants at once,
/// each carrying its own <see cref="TenantId"/>. Change-feed reload (G16g) uses it to reconcile every
/// tenant, not just the default one. Single-tenant loaders (e.g. a mappings directory) do not implement
/// it, so the reconciler falls back to the default tenant for those.
/// </summary>
public interface IMultiTenantMappingsLoader
{
    /// <summary>Loads every persisted mapping across all tenants.</summary>
    IReadOnlyList<StubMapping> LoadAllTenants();
}

/// <summary>
/// Durably persists stub mutations so they survive a restart (G16). The management path calls this
/// alongside the in-memory store; on startup an <see cref="IMappingsLoader"/> reloads what was saved.
/// The default is a no-op (<see cref="NullStubPersistence"/>, purely in-memory); a file/db-backed
/// provider is registered when persistence is configured. Tenant-scoped like the stores.
/// </summary>
public interface IStubPersistence : IExtension
{
    /// <summary>
    /// The provider name surfaced by diagnostics (the admin health endpoint / dashboard). Defaults
    /// to the implementing type's name; a delegating provider reports its EFFECTIVE inner provider.
    /// </summary>
    string ProviderName => GetType().Name;

    /// <summary>Persists a stub. <paramref name="mappingJson"/> is its source JSON in the imported stub dialect (single mapping).</summary>
    void Save(StubMapping stub, string mappingJson);

    /// <summary>Removes a persisted stub by id.</summary>
    void Remove(TenantId tenant, Guid id);

    /// <summary>Removes every persisted stub for a tenant.</summary>
    void Clear(TenantId tenant);
}

/// <summary>The default no-op persistence: the store is purely in-memory and nothing survives a restart.</summary>
public sealed class NullStubPersistence : IStubPersistence
{
    /// <inheritdoc />
    public void Save(StubMapping stub, string mappingJson) { }

    /// <inheritdoc />
    public void Remove(TenantId tenant, Guid id) { }

    /// <inheritdoc />
    public void Clear(TenantId tenant) { }
}

/// <summary>
/// Durable persistence for environment keys (G17), mirroring <see cref="IStubPersistence"/>: writes go
/// through it alongside the in-memory store, and <see cref="IEnvironmentsLoader"/> reloads on startup.
/// The default is a no-op, so a purely in-memory host keeps working unchanged.
/// </summary>
public interface IEnvironmentPersistence : IExtension
{
    /// <summary>The provider name surfaced by diagnostics.</summary>
    string ProviderName => GetType().Name;

    /// <summary>Persists a key for a tenant.</summary>
    void Save(TenantId tenant, EnvironmentKey key);

    /// <summary>Removes a persisted key.</summary>
    void Remove(TenantId tenant, string key);

    /// <summary>Removes every persisted key for a tenant.</summary>
    void Clear(TenantId tenant);
}

/// <summary>The default no-op environment persistence: nothing survives a restart.</summary>
public sealed class NullEnvironmentPersistence : IEnvironmentPersistence
{
    /// <inheritdoc />
    public void Save(TenantId tenant, EnvironmentKey key) { }

    /// <inheritdoc />
    public void Remove(TenantId tenant, string key) { }

    /// <inheritdoc />
    public void Clear(TenantId tenant) { }
}

/// <summary>Reads persisted environment keys back at startup, the counterpart of <see cref="IEnvironmentPersistence"/>.</summary>
public interface IEnvironmentsLoader : IExtension
{
    /// <summary>Loads every persisted key, grouped by the tenant that owns it.</summary>
    IReadOnlyDictionary<TenantId, IReadOnlyList<EnvironmentKey>> LoadAll();
}

/// <summary>Filters or short-circuits requests before matching.</summary>
public interface IRequestFilter : IExtension
{
    /// <summary>The filter name.</summary>
    string Name { get; }
}

/// <summary>
/// Persists sandbox resource documents (G19a) so they survive a restart, mirroring
/// <see cref="IStubPersistence"/> and <see cref="IEnvironmentPersistence"/>.
/// </summary>
/// <remarks>
/// Resources were in-memory only, which sat badly with calling this an integration sandbox: a partner
/// seeds their fixtures, the pod restarts, and their data is gone. Backup and restore covered the
/// deliberate case; this covers the one nobody plans for.
/// </remarks>
public interface IResourcePersistence : IExtension
{
    /// <summary>Persists one document (create or replace).</summary>
    void Save(TenantId tenant, ResourceDocument document);

    /// <summary>Removes one persisted document.</summary>
    void Remove(TenantId tenant, string collection, string id);

    /// <summary>Removes a whole collection, or — with a null collection — everything the tenant owns.</summary>
    void Clear(TenantId tenant, string? collection);
}

/// <summary>The default: nothing survives a restart.</summary>
public sealed class NullResourcePersistence : IResourcePersistence
{
    /// <inheritdoc />
    public void Save(TenantId tenant, ResourceDocument document) { }

    /// <inheritdoc />
    public void Remove(TenantId tenant, string collection, string id) { }

    /// <inheritdoc />
    public void Clear(TenantId tenant, string? collection) { }
}

/// <summary>Reads persisted resources back at startup, the counterpart of <see cref="IResourcePersistence"/>.</summary>
public interface IResourcesLoader : IExtension
{
    /// <summary>Every persisted document, grouped by the tenant that owns it.</summary>
    IReadOnlyDictionary<TenantId, IReadOnlyList<ResourceDocument>> LoadAll();
}
