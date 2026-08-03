using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Tenant- and collection-scoped in-memory sandbox document store (G19a, ADR 0011). Bounded per
/// collection: beyond <see cref="Capacity"/> the oldest document is evicted first (the message
/// inbox's ethos — an unattended host cannot grow without limit). Updates are last-write-wins and
/// keep the document's insertion position; timestamps come from the injected
/// <see cref="TimeProvider"/> so tests assert exact values. Thread-safe.
/// </summary>
public sealed class InMemoryResourceStore(
    int capacity = InMemoryResourceStore.DefaultCapacity, TimeProvider? clock = null) : IResourceStore
{
    /// <summary>The default per-collection document bound.</summary>
    public const int DefaultCapacity = 1000;

    private readonly Dictionary<TenantId, Dictionary<string, List<ResourceDocument>>> _byTenant = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();

    /// <summary>The per-collection bound this store was built with.</summary>
    public int Capacity { get; } = capacity > 0 ? capacity : DefaultCapacity;

    /// <inheritdoc />
    public IReadOnlyList<ResourceCollectionInfo> GetCollections(TenantId tenant)
    {
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var collections))
            {
                return [];
            }

            return [.. collections
                .Where(pair => pair.Value.Count > 0)
                .Select(pair => new ResourceCollectionInfo(pair.Key, pair.Value.Count))
                .OrderBy(info => info.Name, StringComparer.Ordinal)];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ResourceDocument> List(TenantId tenant, string collection)
    {
        lock (_gate)
        {
            return Documents(tenant, collection) is { } documents ? [.. documents] : [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<TenantId> GetTenants()
    {
        lock (_gate)
        {
            // Only tenants that still hold a document: Reset leaves an empty collection behind, and a
            // tenant reduced to those is gone as far as a reload is concerned.
            return [.. _byTenant.Where(pair => pair.Value.Any(c => c.Value.Count > 0)).Select(pair => pair.Key)];
        }
    }

    /// <inheritdoc />
    public ResourceDocument? Get(TenantId tenant, string collection, string id)
    {
        lock (_gate)
        {
            return Documents(tenant, collection)?.Find(d => string.Equals(d.Id, id, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public ResourceDocument Put(TenantId tenant, string collection, string id, string body)
    {
        lock (_gate)
        {
            var collections = _byTenant.TryGetValue(tenant, out var existing) ? existing : _byTenant[tenant] = [];
            var documents = collections.TryGetValue(collection, out var list) ? list : collections[collection] = [];
            var now = _clock.GetUtcNow();

            var index = documents.FindIndex(d => string.Equals(d.Id, id, StringComparison.Ordinal));
            if (index >= 0)
            {
                // Replace in place: CreatedAt and the insertion position survive, the version advances.
                var previous = documents[index];
                var updated = previous with { Body = body, UpdatedAt = now, Version = previous.Version + 1 };
                documents[index] = updated;
                return updated;
            }

            var created = new ResourceDocument(id, collection, body, now, now, Version: 1);
            documents.Add(created);
            // One create can overflow by at most one, so evicting the single oldest entry is enough.
            if (documents.Count > Capacity)
            {
                documents.RemoveAt(0);
            }

            return created;
        }
    }

    /// <inheritdoc />
    public void Restore(TenantId tenant, ResourceDocument document)
    {
        lock (_gate)
        {
            var collections = _byTenant.TryGetValue(tenant, out var existing) ? existing : _byTenant[tenant] = [];
            var documents = collections.TryGetValue(document.Collection, out var list)
                ? list
                : collections[document.Collection] = [];

            // Timestamps and version come from the document, not the clock: this is another instance's
            // write being mirrored, not a write happening here.
            var index = documents.FindIndex(d => string.Equals(d.Id, document.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                documents[index] = document;
                return;
            }

            documents.Add(document);
            if (documents.Count > Capacity)
            {
                documents.RemoveAt(0);
            }
        }
    }

    /// <inheritdoc />
    public bool Delete(TenantId tenant, string collection, string id)
    {
        lock (_gate)
        {
            return Documents(tenant, collection)?.RemoveAll(d => string.Equals(d.Id, id, StringComparison.Ordinal)) > 0;
        }
    }

    /// <inheritdoc />
    public void Reset(TenantId tenant, string collection)
    {
        lock (_gate)
        {
            if (_byTenant.TryGetValue(tenant, out var collections))
            {
                collections.Remove(collection);
            }
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

    private List<ResourceDocument>? Documents(TenantId tenant, string collection) =>
        _byTenant.TryGetValue(tenant, out var collections) && collections.TryGetValue(collection, out var documents)
            ? documents
            : null;
}
