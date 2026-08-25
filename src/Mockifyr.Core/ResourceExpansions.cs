using System.Text.Json;

namespace Mockifyr.Core;

/// <summary>One resolved expansion: the name the caller may write, and the relation it names (#378).</summary>
public sealed record ResourceExpansion(string Name, ResourceRelation Relation);

/// <summary>
/// What <c>?_expand=</c> asked for, resolved against a collection's declared relations (#378): either
/// the expansions to embed, or the first name that names none.
/// </summary>
/// <remarks>
/// A refusal carries the offending name <em>and</em> the names that would have worked, because the
/// failure this guards against is a typo, and a caller who mistyped a relation needs to be told what
/// the collection actually declares rather than that they were wrong.
/// </remarks>
public sealed record ExpansionPlan(
    IReadOnlyList<ResourceExpansion> Expansions,
    string? UnknownName,
    IReadOnlyList<string> Available)
{
    /// <summary>A plan that embeds nothing — what a request with no <c>_expand</c> produces.</summary>
    public static ExpansionPlan None { get; } = new([], null, []);

    /// <summary>Whether a requested name matched no declared relation.</summary>
    public bool IsRefused => UnknownName is not null;

    /// <summary>Whether this plan would change a document at all.</summary>
    public bool IsEmpty => Expansions.Count == 0;

    /// <summary>The refusal, in one sentence naming the miss and the alternatives.</summary>
    public string RefusalMessage => UnknownName is null
        ? string.Empty
        : $"Unknown relation '{UnknownName}'. Declared relations: "
            + (Available.Count == 0 ? "(none)" : string.Join(", ", Available)) + ".";
}

/// <summary>
/// Embedding a related document in a read (#378). A relation already knows that an order belongs to a
/// customer; without this the consumer reads the foreign key, makes a second call and stitches the two
/// together — which is exactly the work the relation was declared to describe.
/// </summary>
/// <remarks>
/// <para>
/// Bounded on purpose, and the bounds are ADR 0015's: one level, a declared relation named explicitly,
/// and the parent direction only. <c>?_expand=a.b</c> and "embed every child" are not near-misses of
/// this feature — they are a query planner, and a sandbox that is harder to reason about than the
/// service it stands in for has stopped being a sandbox.
/// </para>
/// <para>
/// The parameter is <c>_expand</c>, not <c>expand</c>, for two reasons that agree: it is the spelling
/// json-server uses — the vocabulary ADR 0015 chose to adopt rather than reinvent — and every
/// unprefixed query parameter is already a field filter (#353), so claiming the bare word would take
/// the field name <c>expand</c> away from every document in every tenant.
/// </para>
/// </remarks>
public static class ResourceExpansions
{
    /// <summary>
    /// The property the embedded documents are written under.
    /// </summary>
    /// <remarks>
    /// An envelope rather than a sibling field per relation, because a top-level <c>customer</c> would
    /// be indistinguishable from a field the modelled contract declares — and would silently overwrite
    /// one if it did. Under <c>_expand</c> the addition is unmistakably the sandbox's, and a document
    /// whose own body carries every name we might choose is still expandable.
    /// </remarks>
    public const string Envelope = "_expand";

    /// <summary>
    /// The name a relation is expanded by: its key field with a trailing id suffix removed, so
    /// <c>customerId</c> and <c>customer_id</c> are both <c>customer</c> — the form json-server answers
    /// to and the form the embedded document reads as.
    /// </summary>
    public static string NameOf(ResourceRelation relation) => NameOf(relation.Via);

    /// <summary>The canonical expansion name for a key field.</summary>
    public static string NameOf(string via)
    {
        if (via.Length > 3 && via.EndsWith("_id", StringComparison.OrdinalIgnoreCase))
        {
            return via[..^3];
        }

        // The separator check is what keeps a field literally called `_id` its own name: stripping the
        // suffix there leaves `_`, which is not something to call a relation.
        return via.Length > 2 && via.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && via[^3] != '_'
            ? via[..^2]
            : via;
    }

