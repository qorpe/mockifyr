namespace Mockifyr.Core;

/// <summary>
/// One JSON document in a tenant's sandbox collection (G19a, ADR 0011). <see cref="Body"/> is
/// opaque JSON text — Core never parses it; the admin facade validates well-formedness at the
/// edge and the document round-trips byte-for-byte.
/// </summary>
/// <remarks>
/// <see cref="Parent"/> (ADR 0015) is the optional half of a relation: it carries the owning
/// document's id when the modelled contract declares no field for it, which is what keeps
/// <see cref="Body"/> byte-identical to what the client sent. When the contract <em>does</em>
/// declare the field, the key lives in the body and this stays null. Being optional is also what
/// makes the change compatible: documents written before ADR 0015 have none and stay valid on
/// every persistence provider.
/// </remarks>
public sealed record ResourceDocument(
    string Id,
    string Collection,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    ResourceLink? Parent = null);

/// <summary>A collection summary for the admin listing: its name and how many documents it holds.</summary>
public sealed record ResourceCollectionInfo(string Name, int Count);

/// <summary>
/// Id-generation seam (ADR 0011): tests inject a deterministic generator; the default is a UUID.
/// The G19b state directive uses this for `create` operations; the admin PUT takes explicit ids.
/// </summary>
public interface IResourceIdGenerator
{
    /// <summary>The next id for a document created in <paramref name="collection"/>.</summary>
    string NextId(string collection);
}

/// <summary>The default id generator: lowercase UUIDs.</summary>
public sealed class GuidResourceIdGenerator : IResourceIdGenerator
{
    /// <inheritdoc />
    public string NextId(string collection) => Guid.NewGuid().ToString("D");
}

/// <summary>
/// Sandbox-resource limits (G19a, ADR 0011 addendum). <see cref="MaxBodyBytes"/> caps one
/// document's UTF-8 size (honest 413 beyond it); the per-collection document capacity lives on
/// the store (ring-buffer eviction, oldest first).
/// </summary>
public sealed record ResourceOptions(
    int MaxBodyBytes = ResourceOptions.DefaultMaxBodyBytes,
    long TenantStorageLimitBytes = TenantStorage.Unlimited)
{
    /// <summary>The default per-document cap: 1 MiB.</summary>
    public const int DefaultMaxBodyBytes = 1024 * 1024;
}

/// <summary>
/// The <c>state</c> response directive (G19b, ADR 0011): a sibling of delay/fault that turns a
/// matched stub into a sandbox CRUD operation. Pure data — the engine never interprets it; the
/// templating renderer applies it at serve time. <see cref="Id"/> and <see cref="Document"/> are
/// template expressions rendered against the request (path segments, body, query); an absent
/// create id comes from <see cref="IResourceIdGenerator"/>, an absent document is the request body.
/// </summary>
public sealed record StateDirective(
    string Operation,
    string Collection,
    string? Id = null,
    string? Document = null,
    int MissStatus = 404,
    StateParent? Parent = null);

/// <summary>
/// The owning document a nested route names (ADR 0015) — the <c>/customers/{customerId}</c> half of
/// <c>/customers/{customerId}/orders</c>. <see cref="Id"/> is a template expression rendered against
/// the request, like <see cref="StateDirective.Id"/>; the collection is literal.
/// </summary>
public sealed record StateParent(string Collection, string Id);

/// <summary>
/// The body checks shared by the admin PUT path and the serve-time state directive — one
/// definition, two edges (ADR 0011 addendum). BCL-only so Core stays dependency-free.
/// </summary>
public static class ResourceGuards
{
    /// <summary>Whether the body parses as JSON — resources are JSON documents by contract.</summary>
    public static bool IsWellFormedJson(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether the body's UTF-8 size exceeds the configured cap.</summary>
    public static bool ExceedsCap(string body, int maxBytes) =>
        System.Text.Encoding.UTF8.GetByteCount(body) > maxBytes;
}

/// <summary>
/// Tenant- and collection-scoped sandbox document store (G19a, ADR 0011). Every entry point takes
/// the <see cref="TenantId"/> — there is no tenant-less overload (CLAUDE.md §2.6). Bounded per
/// collection: beyond capacity the oldest document is evicted first. Updates are last-write-wins
/// (ADR 0011 addendum); conditional update is a tracked deferred edge.
/// </summary>
public interface IResourceStore
{
    /// <summary>The tenant's collections with document counts, name-ordered.</summary>
    IReadOnlyList<ResourceCollectionInfo> GetCollections(TenantId tenant);

