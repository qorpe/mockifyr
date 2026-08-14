using System.Collections.Concurrent;
using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Tenant-scoped in-memory environment store (G17). Doubles as the serve-path
/// <see cref="IEnvironmentResolver"/>, so resolution reads the same live state the admin API writes —
/// changing a key's active value takes effect on the next request with no stub re-save, which is the
/// whole point of issue #165.
/// <para>
/// Keys are held per tenant in separate dictionaries. There is no shared/global bucket and no
/// fallback to the default tenant: a tenant that has defined nothing resolves nothing (issue #166).
/// </para>
/// </summary>
public sealed class InMemoryEnvironmentStore(HostEnvironment? shared = null) : IEnvironmentStore, IEnvironmentResolver
{
    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, EnvironmentKey>> _byTenant = new();

    /// <summary>Values every tenant inherits unless it defines the key itself (#352).</summary>
    private readonly HostEnvironment _shared = shared ?? HostEnvironment.Empty;

    /// <inheritdoc />
    public IReadOnlyList<EnvironmentKey> GetKeys(TenantId tenant) =>
        _byTenant.TryGetValue(tenant, out var keys)
            ? [.. keys.Values.OrderBy(k => k.Key, StringComparer.Ordinal)]
            : [];

    /// <inheritdoc />
    public IReadOnlyCollection<TenantId> GetTenants() =>
        [.. _byTenant.Where(entry => !entry.Value.IsEmpty).Select(entry => entry.Key)];

    /// <inheritdoc />
    public void Put(TenantId tenant, EnvironmentKey key) =>
        _byTenant.GetOrAdd(tenant, static _ => new ConcurrentDictionary<string, EnvironmentKey>(StringComparer.Ordinal))[key.Key] = key;

    /// <inheritdoc />
    public bool Remove(TenantId tenant, string key) =>
        _byTenant.TryGetValue(tenant, out var keys) && keys.TryRemove(key, out _);

    /// <inheritdoc />
    public void Clear(TenantId tenant) => _byTenant.TryRemove(tenant, out _);

    /// <inheritdoc />
    public bool HasKeys(TenantId tenant) =>
        (_byTenant.TryGetValue(tenant, out var keys) && !keys.IsEmpty) || _shared.Keys.Count > 0;

    /// <summary>
    /// The key in force for this tenant — its own, else the shared one (#352), else null.
    /// </summary>
    /// <remarks>
    /// The tenant's own always wins. A shared value that could not be overridden would be a constraint
    /// rather than a convenience, and the first tenant that needed a different base URL would have to
    /// stop using the mechanism entirely.
    /// </remarks>
    public EnvironmentKey? Effective(TenantId tenant, string key) =>
        _byTenant.TryGetValue(tenant, out var keys) && keys.TryGetValue(key, out var own)
            ? own
            : _shared.Get(key);

    /// <inheritdoc />
    public bool TryResolve(TenantId tenant, string key, out string value)
    {
        // Composed (#352): a value may reference another value, so resolution is not a single lookup.
        // Cycles are refused at write time; the depth bound here is the backstop, not the mechanism.
        var resolved = EnvironmentComposition.Resolve(key, name => Effective(tenant, name)?.Resolve());
        value = resolved ?? string.Empty;
        return resolved is not null;
    }

    /// <summary>Whether this key, or anything it references, resolves to a secret (#348 + #352).</summary>
    public bool ResolvesToSecret(TenantId tenant, string key) =>
        EnvironmentComposition.ResolvesToSecret(key, name => Effective(tenant, name));
}
