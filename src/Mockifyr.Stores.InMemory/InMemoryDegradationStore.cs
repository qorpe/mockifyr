using System.Collections.Concurrent;
using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Tenant-scoped degradation profiles held in memory (#289), serving both the admin store and the
/// serve-path resolver so flipping a profile applies to the very next request.
/// </summary>
/// <remarks>
/// <para>
/// The per-tenant ordinal is an <see cref="Interlocked"/> counter rather than a shared
/// <see cref="Random"/>: a counter is thread-safe without a lock on the hot path, and pairing it with
/// the profile's seed is what makes a run reproducible. Setting a profile restarts the count, so
/// "seed 42, from the beginning" means the same thing twice.
/// </para>
/// <para>
/// Only degraded tenants occupy a slot; a healthy host holds an empty dictionary and answers every
/// lookup from a readonly static.
/// </para>
/// </remarks>
public sealed class InMemoryDegradationStore : IDegradationStore, IDegradationResolver
{
    private sealed class Entry(DegradationProfile profile)
    {
        public DegradationProfile Profile { get; } = profile;

        public long Served;
    }

    private readonly ConcurrentDictionary<TenantId, Entry> _byTenant = new();

    /// <summary>How many tenants are currently degraded — the invariant that healthy never accumulates.</summary>
    public int DegradedCount => _byTenant.Count;

    /// <inheritdoc />
    public DegradationProfile Get(TenantId tenant) =>
        _byTenant.TryGetValue(tenant, out var entry) ? entry.Profile : DegradationProfile.Healthy;

    /// <inheritdoc />
    public void Set(TenantId tenant, DegradationProfile profile)
    {
        if (profile.IsHealthy)
        {
            Clear(tenant);
            return;
        }

        _byTenant[tenant] = new Entry(profile);
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant) => _byTenant.TryRemove(tenant, out _);

    /// <inheritdoc />
    public DegradationDecision Next(TenantId tenant)
    {
        if (!_byTenant.TryGetValue(tenant, out var entry))
        {
            return DegradationDecision.None;
        }

        return DegradationPlan.For(entry.Profile, Interlocked.Increment(ref entry.Served) - 1);
    }
}
