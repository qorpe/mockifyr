namespace Mockifyr.Core;

/// <summary>
/// One JSON document in a tenant's sandbox collection (G19a, ADR 0011). <see cref="Body"/> is
/// opaque JSON text — Core never parses it; the admin facade validates well-formedness at the
/// edge and the document round-trips byte-for-byte.
/// </summary>
public sealed record ResourceDocument(
    string Id,
    string Collection,
    string Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version);

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
public sealed record ResourceOptions(int MaxBodyBytes = ResourceOptions.DefaultMaxBodyBytes)
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
    int MissStatus = 404);

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

    /// <summary>One document, or null when the id (or collection) is unknown.</summary>
    ResourceDocument? Get(TenantId tenant, string collection, string id);

    /// <summary>
    /// Creates or replaces a document. A create stamps <c>CreatedAt</c> and version 1; a replace
    /// keeps <c>CreatedAt</c> and the insertion position, advances <c>UpdatedAt</c> and the version.
    /// </summary>
    ResourceDocument Put(TenantId tenant, string collection, string id, string body);

    /// <summary>Removes one document; false when it did not exist.</summary>
    bool Delete(TenantId tenant, string collection, string id);

    /// <summary>Clears one collection.</summary>
    void Reset(TenantId tenant, string collection);

    /// <summary>Clears every collection of the tenant.</summary>
    void ResetAll(TenantId tenant);
}
