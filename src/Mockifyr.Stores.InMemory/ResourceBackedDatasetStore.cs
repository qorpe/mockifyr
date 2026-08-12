using System.Text.Json;
using Mockifyr.Core;

namespace Mockifyr.Stores.InMemory;

/// <summary>
/// Keeps dataset definitions and their load records as documents in the resource store (#351), under
/// collection names no caller can reach.
/// </summary>
/// <remarks>
/// The same reasoning as <see cref="ResourceBackedSchemaStore"/>, and for the load record it is
/// sharper: a dataset whose definition survived a restart while the record of what it created did not
/// would be a dataset you can load twice and remove once. Riding the resource store means both halves
/// persist, restore and reload together on every backend, because they are the same kind of thing
/// stored the same way.
/// </remarks>
public sealed class ResourceBackedDatasetStore(IResourceStore documents) : IDatasetStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <inheritdoc />
    public IReadOnlyList<DatasetDefinition> List(TenantId tenant) =>
        [.. documents.List(tenant, ResourceRelations.DatasetCollection)
            .Select(document => Read<StoredDataset>(document.Body) is { } stored ? ToDefinition(document.Id, stored) : null)
            .OfType<DatasetDefinition>()
            .OrderBy(dataset => dataset.Name, StringComparer.Ordinal)];

    /// <inheritdoc />
    public DatasetDefinition? Get(TenantId tenant, string name) =>
        documents.Get(tenant, ResourceRelations.DatasetCollection, name) is { } document
            && Read<StoredDataset>(document.Body) is { } stored
                ? ToDefinition(name, stored)
                : null;

    /// <inheritdoc />
    public void Put(TenantId tenant, DatasetDefinition dataset) =>
        documents.Put(tenant, ResourceRelations.DatasetCollection, dataset.Name, JsonSerializer.Serialize(
            new StoredDataset(
                [.. dataset.Items.Select(i => new StoredItem(i.Collection, i.Count, i.Document, i.Id))],
                dataset.Seed),
            Json));

    /// <inheritdoc />
    public bool Delete(TenantId tenant, string name) =>
        documents.Delete(tenant, ResourceRelations.DatasetCollection, name);

    /// <inheritdoc />
    public DatasetLoad? GetLoad(TenantId tenant, string name) =>
        documents.Get(tenant, ResourceRelations.DatasetLoadCollection, name) is { } document
            && Read<StoredLoad>(document.Body) is { } stored
                ? new DatasetLoad(name, [.. stored.Created.Select(c => new ResourceLink(c.Collection, c.Id))], stored.LoadedAt)
                : null;

    /// <inheritdoc />
    public void RecordLoad(TenantId tenant, DatasetLoad load) =>
        documents.Put(tenant, ResourceRelations.DatasetLoadCollection, load.Name, JsonSerializer.Serialize(
            new StoredLoad([.. load.Created.Select(c => new StoredLink(c.Collection, c.Id))], load.LoadedAt),
            Json));

    /// <inheritdoc />
    public void ClearLoad(TenantId tenant, string name) =>
        documents.Delete(tenant, ResourceRelations.DatasetLoadCollection, name);

    private sealed record StoredItem(string Collection, int Count, string Document, string? Id);

    private sealed record StoredDataset(List<StoredItem> Items, int? Seed);

    private sealed record StoredLink(string Collection, string Id);

    private sealed record StoredLoad(List<StoredLink> Created, DateTimeOffset LoadedAt);

    private static DatasetDefinition ToDefinition(string name, StoredDataset stored) =>
        new(name, [.. stored.Items.Select(i => new DatasetItem(i.Collection, i.Count, i.Document, i.Id))], stored.Seed);

    /// <summary>
    /// Reads one stored record, or null when it does not parse. Dropped rather than thrown: this
    /// storage is reachable by a restore or a hand-edited file, and one unreadable dataset must not
    /// make the rest of them unreadable too.
    /// </summary>
    private static T? Read<T>(string body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
