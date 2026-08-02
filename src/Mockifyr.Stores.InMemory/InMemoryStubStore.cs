using System.Collections.Concurrent;
using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Tenant-scoped in-memory stub store. Stubs are returned in insertion order so the engine can
/// break priority ties by recency (last added wins). This is the default (ephemeral) provider;
/// durable providers slot in behind <see cref="IStubStore"/> later. See docs/decisions/0006.
/// </summary>
public sealed class InMemoryStubStore : IStubStore
{
    private readonly ConcurrentDictionary<TenantId, List<StubMapping>> _byTenant = new();
    private readonly Lock _gate = new();

    // Per-tenant match index (#265), rebuilt lazily after a mutation. Invalidated by dropping the
    // entry inside the same lock every mutation already takes, so a stale index cannot outlive the
    // change that made it stale.
    private readonly Dictionary<TenantId, StubIndex> _indexes = [];

    /// <inheritdoc />
    public IReadOnlyList<StubMapping> GetStubs(TenantId tenant)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var stubs)
                ? [.. stubs]
                : [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<TenantId> GetTenants()
    {
        lock (_gate)
        {
            // Only tenants that still own at least one stub (an emptied list is pruned by Remove callers
            // conceptually, but guard here so a drained tenant isn't reported).
            return [.. _byTenant.Where(entry => entry.Value.Count > 0).Select(entry => entry.Key)];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<StubMapping> GetCandidates(TenantId tenant, CanonicalRequest request)
    {
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var stubs) || stubs.Count == 0)
            {
                return [];
            }

            if (!_indexes.TryGetValue(tenant, out var index))
            {
                _indexes[tenant] = index = new StubIndex(stubs);
            }

            return index.Candidates(request);
        }
    }

    /// <inheritdoc />
    public void Put(StubMapping stub)
    {
        lock (_gate)
        {
            _indexes.Remove(stub.TenantId);
            var stubs = _byTenant.GetOrAdd(stub.TenantId, static _ => []);
            var existing = stubs.FindIndex(s => s.Id == stub.Id);
            if (existing >= 0)
            {
                stubs[existing] = stub;
            }
            else
            {
                stubs.Add(stub);
            }
        }
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, Guid id)
    {
        lock (_gate)
        {
            _indexes.Remove(tenant);
            if (_byTenant.TryGetValue(tenant, out var stubs))
            {
                stubs.RemoveAll(s => s.Id == id);
            }
        }
    }
}
