using System.Collections.Concurrent;
using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Tenant-scoped clock overrides held in memory (#290), serving both the admin store and the
/// serve-path resolver so a change takes effect on the very next request rather than after a reload.
/// </summary>
/// <remarks>
/// <para>
/// The real clock comes from an injected <see cref="TimeProvider"/>, so a test can assert exact
/// instants instead of "about now" — the same seam the resource store and the rate limiter use.
/// </para>
/// <para>
/// Only tenants that have actually set an override occupy a slot: an untouched host holds an empty
/// dictionary and answers every lookup from a readonly static.
/// </para>
/// </remarks>
public sealed class InMemoryClockStore(TimeProvider? clock = null) : IClockStore, IClockResolver
{
    private readonly ConcurrentDictionary<TenantId, ClockOverride> _byTenant = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// How many tenants currently hold an override. Exists so the no-accumulation invariant is
    /// assertable rather than merely intended: a client that PUTs its whole configuration on every run
    /// sends real time for tenants that want none, and storing those would grow a dictionary keyed by
    /// tenant that nothing ever prunes.
    /// </summary>
    public int OverrideCount => _byTenant.Count;

    /// <inheritdoc />
    public ClockOverride Get(TenantId tenant) =>
        _byTenant.TryGetValue(tenant, out var over) ? over : ClockOverride.RealTime;

    /// <inheritdoc />
    public void Set(TenantId tenant, ClockOverride over)
    {
        // Storing a real-time override would leave a tenant occupying a slot to say "nothing special
        // here", so setting real time is the same thing as clearing.
        if (over.IsRealTime)
        {
            Clear(tenant);
            return;
        }

        _byTenant[tenant] = over;
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant) => _byTenant.TryRemove(tenant, out _);

    /// <inheritdoc />
    public DateTimeOffset UtcNow(TenantId tenant) =>
        _byTenant.TryGetValue(tenant, out var over)
            ? over.Apply(_clock.GetUtcNow())
            : _clock.GetUtcNow();
}
