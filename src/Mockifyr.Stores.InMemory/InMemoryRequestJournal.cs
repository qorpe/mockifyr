using System.Collections.Concurrent;
using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Tenant-scoped in-memory request journal. Records serve events and answers simple queries;
/// verification and near-miss diagnostics (G6) build on this. Bounded (#220): each tenant keeps at
/// most <paramref name="limit"/> events — the oldest is evicted first, matching the reference
/// engine's <c>--max-request-journal-entries</c> semantics — so a long-running host neither grows
/// without bound nor retains old credentials indefinitely. An id index backs O(1) detail lookups.
/// </summary>
public sealed class InMemoryRequestJournal(int? limit = InMemoryRequestJournal.DefaultLimit) : IRequestJournal
{
    /// <summary>The default per-tenant bound — the same default as the message inbox.</summary>
    public const int DefaultLimit = 1000;

    private readonly ConcurrentDictionary<TenantId, List<ServeEvent>> _byTenant = new();
    private readonly Dictionary<Guid, ServeEvent> _byId = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Record(ServeEvent serveEvent)
    {
        lock (_gate)
        {
            var events = _byTenant.GetOrAdd(serveEvent.TenantId, static _ => []);
            events.Add(serveEvent);
            _byId[serveEvent.Id] = serveEvent;

            if (limit is { } cap and > 0)
            {
                while (events.Count > cap)
                {
                    _byId.Remove(events[0].Id);
                    events.RemoveAt(0);
                }
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ServeEvent> Query(TenantId tenant, ServeEventQuery query)
    {
        lock (_gate)
        {
            // Id lookup: the index answers directly — but the tenant in the query still gates the
            // result, so one tenant can never address another tenant's event by id.
            if (query.Id is { } id)
            {
                return _byId.TryGetValue(id, out var byId) && byId.TenantId == tenant ? [byId] : [];
            }

            if (!_byTenant.TryGetValue(tenant, out var events))
            {
                return [];
            }

            IEnumerable<ServeEvent> result = events;

            if (query.UnmatchedOnly)
            {
                result = result.Where(e => e.MatchedStub is null);
            }

            if (query.MatchingStubId is { } stubId)
            {
                result = result.Where(e => e.MatchedStub?.Id == stubId);
            }

            if (query.Limit is { } take)
            {
                result = result.Take(take);
            }

            return [.. result];
        }
    }
}

/// <summary>
/// A journal that records nothing (<c>--journal-disabled</c>, #220): for load tests where the
/// journal is pure overhead. Verify/near-miss queries simply see an empty journal.
/// </summary>
public sealed class NullRequestJournal : IRequestJournal
{
    /// <inheritdoc />
    public void Record(ServeEvent serveEvent)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<ServeEvent> Query(TenantId tenant, ServeEventQuery query) => [];
}
