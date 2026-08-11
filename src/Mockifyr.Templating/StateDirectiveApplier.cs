using System.Text;
using Mockifyr.Core;

namespace Mockifyr.Templating;

/// <summary>The outcome of applying a state directive: either a short-circuit status or a template model.</summary>
public sealed record StateOutcome(int? ShortCircuitStatus, IReadOnlyDictionary<string, object?>? Model)
{
    /// <summary>A refusal or miss: the response is this status with an empty body, nothing else renders.</summary>
    public static StateOutcome ShortCircuit(int status) => new(status, null);

    /// <summary>Success: the operation result renders as <c>{{state.*}}</c>.</summary>
    public static StateOutcome Success(IReadOnlyDictionary<string, object?> model) => new(null, model);
}

/// <summary>
/// Applies the <c>state</c> directive (G19b, ADR 0011): the pure decision logic between the
/// rendered inputs and the resource store. The renderer supplies the already-rendered id/document
/// (templates are its business); this class owns the semantics — operation dispatch, create-id
/// generation, request-body fallback, the serve-time guards (size cap → 413, non-JSON → 422,
/// unknown operation/collection → 422), and the configurable miss short-circuit.
/// </summary>
public static class StateDirectiveApplier
{
    /// <summary>
    /// Runs the directive. <paramref name="renderedId"/>/<paramref name="renderedDocument"/> are the
    /// directive's templates after rendering (null when the directive omitted them);
    /// <paramref name="requestBody"/> is the raw request body, the default document for
    /// create/update.
    /// </summary>
    public static StateOutcome Apply(
        StateDirective directive,
        TenantId tenant,
        string? renderedId,
        string? renderedDocument,
        byte[] requestBody,
        IResourceStore store,
        IResourceIdGenerator ids,
        ResourceOptions options,
        StateParent? parent = null,
        IResourceSchemaStore? schemas = null,
        ResourceQuery? query = null)
    {
        if (ReservedEnvironmentKeys.IsWellFormed(directive.Collection) is false || directive.Collection.Length > 64)
        {
            return StateOutcome.ShortCircuit(422);
        }

        var id = string.IsNullOrWhiteSpace(renderedId) ? null : renderedId.Trim();
        var schema = schemas?.Get(tenant, directive.Collection);

        switch (directive.Operation.ToLowerInvariant())
        {
            case "create":
            {
                var document = renderedDocument ?? Encoding.UTF8.GetString(requestBody);
                if (Guard(document, options) is { } refusal)
                {
                    return refusal;
                }

                // A parent named by the ROUTE that does not exist is a 404 on that route — the answer a
                // real API gives to POST /customers/99/orders. A parent named by the BODY that does not
                // exist is a 422: the request reached a real place and its payload is what is wrong.
                // Collapsing the two would misreport one of them (ADR 0015).
                if (parent is { } named && store.Get(tenant, named.Collection, named.Id) is null)
                {
                    return StateOutcome.ShortCircuit(directive.MissStatus);
                }

                if (ResourceRelations.UnresolvedReferences(document, schema, tenant, store).Count > 0)
                {
                    return StateOutcome.ShortCircuit(422);
                }

                var stored = store.Put(
                    tenant,
                    directive.Collection,
                    id ?? ids.NextId(directive.Collection),
                    document,
                    LinkFor(parent, schema, document));

                return StateOutcome.Success(DocumentModel(stored));
            }

            case "read":
            {
                var found = id is null ? null : store.Get(tenant, directive.Collection, id);
                return found is null || !InScope(found, schema, parent)
                    ? StateOutcome.ShortCircuit(directive.MissStatus)
                    : StateOutcome.Success(DocumentModel(found));
            }

            case "update":
            {
                if (id is null || store.Get(tenant, directive.Collection, id) is not { } target
                    || !InScope(target, schema, parent))
                {
                    return StateOutcome.ShortCircuit(directive.MissStatus);
                }

                var document = renderedDocument ?? Encoding.UTF8.GetString(requestBody);
                if (Guard(document, options) is { } refusal)
                {
                    return refusal;
                }

                if (ResourceRelations.UnresolvedReferences(document, schema, tenant, store).Count > 0)
                {
                    return StateOutcome.ShortCircuit(422);
                }

                return StateOutcome.Success(DocumentModel(store.Put(tenant, directive.Collection, id, document)));
            }

            case "delete":
            {
                if (id is null || store.Get(tenant, directive.Collection, id) is not { } doomed
                    || !InScope(doomed, schema, parent))
                {
                    return StateOutcome.ShortCircuit(directive.MissStatus);
                }

                if (schemas is not null)
                {
                    var plan = ResourceRelations.PlanDelete(tenant, directive.Collection, id, schemas, store);
                    if (!plan.IsAllowed)
                    {
                        // Nothing is removed: a cascade that stopped halfway would leave the sandbox in
                        // a state no real API could produce.
                        return StateOutcome.ShortCircuit(409);
                    }

                    foreach (var child in plan.Doomed)
                    {
                        store.Delete(tenant, child.Collection, child.Id);
                    }
                }

                store.Delete(tenant, directive.Collection, id);
                return StateOutcome.Success(new Dictionary<string, object?> { ["id"] = id });
            }

            case "list":
            {
                var documents = parent is { } owner
                    ? ResourceRelations.ChildrenOf(tenant, directive.Collection, schema, owner.Collection, owner.Id, store)
                    : store.List(tenant, directive.Collection);

                // The request's own query, evaluated by the same code the admin listing uses (#353).
                // Two evaluators would let the sandbox and the screen watching it disagree about what a
                // collection contains, which is worse than neither of them filtering.
                var selection = query ?? ResourceQuery.All;
                documents = selection.Apply(documents);

                return StateOutcome.Success(new Dictionary<string, object?>
                {
                    ["count"] = documents.Count,
                    ["list"] = "[" + string.Join(",", documents.Select(d => selection.Project(d.Body))) + "]",
                });
            }

            default:
                return StateOutcome.ShortCircuit(422);
        }
    }

