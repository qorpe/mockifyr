namespace Mockifyr.Core;

/// <summary>
/// One collection's share of a dataset (#351): how many documents, and the template each is rendered
/// from.
/// </summary>
/// <param name="Collection">The collection the documents land in.</param>
/// <param name="Count">How many to produce. One is the ordinary case; a larger number is what turns
/// "two hundred plausible customers" into a line rather than a file.</param>
/// <param name="Document">The document template, rendered once per document at the edge — Core never
/// renders anything.</param>
/// <param name="Id">An optional id template; absent ids are generated.</param>
public sealed record DatasetItem(string Collection, int Count, string Document, string? Id = null);

/// <summary>
/// A named set of documents across collections, loaded and reset as one thing (#351).
/// </summary>
/// <remarks>
/// <para>
/// "The delinquent customer" is a customer, three orders, two failed payments and a dunning record —
/// across four collections and only meaningful together. Without a name for that, it lives in
/// somebody's shell script, which is the one place nobody else can find it.
/// </para>
/// <para>
/// <paramref name="Seed"/> makes the generated data reproducible. A dataset that produced different
/// customers every time could not be the basis of a regression test, which is most of why anybody
/// wants one.
/// </para>
/// </remarks>
public sealed record DatasetDefinition(string Name, IReadOnlyList<DatasetItem> Items, int? Seed = null);

/// <summary>
/// What a load actually created, so the same dataset can be taken back out again (#351).
/// </summary>
/// <remarks>
/// Recorded rather than recomputed: resetting by "delete everything in the collections this dataset
/// touches" would take a colleague's data with it, and a dataset that cannot be removed without
/// collateral is one people stop loading.
/// </remarks>
public sealed record DatasetLoad(string Name, IReadOnlyList<ResourceLink> Created, DateTimeOffset LoadedAt);

/// <summary>
/// Tenant-scoped store of dataset definitions and their last load (#351). Every entry point takes the
/// <see cref="TenantId"/> — there is no tenant-less overload (CLAUDE.md §2.6).
/// </summary>
public interface IDatasetStore
{
    /// <summary>The tenant's datasets, name-ordered.</summary>
    IReadOnlyList<DatasetDefinition> List(TenantId tenant);

    /// <summary>One dataset, or null when the tenant has none by that name.</summary>
    DatasetDefinition? Get(TenantId tenant, string name);

    /// <summary>Declares or replaces a dataset.</summary>
    void Put(TenantId tenant, DatasetDefinition dataset);

    /// <summary>Removes a dataset definition; false when there was none. Loaded documents are untouched.</summary>
    bool Delete(TenantId tenant, string name);

    /// <summary>What the last load of this dataset created, or null when it has never been loaded.</summary>
    DatasetLoad? GetLoad(TenantId tenant, string name);

    /// <summary>Records what a load created, replacing any earlier record for the same dataset.</summary>
    void RecordLoad(TenantId tenant, DatasetLoad load);

    /// <summary>Forgets the load record for a dataset.</summary>
    void ClearLoad(TenantId tenant, string name);
}

/// <summary>The pure decisions a dataset load rests on (#351): what order, and is it usable at all.</summary>
public static class Datasets
{
    /// <summary>How many documents one dataset may produce.</summary>
    /// <remarks>
    /// A bound rather than none: the count comes from a request, and a dataset is loaded by whoever
    /// holds a credential. Ten thousand is far past any believable scenario and small enough that a
    /// mistyped count refuses instead of filling the host.
    /// </remarks>
    public const int MaxDocuments = 10_000;

    /// <summary>
    /// The dataset's items in dependency order: a collection is loaded after everything it belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Referential integrity (ADR 0015) refuses a child whose parent does not exist yet, so a dataset
    /// listed in the wrong order would fail on its second item — and asking the author to order it by
    /// hand is asking them to know the relation graph they did not write.
    /// </para>
    /// <para>
    /// Cycles are legal in that graph (<c>employees.managerId → employees</c> is a real model), so this
    /// cannot be a strict topological sort that refuses one. It is a stable ordering that puts parents
    /// first where it can and leaves the rest in the order they were written — enforcement is
    /// presence-triggered, so a cycle simply loads with its keys unresolved until the second pass of
    /// whatever creates them.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DatasetItem> InDependencyOrder(
        IReadOnlyList<DatasetItem> items,
        IReadOnlyList<ResourceSchema> schemas)
    {
        var ordered = new List<DatasetItem>(items.Count);
        var remaining = new List<DatasetItem>(items);

        // Repeatedly take every item still waiting on nothing. "Waiting" is exactly "a parent this
        // dataset has not emitted yet", and an item leaves `remaining` the moment it is emitted — so
        // the remaining list already answers the question and a separate set of placed collections
        // would be a second copy of the same fact.
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(item => Parents(item, schemas).All(parent =>
                    remaining.All(other => !Names(other, item, parent))))
                .ToList();

            // Nothing is ready: the rest reference each other. Emit them in the order they were
            // written rather than refusing — a cycle is a legal model, not a malformed dataset.
            if (ready.Count == 0)
            {
                ordered.AddRange(remaining);
                break;
            }

            foreach (var item in ready)
            {
                ordered.Add(item);
                remaining.Remove(item);
            }
        }

        return ordered;
    }

    private static bool Names(DatasetItem candidate, DatasetItem self, string parent) =>
        !ReferenceEquals(candidate, self) && string.Equals(candidate.Collection, parent, StringComparison.Ordinal);

    private static IEnumerable<string> Parents(DatasetItem item, IReadOnlyList<ResourceSchema> schemas) =>
        schemas.FirstOrDefault(s => string.Equals(s.Collection, item.Collection, StringComparison.Ordinal))
            ?.BelongsTo.Select(r => r.Collection)
            ?? [];

    /// <summary>
    /// Why this dataset cannot be stored, or null when it can. Checked once at declaration so a load
    /// cannot fail halfway on something that was knowable up front.
    /// </summary>
    public static string? Invalid(DatasetDefinition dataset)
    {
        if (!ReservedEnvironmentKeys.IsWellFormed(dataset.Name) || dataset.Name.Length > 64)
        {
            return $"'{dataset.Name}' is not a usable dataset name.";
        }

        if (dataset.Items.Count == 0)
        {
            return "A dataset with no collections would load nothing.";
        }

        var total = 0;
        foreach (var item in dataset.Items)
        {
            if (!ReservedEnvironmentKeys.IsWellFormed(item.Collection) || item.Collection.Length > 64)
            {
                return $"'{item.Collection}' is not a usable collection name.";
            }

            if (item.Count <= 0)
            {
                return $"'{item.Collection}' asks for {item.Count} documents.";
            }

            // A template is deliberately NOT required to be JSON here. `{"total": {{random
            // 'Number.digit'}}}` is a legitimate and ordinary document template, and it is not JSON
            // until it has been rendered — checking it now would refuse the numeric helpers outright.
            // The guard belongs after rendering, where the loader applies it to what was produced.

            total += item.Count;
        }

        return total > MaxDocuments
            ? $"A dataset may create at most {MaxDocuments} documents; this one asks for {total}."
            : null;
    }
}
