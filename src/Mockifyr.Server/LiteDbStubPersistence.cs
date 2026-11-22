using LiteDB;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// A persisted stub row in LiteDB: the document key <c>tenant|id</c>, owning tenant, and the stub's id-stamped
/// mapping JSON. Storing the raw JSON keeps persistence faithful without a domain → JSON serializer.
/// </summary>
internal sealed class StoredStub
{
    public string Id { get; set; } = string.Empty;

    public string Tenant { get; set; } = string.Empty;

    public string Json { get; set; } = string.Empty;
}

/// <summary>
/// LiteDB-backed stub persistence (G16b): the second provider behind the <see cref="IStubPersistence"/>
/// seam, proving the multi-provider design. Each stub is one document (id-stamped JSON, keyed by
/// <c>tenant|id</c> and tagged with its tenant) in a single embedded database file; <see cref="LiteDbMappingsLoader"/>
/// reloads them on startup. The <see cref="LiteDatabase"/> is shared (DI singleton) and thread-safe.
/// </summary>
public sealed class LiteDbStubPersistence : IStubPersistence
{
    internal const string Collection = "stubs";

    private readonly ILiteCollection<StoredStub> _stubs;

    public LiteDbStubPersistence(LiteDatabase database)
    {
        MigrateKeys(LiteDbCollation.EnsureOrdinal(database));
        _stubs = database.GetCollection<StoredStub>(Collection);
    }

    /// <summary>
    /// The document key: the tenant and the stub id, as the resource and environment collections key theirs. The id
    /// is the client's choice, so two tenants may hold the same one; a key of the id alone let one tenant's write take
    /// the other's document and its delete remove it (#466). A Guid has no <c>|</c>, so the key is unambiguous.
    /// </summary>
    internal static string IdFor(TenantId tenant, Guid id) => $"{tenant.Value}|{id}";

    /// <summary>
    /// Re-keys documents written before #466, whose key is the bare stub id, to <see cref="IdFor"/>. Each one already
    /// has a distinct id, so nothing collides; a document already re-keyed is left alone, so this is re-runnable. Both
    /// the persistence and the loader run it, whichever the host builds first.
    /// </summary>
    internal static void MigrateKeys(LiteDatabase database)
    {
        var raw = database.GetCollection(Collection);
        var legacy = raw.Find(Query.All()).Where(document => document["_id"].IsGuid).ToList();
        foreach (var document in legacy)
        {
            var id = document["_id"].AsGuid;
            var tenant = document["Tenant"].IsString ? document["Tenant"].AsString : TenantId.Default.Value;
            var rekeyed = new BsonDocument(document) { ["_id"] = IdFor(new TenantId(tenant), id) };
            raw.Upsert(rekeyed);
            raw.Delete(id);
        }
    }

    /// <inheritdoc />
    public void Save(StubMapping stub, string mappingJson) =>
        _stubs.Upsert(new StoredStub
        {
            Id = IdFor(stub.TenantId, stub.Id),
            Tenant = stub.TenantId.Value,
            Json = PersistableJson.WithId(mappingJson, stub.Id),
        });

    /// <inheritdoc />
    public void Remove(TenantId tenant, Guid id) => _stubs.Delete(IdFor(tenant, id));

    /// <inheritdoc />
    public void Clear(TenantId tenant) => _stubs.DeleteMany(stored => stored.Tenant == tenant.Value);
}

/// <summary>
/// Reloads the stubs a <see cref="LiteDbStubPersistence"/> saved (G16b) — the <see cref="IMappingsLoader"/>
/// counterpart, run at startup. Reads the tenant's documents and parses each stored JSON back into a
/// <see cref="StubMapping"/> (ids are preserved because the JSON was id-stamped when saved).
/// </summary>
public sealed class LiteDbMappingsLoader : IMappingsLoader, IMultiTenantMappingsLoader
{
    private readonly ILiteCollection<StoredStub> _stubs;
    private readonly IMatcherRegistry? _matchers;

    public LiteDbMappingsLoader(LiteDatabase database, IMatcherRegistry? matchers = null)
    {
        LiteDbStubPersistence.MigrateKeys(LiteDbCollation.EnsureOrdinal(database));
        _stubs = database.GetCollection<StoredStub>(LiteDbStubPersistence.Collection);
        _matchers = matchers;
    }

    /// <inheritdoc />
    public IReadOnlyList<StubMapping> Load(TenantId tenant)
    {
        var stubs = new List<StubMapping>();
        foreach (var stored in _stubs.Find(row => row.Tenant == tenant.Value))
        {
            stubs.AddRange(MappingJsonReader.Read(stored.Json, tenant, _matchers));
        }

        return stubs;
    }

    /// <inheritdoc />
    /// <remarks>Every row carries its tenant, so startup reloads them all, each under its own (#466).</remarks>
    public IReadOnlyList<StubMapping> LoadAllTenants()
    {
        var stubs = new List<StubMapping>();
        foreach (var stored in _stubs.FindAll())
        {
            stubs.AddRange(MappingJsonReader.Read(stored.Json, new TenantId(stored.Tenant), _matchers));
        }

        return stubs;
    }
}
