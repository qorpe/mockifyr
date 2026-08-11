using System.Text.Json;
using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Keeps relation declarations (ADR 0015) as documents in the resource store, under a collection
/// name no caller can reach.
/// </summary>
/// <remarks>
/// <para>
/// The alternative was a persistence surface of its own: a writer and a loader for each of the four
/// backends, mirroring roughly four hundred lines that already exist to do exactly this job. Riding
/// the resource store instead means relations persist, restore, export and reload through the change
/// feed on every backend without a line of per-backend code — and it means a relation can never
/// outlive, or be lost by, the documents it describes.
/// </para>
/// <para>
/// This is not a convenience. Relations held only in memory would vanish on restart while their
/// documents survived, and a scoped list would quietly answer with the whole collection again — the
/// exact defect ADR 0015 exists to remove, reappearing at the one moment nobody is watching.
/// </para>
/// <para>
/// The reserved name deliberately fails <see cref="ReservedEnvironmentKeys.IsWellFormed"/>, which
/// every user-facing path applies to a collection name. A tenant therefore cannot create, seed or
/// address a collection that would collide with it — the isolation is structural, not a convention
/// someone has to remember.
/// </para>
/// </remarks>
public sealed class ResourceBackedSchemaStore(IResourceStore documents) : IResourceSchemaStore
{
    /// <summary>The reserved collection. Unreachable by construction: no valid name starts with '!'.</summary>
    public const string ReservedCollection = ResourceRelations.SchemaCollection;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <inheritdoc />
    public IReadOnlyList<ResourceSchema> List(TenantId tenant) =>
        [.. documents.List(tenant, ReservedCollection)
            .Select(document => Read(document.Id, document.Body))
            .OfType<ResourceSchema>()
            .OrderBy(schema => schema.Collection, StringComparer.Ordinal)];

    /// <inheritdoc />
    public ResourceSchema? Get(TenantId tenant, string collection) =>
        documents.Get(tenant, ReservedCollection, collection) is { } document
            ? Read(document.Id, document.Body)
            : null;

    /// <inheritdoc />
    public void Put(TenantId tenant, ResourceSchema schema) =>
        documents.Put(tenant, ReservedCollection, schema.Collection, JsonSerializer.Serialize(
            schema.BelongsTo.Select(relation => new Stored(relation.Collection, relation.Via, relation.OnDelete)),
            Json));

    /// <inheritdoc />
    public bool Delete(TenantId tenant, string collection) =>
        documents.Delete(tenant, ReservedCollection, collection);

    /// <inheritdoc />
    public IReadOnlyCollection<TenantId> GetTenants() =>
        [.. documents.GetTenants().Where(tenant => documents.List(tenant, ReservedCollection).Count > 0)];

    /// <inheritdoc />
    public void ResetAll(TenantId tenant) => documents.Reset(tenant, ReservedCollection);

    private sealed record Stored(string Collection, string Via, RelationDeleteRule OnDelete);

    /// <summary>
    /// Reads one stored declaration. A row that does not parse is dropped rather than thrown: this
    /// storage is reachable by a restore or a hand-edited file, and one unreadable declaration must not
    /// take the whole sandbox down with it.
    /// </summary>
    private static ResourceSchema? Read(string collection, string body)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Stored>>(body) is { } stored
                ? new ResourceSchema(
                    collection,
                    [.. stored.Select(relation => new ResourceRelation(relation.Collection, relation.Via, relation.OnDelete))])
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