    /// <summary>The collection's documents in insertion order; empty for an unknown collection.</summary>
    IReadOnlyList<ResourceDocument> List(TenantId tenant, string collection);

    /// <summary>
    /// Every tenant that currently owns at least one document. Used by change-feed reload (#279),
    /// including pruning a tenant whose last document was deleted on another instance.
    /// </summary>
    IReadOnlyCollection<TenantId> GetTenants();

    /// <summary>One document, or null when the id (or collection) is unknown.</summary>
    ResourceDocument? Get(TenantId tenant, string collection, string id);

    /// <summary>
    /// The collection's documents whose top-level <paramref name="field"/> equals
    /// <paramref name="value"/>, in insertion order (ADR 0015).
    /// </summary>
    /// <remarks>
    /// This is the one primitive the store was missing: reading by id or reading everything were the
    /// only two options, so scoping a relation, filtering a collection and resolving a session by its
    /// token were all unanswerable. A default implementation over <see cref="List"/> keeps every
    /// existing implementer compiling and is the right shape anyway — the in-memory store is the hot
    /// path source of truth (ADR 0006) and collections are bounded, so this is an in-process scan and
    /// never a query pushed to a backend.
    /// </remarks>
    IReadOnlyList<ResourceDocument> Find(TenantId tenant, string collection, string field, string value) =>
        [.. List(tenant, collection)
            .Where(d => string.Equals(ResourceRelations.ReadKey(d.Body, field), value, StringComparison.Ordinal))];

    /// <summary>
    /// How many bytes of document bodies this tenant holds (#357).
    /// </summary>
    /// <remarks>
    /// A default over <see cref="List"/> so every existing implementer keeps compiling, and the
    /// in-memory store — the hot-path source of truth (ADR 0006) — overrides it with a counter it
    /// maintains as documents come and go. A scan on every write would make the ceiling cost more
    /// than the storage it protects.
    /// </remarks>
    long UsedBytes(TenantId tenant) =>
        GetCollections(tenant).Sum(collection => List(tenant, collection.Name).Sum(d => (long)d.Body.Length));

    /// <summary>
    /// Creates or replaces a document. A create stamps <c>CreatedAt</c> and version 1; a replace
    /// keeps <c>CreatedAt</c> and the insertion position, advances <c>UpdatedAt</c> and the version.
    /// <paramref name="parent"/> (ADR 0015) records the owning document when the modelled contract
    /// declares no field for it; a replace that passes none keeps whatever the document already had,
    /// so updating a child's body never quietly reparents it.
    /// </summary>
    ResourceDocument Put(TenantId tenant, string collection, string id, string body, ResourceLink? parent = null);

    /// <summary>
    /// Writes a document exactly as it was persisted — id, collection, timestamps <em>and version</em>
    /// preserved — rather than as a new write.
    /// </summary>
    /// <remarks>
    /// This is what change-feed reload (#279) uses. Replaying another instance's document through
    /// <see cref="Put"/> would advance its version and stamp a local <c>UpdatedAt</c>, so the same
    /// document would report different versions on two replicas of one backend — a difference a client
    /// can observe, and the reason this is a separate entry point rather than a flag on <c>Put</c>.
    /// The per-collection bound still applies: a restore is a document arriving, like any other.
    /// </remarks>
    void Restore(TenantId tenant, ResourceDocument document);

    /// <summary>Removes one document; false when it did not exist.</summary>
    bool Delete(TenantId tenant, string collection, string id);

    /// <summary>Clears one collection.</summary>
    void Reset(TenantId tenant, string collection);

    /// <summary>Clears every collection of the tenant.</summary>
    void ResetAll(TenantId tenant);
}
