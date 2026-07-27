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
        ResourceOptions options)
    {
        if (ReservedEnvironmentKeys.IsWellFormed(directive.Collection) is false || directive.Collection.Length > 64)
        {
            return StateOutcome.ShortCircuit(422);
        }

        var id = string.IsNullOrWhiteSpace(renderedId) ? null : renderedId.Trim();

        switch (directive.Operation.ToLowerInvariant())
        {
            case "create":
            {
                var document = renderedDocument ?? Encoding.UTF8.GetString(requestBody);
                if (Guard(document, options) is { } refusal)
                {
                    return refusal;
                }

                var stored = store.Put(tenant, directive.Collection, id ?? ids.NextId(directive.Collection), document);
                return StateOutcome.Success(DocumentModel(stored));
            }

            case "read":
            {
                var found = id is null ? null : store.Get(tenant, directive.Collection, id);
                return found is null
                    ? StateOutcome.ShortCircuit(directive.MissStatus)
                    : StateOutcome.Success(DocumentModel(found));
            }

            case "update":
            {
                if (id is null || store.Get(tenant, directive.Collection, id) is null)
                {
                    return StateOutcome.ShortCircuit(directive.MissStatus);
                }

                var document = renderedDocument ?? Encoding.UTF8.GetString(requestBody);
                if (Guard(document, options) is { } refusal)
                {
                    return refusal;
                }

                return StateOutcome.Success(DocumentModel(store.Put(tenant, directive.Collection, id, document)));
            }

            case "delete":
            {
                return id is not null && store.Delete(tenant, directive.Collection, id)
                    ? StateOutcome.Success(new Dictionary<string, object?> { ["id"] = id })
                    : StateOutcome.ShortCircuit(directive.MissStatus);
            }

            case "list":
            {
                var documents = store.List(tenant, directive.Collection);
                return StateOutcome.Success(new Dictionary<string, object?>
                {
                    ["count"] = documents.Count,
                    ["list"] = "[" + string.Join(",", documents.Select(d => d.Body)) + "]",
                });
            }

            default:
                return StateOutcome.ShortCircuit(422);
        }
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
