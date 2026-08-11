using System.Text.Json;

namespace Mockifyr.Core;

/// <summary>
/// What happens to a document's children when it is deleted (ADR 0015). The default is
/// <see cref="Restrict"/> because the APIs a sandbox stands in for mostly behave that way — deleting
/// a Stripe customer does not delete their charges — so cascading by default would give an imported
/// spec destructive behaviour the modelled API does not have.
/// </summary>
public enum RelationDeleteRule
{
    /// <summary>Refuse the delete while children exist (409). The default.</summary>
    Restrict = 0,

    /// <summary>Delete the children too, depth-first, terminating through a visited set.</summary>
    Cascade = 1,

    /// <summary>Delete the parent and leave the children with a key that no longer resolves.</summary>
    Orphan = 2,
}

/// <summary>
/// A pointer from a document to the document that owns it (ADR 0015). This is the storage half of a
/// relation: it carries the parent id when the modelled contract does <em>not</em> declare a field
/// for it, so the document body still round-trips byte-for-byte as ADR 0011 promised. When the
/// contract does declare the field, the key lives in the body instead and this stays null.
/// </summary>
public sealed record ResourceLink(string Collection, string Id);

/// <summary>
/// One declared relation: documents of the owning collection belong to <paramref name="Collection"/>,
/// keyed by <paramref name="Via"/> (ADR 0015). Enforced only when the key is present, so mutually
/// referencing collections stay creatable.
/// </summary>
public sealed record ResourceRelation(
    string Collection,
    string Via,
    RelationDeleteRule OnDelete = RelationDeleteRule.Restrict);

/// <summary>
/// A collection's relations, declared once rather than restated by every stub that touches it
/// (ADR 0015) — four stubs each repeating the relation is four chances to get three of them right.
/// </summary>
public sealed record ResourceSchema(string Collection, IReadOnlyList<ResourceRelation> BelongsTo);

/// <summary>
/// Tenant-scoped store of collection relation schemas (ADR 0015). Every entry point takes the
/// <see cref="TenantId"/> — there is no tenant-less overload (CLAUDE.md §2.6).
/// </summary>
public interface IResourceSchemaStore
{
    /// <summary>The tenant's declared schemas, collection-name ordered; empty when none.</summary>
    IReadOnlyList<ResourceSchema> List(TenantId tenant);

    /// <summary>One collection's schema, or null when the collection declares no relations.</summary>
    ResourceSchema? Get(TenantId tenant, string collection);

    /// <summary>Declares or replaces a collection's schema.</summary>
    void Put(TenantId tenant, ResourceSchema schema);

    /// <summary>Removes a collection's schema; false when it declared none.</summary>
    bool Delete(TenantId tenant, string collection);

    /// <summary>Every tenant that currently declares at least one schema.</summary>
    IReadOnlyCollection<TenantId> GetTenants();

    /// <summary>Clears every schema of the tenant.</summary>
    void ResetAll(TenantId tenant);
}

/// <summary>
/// The pure relational decisions (ADR 0015): where a document's parent key lives, whether a
/// reference resolves, and what a delete does to the documents below it. Expressed against the Core
/// store contracts so there is exactly one answer to "who owns this document" — the storage choice
/// (body field or metadata pointer) never reaches a caller.
/// </summary>
public static class ResourceRelations
{
    /// <summary>How deep a cascade will walk before refusing to continue.</summary>
    /// <remarks>
    /// Cycles in the relation graph are legal — <c>employees.managerId → employees</c> is a real
    /// model — and the visited set is what makes a cascade over one terminate. This bound is the
    /// second belt: a pathological schema cannot turn one delete into unbounded work.
    /// </remarks>
    public const int MaxCascadeDepth = 32;