    /// <summary>
    /// Resolves the requested names against a collection's schema. A name matches a relation's key
    /// field verbatim or its canonical name; anything else is a refusal rather than a document returned
    /// unexpanded, because a silent no-op is indistinguishable from a typo.
    /// </summary>
    /// <remarks>
    /// Verbatim first, so a collection that declares both <c>customer</c> and <c>customerId</c> as keys
    /// resolves <c>?_expand=customer</c> to the field of that exact name — the reading with no
    /// inference in it. Duplicate requests collapse: asking twice embeds once.
    /// </remarks>
    public static ExpansionPlan Plan(IReadOnlyList<string> requested, ResourceSchema? schema)
    {
        if (requested.Count == 0)
        {
            return ExpansionPlan.None;
        }

        var relations = schema?.BelongsTo ?? [];
        var available = relations.Select(NameOf).Distinct(StringComparer.Ordinal).ToArray();

        var expansions = new List<ResourceExpansion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in requested)
        {
            var relation = relations.FirstOrDefault(r => string.Equals(r.Via, name, StringComparison.Ordinal))
                ?? relations.FirstOrDefault(r => string.Equals(NameOf(r), name, StringComparison.Ordinal));

            if (relation is null)
            {
                return new ExpansionPlan([], name, available);
            }

            if (seen.Add(NameOf(relation)))
            {
                expansions.Add(new ResourceExpansion(NameOf(relation), relation));
            }
        }

        return new ExpansionPlan(expansions, null, available);
    }

    /// <summary>
    /// The document body with its expansions embedded under <see cref="Envelope"/>.
    /// <paramref name="body"/> is what the caller would otherwise receive — already projected, when
    /// <c>_fields</c> asked for that — while <paramref name="source"/> is the stored document the parent
    /// key is read from, so selecting fields cannot make an expansion disappear.
    /// </summary>
    /// <param name="cache">
    /// Optional per-batch memo, so listing a hundred orders of one customer reads that customer once.
    /// Keyed by collection and id; a parent that does not exist is remembered as absent too.
    /// </param>
    public static string Embed(
        string body,
        ResourceDocument source,
        ExpansionPlan plan,
        TenantId tenant,
        IResourceStore store,
        Dictionary<(string Collection, string Id), string?>? cache = null)
    {
        if (plan.IsEmpty)
        {
            return body;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                // Nothing to embed into. A body that is an array or a scalar is legal here (the store
                // accepts any JSON) and returning it untouched is better than wrapping it in a shape
                // the caller did not ask for.
                return body;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    // A document that already carries an `_expand` field loses it to ours: two
                    // properties of one name is not JSON anyone can read reliably, and the one the
                    // request asked for is the one that answers the request.
                    if (!string.Equals(property.Name, Envelope, StringComparison.Ordinal))
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WritePropertyName(Envelope);
                writer.WriteStartObject();
                foreach (var expansion in plan.Expansions)
                {
                    writer.WritePropertyName(expansion.Name);
                    WriteParent(writer, source, expansion.Relation, tenant, store, cache);
                }

                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return body;
        }
    }

    /// <summary>
    /// Writes the parent document, or <c>null</c>. A key that names nothing — never set, or pointing at
    /// a document since deleted — embeds null rather than failing the read: the caller asked for this
    /// document, and answering 404 because something beside it is missing would be a worse answer than
    /// the truth that there is no parent.
    /// </summary>
    private static void WriteParent(
        Utf8JsonWriter writer,
        ResourceDocument source,
        ResourceRelation relation,
        TenantId tenant,
        IResourceStore store,
        Dictionary<(string Collection, string Id), string?>? cache)
    {
        if (ResourceRelations.ParentIdOf(source, relation) is not { Length: > 0 } parentId)
        {
            writer.WriteNullValue();
            return;
        }

        var key = (relation.Collection, parentId);
        if (cache is null || !cache.TryGetValue(key, out var parentBody))
        {
            // Tenant-scoped by construction, like every other relational read: a parent that exists for
            // somebody else is absent here, not embedded.
            parentBody = store.Get(tenant, relation.Collection, parentId)?.Body;
            cache?.Add(key, parentBody);
        }

        if (parentBody is null)
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            using var parent = JsonDocument.Parse(parentBody);
            parent.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteNullValue();
        }
    }
}