    /// <summary>
    /// Whether a document is reachable through the route that asked for it. A directive with no
    /// parent scopes nothing, which is what keeps a flat collection behaving exactly as it did before
    /// relations existed.
    /// </summary>
    private static bool InScope(ResourceDocument document, ResourceSchema? schema, StateParent? parent) =>
        parent is not { } owner || ResourceRelations.BelongsTo(document, schema, owner.Collection, owner.Id);

    /// <summary>
    /// The metadata pointer to store with a new document, or null when none is needed. None is needed
    /// when the modelled contract declares the key itself: the body already carries it, the body is
    /// what a client can see and edit, and writing it twice would let the two disagree.
    /// </summary>
    private static ResourceLink? LinkFor(StateParent? parent, ResourceSchema? schema, string document)
    {
        if (parent is not { } owner)
        {
            return null;
        }

        if (schema is not null)
        {
            foreach (var relation in schema.BelongsTo)
            {
                if (string.Equals(relation.Collection, owner.Collection, StringComparison.Ordinal)
                    && ResourceRelations.ReadKey(document, relation.Via) is { Length: > 0 })
                {
                    return null;
                }
            }
        }

        return new ResourceLink(owner.Collection, owner.Id);
    }

    private static StateOutcome? Guard(string document, ResourceOptions options)
    {
        if (ResourceGuards.ExceedsCap(document, options.MaxBodyBytes))
        {
            return StateOutcome.ShortCircuit(413);
        }

        if (!ResourceGuards.IsWellFormedJson(document))
        {
            return StateOutcome.ShortCircuit(422);
        }

        return null;
    }

    private static Dictionary<string, object?> DocumentModel(ResourceDocument document) => new()
    {
        ["id"] = document.Id,
        ["body"] = document.Body,
        ["version"] = document.Version,
    };
}
