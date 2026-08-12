using Mockifyr.Core;

namespace Mockifyr.Templating;

/// <summary>
/// Loads and unloads a named dataset (#351): renders each document, writes them in dependency order,
/// and takes the whole thing back out again on failure or on request.
/// </summary>
/// <remarks>
/// Lives beside the templating engine because a dataset document is a template — <c>{{random
/// 'Name.fullName'}}</c> is the point of having one — and Core neither renders nor does I/O.
/// </remarks>
public sealed class DatasetLoader(
    IResourceStore documents,
    IResourceSchemaStore schemas,
    IResourceIdGenerator ids,
    ResourceOptions? options = null) : IDatasetLoader
{
    private readonly CompiledTemplateCache _templates = CompiledTemplateCache.Create();
    private readonly ResourceOptions _options = options ?? new ResourceOptions();

    /// <summary>
    /// Loads the dataset. On any failure nothing survives: every document written so far is removed,
    /// because a dataset that half-landed leaves the sandbox in a state no scenario describes — and
    /// the person who ran it would have no way to know which half they got.
    /// </summary>
    public DatasetLoadOutcome Load(TenantId tenant, DatasetDefinition dataset)
    {
        if (Datasets.Invalid(dataset) is { } invalid)
        {
            return new DatasetLoadOutcome([], invalid);
        }

        var created = new List<ResourceLink>();

        // One scope for the whole load, so a seeded dataset draws from a single sequence — and so the
        // seeding never escapes into whatever else this host is serving.
        using var seed = FakerSeed.Use(dataset.Seed);

        foreach (var item in Datasets.InDependencyOrder(dataset.Items, schemas.List(tenant)))
        {
            var schema = schemas.Get(tenant, item.Collection);

            for (var i = 0; i < item.Count; i++)
            {
                // Rendered per document, not once for the item: the whole reason to ask for two hundred
                // is that they differ.
                var model = new Dictionary<string, object?> { ["index"] = i, ["dataset"] = dataset.Name };

                string body;
                string id;
                try
                {
                    body = _templates.Render(item.Document, model);
                    id = item.Id is { Length: > 0 } template
                        ? _templates.Render(template, model)
                        : ids.NextId(item.Collection);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    return Undo(tenant, created,
                        $"The template for '{item.Collection}' did not render: {exception.Message}");
                }

                if (!ResourceGuards.IsWellFormedJson(body))
                {
                    // The template was JSON before rendering — this is a helper that produced something
                    // unquoted, which is a mistake worth naming rather than storing.
                    return Undo(tenant, created, $"A document for '{item.Collection}' did not render as JSON.");
                }

                if (ResourceGuards.ExceedsCap(body, _options.MaxBodyBytes))
                {
                    return Undo(tenant, created, $"A document for '{item.Collection}' exceeds the body limit.");
                }

                if (ResourceRelations.UnresolvedReferences(body, schema, tenant, documents) is { Count: > 0 } unresolved)
                {
                    return Undo(tenant, created,
                        $"A document for '{item.Collection}' references "
                        + $"{string.Join(", ", unresolved.Select(r => $"{r.Collection}.{r.Via}"))}, which does not exist.");
                }

                documents.Put(tenant, item.Collection, id, body);
                created.Add(new ResourceLink(item.Collection, id));
            }
        }

        return new DatasetLoadOutcome(created, null);
    }

    /// <summary>
    /// Removes exactly what a load created, children first.
    /// </summary>
    /// <remarks>
    /// Reverse order matters: a relation defaulting to <c>restrict</c> (ADR 0015) refuses to delete a
    /// parent while its children exist, so unloading in load order would refuse on the first document
    /// and leave the rest behind. Documents somebody else added are untouched — resetting by "clear the
    /// collections this dataset uses" would take a colleague's work with it.
    /// </remarks>
    public int Unload(TenantId tenant, IReadOnlyList<ResourceLink> created)
    {
        var removed = 0;
        for (var i = created.Count - 1; i >= 0; i--)
        {
            if (documents.Delete(tenant, created[i].Collection, created[i].Id))
            {
                removed++;
            }
        }

        return removed;
    }

    private DatasetLoadOutcome Undo(TenantId tenant, IReadOnlyList<ResourceLink> created, string refusal)
    {
        Unload(tenant, created);
        return new DatasetLoadOutcome([], refusal);
    }
}
