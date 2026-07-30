using System.Collections.Concurrent;
using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Bounded in-memory audit log (#247): each tenant keeps at most <paramref name="limit"/> entries,
/// oldest evicted first — the same shape as the request journal and the message inbox, so an operator
/// only has one retention model to reason about. Thread-safe; appends never throw, because auditing
/// must not be able to fail the operation it is describing.
/// </summary>
public sealed class InMemoryAuditLog(int limit = InMemoryAuditLog.DefaultLimit) : IAuditLog
{
    /// <summary>The default per-tenant bound.</summary>
    public const int DefaultLimit = 1000;

    private readonly ConcurrentDictionary<TenantId, List<AuditEntry>> _byTenant = new();
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Append(AuditEntry entry)
    {
        lock (_gate)
        {
            var entries = _byTenant.GetOrAdd(entry.Tenant, static _ => []);
            entries.Add(entry);
            while (limit > 0 && entries.Count > limit)
            {
                entries.RemoveAt(0);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEntry> Read(TenantId tenant, int limit)
    {
        lock (_gate)
        {
            if (!_byTenant.TryGetValue(tenant, out var entries))
            {
                return [];
            }

            // Newest first: an operator reading an audit trail is almost always asking "what just
            // happened", not "what happened first".
            return [.. entries.AsEnumerable().Reverse().Take(Math.Max(1, limit))];
        }
    }
}
