using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Tenant-scoped in-memory store of collection relation schemas (ADR 0015). Small and read-heavy by
/// nature — a schema is written when a spec is imported or an operator declares one, and read on
/// every create, list and delete — so it is a plain dictionary behind the same lock discipline as
/// <see cref="InMemoryResourceStore"/>. Thread-safe.
/// </summary>
public sealed class InMemoryResourceSchemaStore : IResourceSchemaStore
{
    private readonly Dictionary<TenantId, Dictionary<string, ResourceSchema>> _byTenant = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public IReadOnlyList<ResourceSchema> List(TenantId tenant)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var schemas)
                ? [.. schemas.Values.OrderBy(schema => schema.Collection, StringComparer.Ordinal)]
                : [];
        }
    }

    /// <inheritdoc />
    public ResourceSchema? Get(TenantId tenant, string collection)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var schemas) && schemas.TryGetValue(collection, out var schema)
                ? schema
                : null;
        }
    }

    /// <inheritdoc />
    public void Put(TenantId tenant, ResourceSchema schema)
    {
        lock (_gate)
        {
            var schemas = _byTenant.TryGetValue(tenant, out var existing) ? existing : _byTenant[tenant] = [];
            schemas[schema.Collection] = schema;
        }
    }

    /// <inheritdoc />
    public bool Delete(TenantId tenant, string collection)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var schemas) && schemas.Remove(collection);
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<TenantId> GetTenants()
    {
        lock (_gate)
        {
            return [.. _byTenant.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key)];
        }
    }

    /// <inheritdoc />
    public void ResetAll(TenantId tenant)
    {
        lock (_gate)
        {
            _byTenant.Remove(tenant);
        }
    }
}
