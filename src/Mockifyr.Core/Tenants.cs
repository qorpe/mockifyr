namespace Mockifyr.Core;

/// <summary>
/// Whether a declared tenant may be served (#357).
/// </summary>
public enum TenantStatus
{
    /// <summary>Serving normally.</summary>
    Active = 0,

    /// <summary>
    /// Declared but refused at the door — the sandbox is still there, and nothing has been deleted.
    /// </summary>
    /// <remarks>
    /// The state that made this worth building: "finished with this partner" and "paused pending
    /// payment" were both spelled *delete everything they own*, which is not a decision anybody wants
    /// to make on a Friday.
    /// </remarks>
    Suspended = 1,
}

/// <summary>
/// A tenant somebody declared, as opposed to one inferred from owning a stub (#357).
/// </summary>
/// <remarks>
/// <para>
/// Declaring is opt-in and additive: a host that never calls the create route keeps listing tenants
/// derived from the stub store exactly as before. That matters because the derived listing is what
/// every existing deployment relies on, and a "migration" to a declared model would be a breaking
/// change dressed as a feature.
/// </para>
/// <para>
/// The record is deliberately thin. It holds what an operator needs to run a partner relationship —
/// a name they recognise, when it started, whether it is live, and how much room it gets — and not a
/// billing plan, a contact, or anything else that belongs in the system that actually owns it.
/// </para>
/// </remarks>
public sealed record TenantRecord(
    TenantId Id,
    string DisplayName,
    DateTimeOffset CreatedAt,
    TenantStatus Status = TenantStatus.Active,
    long? StorageLimitBytes = null,
    bool? Idempotency = null);

/// <summary>
/// Whether a tenant replays retried writes carrying an <c>Idempotency-Key</c> (#358): its own answer
/// when it has declared one, else the host default.
/// </summary>
/// <remarks>
/// Per tenant rather than host-only because a sandbox deliberately testing double-submission has to
/// be able to keep it off while the partner beside it keeps it on — on a shared host those are two
/// different teams with two different intentions.
/// </remarks>
public static class TenantIdempotency
{
    /// <summary>Whether replay is in force for this tenant.</summary>
    public static bool EnabledFor(TenantRecord? tenant, bool hostDefault) =>
        tenant?.Idempotency ?? hostDefault;
}

/// <summary>Host-wide store of declared tenants (#357).</summary>
public interface ITenantStore
{
    /// <summary>Adds or replaces a declaration.</summary>
    void Put(TenantRecord tenant);

    /// <summary>One declaration, or null when this tenant was never declared.</summary>
    TenantRecord? Get(TenantId id);

    /// <summary>Every declaration, oldest first.</summary>
    IReadOnlyList<TenantRecord> GetAll();

    /// <summary>Removes a declaration; false when it was not there.</summary>
    bool Remove(TenantId id);
}

/// <summary>Durability seam for declared tenants (#357), riding the same G16 backends as API keys.</summary>
public interface ITenantPersistence
{
    /// <summary>Persists a declaration.</summary>
    void Save(TenantRecord tenant);

    /// <summary>Removes a declaration.</summary>
    void Remove(TenantId id);

    /// <summary>Loads every declaration at startup.</summary>
    IReadOnlyList<TenantRecord> LoadAll();
}

/// <summary>The no-op default: declarations live only in memory (#357).</summary>
public sealed class NullTenantPersistence : ITenantPersistence
{
    /// <inheritdoc />
    public void Save(TenantRecord tenant)
    {
    }

    /// <inheritdoc />
    public void Remove(TenantId id)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<TenantRecord> LoadAll() => [];
}

/// <summary>In-memory declared-tenant store (#357). Thread-safe.</summary>
public sealed class InMemoryTenantStore : ITenantStore
{
    private readonly Dictionary<TenantId, TenantRecord> _tenants = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Put(TenantRecord tenant)
    {
        lock (_gate)
        {
            _tenants[tenant.Id] = tenant;
        }
    }