    /// <summary>
    /// Reads a top-level key out of a document body. Numbers are read as their raw text so
    /// <c>{"customerId": 1}</c> and <c>{"customerId": "1"}</c> name the same parent — a distinction
    /// that is invisible to whoever wrote the spec and would otherwise be a silent miss.
    /// </summary>
    public static string? ReadKey(string body, string field)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not JsonValueKind.Object
                || !document.RootElement.TryGetProperty(field, out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The id of the document's parent in <paramref name="relation"/>'s collection, or null when the
    /// document names none. The body wins over the metadata pointer: when the contract declares the
    /// field, that field is the truth a client can see and edit.
    /// </summary>
    public static string? ParentIdOf(ResourceDocument document, ResourceRelation relation)
    {
        if (ReadKey(document.Body, relation.Via) is { Length: > 0 } fromBody)
        {
            return fromBody;
        }

        return document.Parent is { } parent
            && string.Equals(parent.Collection, relation.Collection, StringComparison.Ordinal)
                ? parent.Id
                : null;
    }

    /// <summary>
    /// Whether the document belongs to <paramref name="parentId"/> in <paramref name="parentCollection"/>.
    /// Used to scope a list and to turn "the right id under the wrong parent" into a miss.
    /// </summary>
    public static bool BelongsTo(
        ResourceDocument document,
        ResourceSchema? schema,
        string parentCollection,
        string parentId)
    {
        if (schema is null)
        {
            return false;
        }

        foreach (var relation in schema.BelongsTo)
        {
            if (!string.Equals(relation.Collection, parentCollection, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(ParentIdOf(document, relation), parentId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The documents in <paramref name="collection"/> that belong to <paramref name="parentId"/> —
    /// the scoped answer that replaces "the whole collection" for a nested route.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="IResourceStore.Find"/>: that reads a body field, and a document
    /// whose contract declares no such field carries its parent in the metadata pointer instead. A
    /// scoped list built on <c>Find</c> would silently return nothing for exactly the collections
    /// this ADR added the pointer for. <see cref="ParentIdOf"/> is the one accessor that answers for
    /// both, so scoping goes through it.
    /// </remarks>
    public static IReadOnlyList<ResourceDocument> ChildrenOf(
        TenantId tenant,
        string collection,
        ResourceSchema? schema,
        string parentCollection,
        string parentId,
        IResourceStore store)
    {
        if (schema is null)
        {
            return store.List(tenant, collection);
        }

        List<ResourceDocument>? children = null;
        foreach (var document in store.List(tenant, collection))
        {
            if (BelongsTo(document, schema, parentCollection, parentId))
            {
                (children ??= []).Add(document);
            }
        }

        return children is null ? [] : children;
    }

    /// <summary>
    /// The declared references a body does <em>not</em> satisfy: every relation whose key the body
    /// carries but whose target does not exist in this tenant. An empty result means the document may
    /// be stored. Absent keys are not checked — enforcement is presence-triggered (ADR 0015).
    /// </summary>
    public static IReadOnlyList<ResourceRelation> UnresolvedReferences(
        string body,
        ResourceSchema? schema,
        TenantId tenant,
        IResourceStore store)
    {
        if (schema is null || schema.BelongsTo.Count == 0)
        {
            return [];
        }

        List<ResourceRelation>? unresolved = null;
        foreach (var relation in schema.BelongsTo)
        {
            if (ReadKey(body, relation.Via) is not { Length: > 0 } parentId)
            {
                continue;
            }

            // Tenant-scoped by construction: a parent that exists for someone else is a miss here,
            // which is the one place a relation could have become a cross-tenant read.
            if (store.Get(tenant, relation.Collection, parentId) is null)
            {
                (unresolved ??= []).Add(relation);
            }
        }

        return unresolved is null ? [] : unresolved;
    }

    /// <summary>
    /// What deleting one document implies (ADR 0015): the documents to remove with it, or the
    /// relations that refuse the delete outright. <see cref="CascadePlan.Restricted"/> being
    /// non-empty means the caller answers 409 and removes nothing.
    /// </summary>
    public static CascadePlan PlanDelete(
        TenantId tenant,
        string collection,
        string id,
        IResourceSchemaStore schemas,
        IResourceStore store)
    {
        var doomed = new List<ResourceLink>();
        var restricted = new Dictionary<(string Collection, string Via), int>();
        var visited = new HashSet<(string Collection, string Id)>();

        Walk(collection, id, depth: 0);
        return new CascadePlan(
            doomed,
            [.. restricted
                .Select(pair => new RestrictedRelation(pair.Key.Collection, pair.Key.Via, pair.Value))
                .OrderBy(r => r.Collection, StringComparer.Ordinal)
                .ThenBy(r => r.Via, StringComparer.Ordinal)]);

        void Walk(string parentCollection, string parentId, int depth)
        {
            if (depth > MaxCascadeDepth || !visited.Add((parentCollection, parentId)))
            {
                return;
            }

            if (depth > 0)
            {
                doomed.Add(new ResourceLink(parentCollection, parentId));
            }

            foreach (var child in ChildLinksOf(tenant, parentCollection, parentId, schemas, store))
            {
                switch (child.Rule)
                {
                    case RelationDeleteRule.Restrict:
                        var key = (child.Document.Collection, child.Via);
                        restricted[key] = restricted.TryGetValue(key, out var seen) ? seen + 1 : 1;
                        break;

                    case RelationDeleteRule.Cascade:
                        Walk(child.Document.Collection, child.Document.Id, depth + 1);
                        break;

                    case RelationDeleteRule.Orphan:
                    default:
                        break;
                }
            }
        }
    }

    /// <summary>
    /// The documents that name <paramref name="parentId"/> as their parent, across every collection
    /// whose schema declares a relation to <paramref name="parentCollection"/>.
    /// </summary>
    private static IEnumerable<(ResourceDocument Document, string Via, RelationDeleteRule Rule)> ChildLinksOf(
        TenantId tenant,
        string parentCollection,
        string parentId,
        IResourceSchemaStore schemas,
        IResourceStore store)
    {
        foreach (var schema in schemas.List(tenant))
        {
            foreach (var relation in schema.BelongsTo)
            {
                if (!string.Equals(relation.Collection, parentCollection, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var document in store.List(tenant, schema.Collection))
                {
                    if (string.Equals(ParentIdOf(document, relation), parentId, StringComparison.Ordinal))
                    {
                        yield return (document, relation.Via, relation.OnDelete);
                    }
                }
            }
        }
    }
}

/// <summary>A relation that refuses a delete, named so the 409 can say which one and how many.</summary>
public sealed record RestrictedRelation(string Collection, string Via, int Count);

/// <summary>
/// The outcome of planning a delete: the documents that go with it, or the relations that refuse it.
/// A plan with any <see cref="Restricted"/> entry deletes nothing at all — a partial cascade that
/// stopped halfway would leave a sandbox in a state no real API could produce.
/// </summary>
public sealed record CascadePlan(
    IReadOnlyList<ResourceLink> Doomed,
    IReadOnlyList<RestrictedRelation> Restricted)
{
    /// <summary>Whether the delete may proceed.</summary>
    public bool IsAllowed => Restricted.Count == 0;
}
