using System.Linq;
using Microsoft.Extensions.Hosting;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;
using StackExchange.Redis;

namespace Mockifyr.Server;

/// <summary>
/// Redis-backed stub persistence (G16d): the fourth provider behind the <see cref="IStubPersistence"/>
/// seam — a key-value backend. Each tenant's stubs live in one Redis hash (<c>mockifyr:stubs:{tenant}</c>)
/// keyed by stub id, the value being the id-stamped mapping JSON (shared <see cref="PersistableJson"/>)
/// so ids round-trip identically to the other backends. The <see cref="IConnectionMultiplexer"/> is
/// shared (DI singleton) and thread-safe. <see cref="RedisMappingsLoader"/> reloads on startup.
/// </summary>
public sealed class RedisStubPersistence(IConnectionMultiplexer redis, ChangeFeedIdentity? writer = null)
    : IStubPersistence
{
    /// <summary>The pub/sub channel a mutation announces on, so other instances can reload (G16e).</summary>
    internal static readonly RedisChannel ChangeChannel = RedisChannel.Literal("mockifyr:changes");

    internal static string HashKey(TenantId tenant) => $"mockifyr:stubs:{tenant.Value}";

    /// <inheritdoc />
    public void Save(StubMapping stub, string mappingJson)
    {
        redis.GetDatabase().HashSet(
            HashKey(stub.TenantId), stub.Id.ToString(), PersistableJson.WithId(mappingJson, stub.Id));
        Announce();
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, Guid id)
    {
        redis.GetDatabase().HashDelete(HashKey(tenant), id.ToString());
        Announce();
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant)
    {
        redis.GetDatabase().KeyDelete(HashKey(tenant));
        Announce();
    }

    // Announce a change so change-feed subscribers (G16e) reload, carrying this host's identity so it
    // can skip its own (#279). A publish with no subscribers is a cheap no-op.
    private void Announce() => ChangeFeedAnnouncement.Redis(redis, writer);
}

/// <summary>
/// The change-feed reloader (G16e): keeps a live host's in-memory store coherent with a shared Redis
/// backend across instances. On any change another instance announces (see
/// <see cref="RedisStubPersistence.ChangeChannel"/>), it reloads what is persisted and reconciles this
/// host's state — upserting what's persisted and pruning what's gone — so a stub, environment key or
/// sandbox document written (or deleted) on one instance is served (or stopped) by the others without a
/// restart. Environments and resources joined stubs on the feed in #279.
/// </summary>
public sealed class RedisChangeFeedReloader(
    IConnectionMultiplexer redis, ChangeFeedTargets targets) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        redis.GetSubscriber().Subscribe(RedisStubPersistence.ChangeChannel, (_, message) => Reload(message));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        redis.GetSubscriber().Unsubscribe(RedisStubPersistence.ChangeChannel);
        return Task.CompletedTask;
    }

    // Reloads are serialized (and self-announcements skipped) by the reconciler, so two announcements
    // arriving together cannot interleave into a mixed view of the backend.
    private void Reload(RedisValue message) => ChangeFeedReconciler.Reload(targets, message);
}

/// <summary>
/// Reloads the stubs a <see cref="RedisStubPersistence"/> saved (G16d) — the <see cref="IMappingsLoader"/>
/// counterpart, run at startup. Reads the tenant's hash and parses each stored JSON back into a
/// <see cref="StubMapping"/> (ids preserved via the id-stamped JSON).
/// </summary>
public sealed class RedisMappingsLoader(IConnectionMultiplexer redis, IMatcherRegistry? matchers = null)
    : IMappingsLoader, IMultiTenantMappingsLoader
{
    private const string HashKeyPrefix = "mockifyr:stubs:";

    /// <inheritdoc />
    public IReadOnlyList<StubMapping> Load(TenantId tenant)
    {
        var stubs = new List<StubMapping>();
        foreach (var entry in redis.GetDatabase().HashGetAll(RedisStubPersistence.HashKey(tenant)))
        {
            stubs.AddRange(MappingJsonReader.Read(entry.Value!, tenant, matchers));
        }

        return stubs;
    }

    /// <inheritdoc />
    public IReadOnlyList<StubMapping> LoadAllTenants()
    {
        var database = redis.GetDatabase();
        var stubs = new List<StubMapping>();

        // Enumerate every tenant hash (mockifyr:stubs:*) via SCAN across the connected endpoints; the
        // tenant is the key suffix. SCAN (not KEYS) so it stays non-blocking on a large keyspace.
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            if (!server.IsConnected || server.IsReplica)
            {
                continue;
            }

            foreach (var key in server.Keys(database.Database, pattern: HashKeyPrefix + "*"))
            {
                var tenant = new TenantId(((string)key!)[HashKeyPrefix.Length..]);
                foreach (var entry in database.HashGetAll(key))
                {
                    stubs.AddRange(MappingJsonReader.Read(entry.Value!, tenant, matchers));
                }
            }
        }

        return stubs;
    }
}