    /// <inheritdoc />
    public TenantRecord? Get(TenantId id)
    {
        lock (_gate)
        {
            return _tenants.GetValueOrDefault(id);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TenantRecord> GetAll()
    {
        lock (_gate)
        {
            return [.. _tenants.Values.OrderBy(tenant => tenant.CreatedAt).ThenBy(tenant => tenant.Id.Value, StringComparer.Ordinal)];
        }
    }

    /// <inheritdoc />
    public bool Remove(TenantId id)
    {
        lock (_gate)
        {
            return _tenants.Remove(id);
        }
    }
}

/// <summary>
/// The per-tenant storage ceiling and the decision it produces (#357).
/// </summary>
/// <remarks>
/// <para>
/// Why this exists at all: <c>--resource-max-body</c> caps one document and <c>--resource-limit</c>
/// caps one collection, so a partner seeding a loop across many collections could fill a shared host
/// for everybody. That is precisely the neighbour problem the tenant model exists to prevent, and it
/// was the one bound nobody had.
/// </para>
/// <para>
/// Measured in bytes of document bodies. Not an exact accounting of process memory — indexes, keys and
/// timestamps are not counted — because the number has to mean something an operator can predict from
/// what they seeded, and "the bytes you put in" is that number.
/// </para>
/// </remarks>
public static class TenantStorage
{
    /// <summary>Unlimited, which is what every host meant before this existed.</summary>
    public const long Unlimited = 0;

    /// <summary>
    /// The ceiling in force for a tenant: its own override, else the host default, else unlimited.
    /// </summary>
    public static long LimitFor(TenantRecord? tenant, long hostDefault) =>
        tenant?.StorageLimitBytes is { } own and > 0 ? own : Math.Max(0, hostDefault);

    /// <summary>
    /// Whether writing <paramref name="incomingBytes"/> keeps the tenant inside
    /// <paramref name="limit"/>, given it currently holds <paramref name="usedBytes"/> and is
    /// replacing <paramref name="replacedBytes"/>.
    /// </summary>
    /// <remarks>
    /// The replaced size is subtracted because an update is not a new document: a tenant sitting at
    /// its ceiling must still be able to edit what it already has, or the limit becomes a trap that
    /// only a delete can escape.
    /// </remarks>
    public static bool Fits(long usedBytes, long replacedBytes, long incomingBytes, long limit) =>
        limit <= Unlimited || usedBytes - replacedBytes + incomingBytes <= limit;
}


/// <summary>
/// Answers "may this tenant store this?" in one place, for every path that writes a document (#357).
/// </summary>
/// <remarks>
/// One guard rather than a check per handler: the admin API, the served <c>state</c> directive and a
/// dataset load all write to the same store, and a ceiling enforced on two of the three is not a
/// ceiling — the way in would just be the path nobody instrumented.
/// </remarks>
public sealed class TenantStorageGuard(IResourceStore store, long hostDefaultBytes, ITenantStore? tenants = null)
{
    /// <summary>The host-wide default ceiling in bytes; zero is unlimited.</summary>
    public long HostDefaultBytes { get; } = Math.Max(0, hostDefaultBytes);

    /// <summary>The ceiling in force for this tenant, in bytes; zero is unlimited.</summary>
    public long LimitFor(TenantId tenant) => TenantStorage.LimitFor(tenants?.Get(tenant), HostDefaultBytes);

    /// <summary>What this tenant currently holds, in bytes.</summary>
    public long UsedBy(TenantId tenant) => store.UsedBytes(tenant);

    /// <summary>
    /// Whether writing <paramref name="body"/> at <paramref name="collection"/>/<paramref name="id"/>
    /// stays inside the tenant's ceiling. Replacing an existing document only counts the difference.
    /// </summary>
    public bool Allows(TenantId tenant, string collection, string id, string body, out long used, out long limit)
    {
        limit = LimitFor(tenant);
        used = store.UsedBytes(tenant);
        var replaced = store.Get(tenant, collection, id)?.Body.Length ?? 0;
        return TenantStorage.Fits(used, replaced, body.Length, limit);
    }
}
