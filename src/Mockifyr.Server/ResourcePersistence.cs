using System.Text.Json;
using LiteDB;
using Mockifyr.Core;
using Npgsql;
using StackExchange.Redis;

namespace Mockifyr.Server;

/// <summary>
/// Serialization shared by every resource-persistence provider, so a document written by one backend
/// reads the same as one written by another (G19a durability).
/// </summary>
internal static class ResourceJson
{
    private sealed record Stored(string Id, string Collection, string Body, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version);

    public static string Serialize(ResourceDocument document) => System.Text.Json.JsonSerializer.Serialize(
        new Stored(document.Id, document.Collection, document.Body, document.CreatedAt, document.UpdatedAt, document.Version));

    /// <summary>Reads a stored document, or null when the row is not one (a hand-edited file, an old shape).</summary>
    public static ResourceDocument? Deserialize(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Stored>(json) is { } stored
                ? new ResourceDocument(stored.Id, stored.Collection, stored.Body, stored.CreatedAt, stored.UpdatedAt, stored.Version)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Groups loaded documents by tenant, the shape <see cref="IResourcesLoader"/> returns.</summary>
    public static IReadOnlyDictionary<TenantId, IReadOnlyList<ResourceDocument>> Group(
        IEnumerable<(TenantId Tenant, ResourceDocument Document)> rows) =>
        rows.GroupBy(row => row.Tenant)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ResourceDocument>)[.. group.Select(row => row.Document)]);

    /// <summary>
    /// A file-system-safe name for a collection or document id.
    /// </summary>
    /// <remarks>
    /// Ids are caller-supplied — a partner seeding a sandbox chooses them — so they cannot be trusted
    /// as path segments. Anything outside a conservative allowlist is percent-escaped, which keeps
    /// `../` and separators from ever reaching the path while staying readable for ordinary ids.
    /// </remarks>
    public static string SafeName(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('%').Append(((int)character).ToString("X2"));
            }
        }

        // A name of dots only would still be a relative path component after escaping nothing.
        var safe = builder.ToString();
        return safe is "." or ".." ? safe.Replace(".", "%2E") : safe;
    }
}

// ---- File system (G16a counterpart) ------------------------------------------------------------

/// <summary>
/// File-backed resource persistence: <c>&lt;root&gt;/&lt;tenant&gt;/&lt;collection&gt;/&lt;id&gt;.json</c>.
/// </summary>
public sealed class FileSystemResourcePersistence(string resourcesDirectory) : IResourcePersistence
{
    /// <inheritdoc />
    public void Save(TenantId tenant, ResourceDocument document)
    {
        var directory = DirectoryFor(tenant, document.Collection);
        Directory.CreateDirectory(directory);
        File.WriteAllText(FileFor(directory, document.Id), ResourceJson.Serialize(document));
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, string collection, string id)
    {
        var file = FileFor(DirectoryFor(tenant, collection), id);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant, string? collection)
    {
        var directory = collection is null
            ? Path.Combine(resourcesDirectory, ResourceJson.SafeName(tenant.Value))
            : DirectoryFor(tenant, collection);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string DirectoryFor(TenantId tenant, string collection) =>
        Path.Combine(resourcesDirectory, ResourceJson.SafeName(tenant.Value), ResourceJson.SafeName(collection));

    private static string FileFor(string directory, string id) =>
        Path.Combine(directory, ResourceJson.SafeName(id) + ".json");
}

/// <summary>Reloads what <see cref="FileSystemResourcePersistence"/> wrote.</summary>
public sealed class FileSystemResourcesLoader(string resourcesDirectory) : IResourcesLoader
{
    /// <inheritdoc />
    public IReadOnlyDictionary<TenantId, IReadOnlyList<ResourceDocument>> LoadAll()
    {
        if (!Directory.Exists(resourcesDirectory))
        {
            return new Dictionary<TenantId, IReadOnlyList<ResourceDocument>>();
        }

        var rows = new List<(TenantId, ResourceDocument)>();
        foreach (var tenantDirectory in Directory.EnumerateDirectories(resourcesDirectory))
        {
            // The tenant and collection come from the stored document, not from the directory names:
            // those were escaped on the way out, and un-escaping them would be a second encoding to
            // keep in step with the first.
            foreach (var file in Directory.EnumerateFiles(tenantDirectory, "*.json", SearchOption.AllDirectories))
            {
                if (ResourceJson.Deserialize(File.ReadAllText(file)) is { } document)
                {
                    rows.Add((new TenantId(Path.GetFileName(tenantDirectory)), document));
                }
            }
        }

        return ResourceJson.Group(rows);
    }
}

// ---- LiteDB (G16b counterpart) -----------------------------------------------------------------

/// <summary>A persisted resource row in LiteDB: composite id <c>tenant|collection|id</c>.</summary>
internal sealed class StoredResource
{
    public string Id { get; set; } = string.Empty;

    public string Tenant { get; set; } = string.Empty;

    public string Collection { get; set; } = string.Empty;

    public string Json { get; set; } = string.Empty;
}

/// <summary>LiteDB-backed resource persistence.</summary>
public sealed class LiteDbResourcePersistence(LiteDatabase database) : IResourcePersistence
{
    internal const string CollectionName = "resources";

    private readonly ILiteCollection<StoredResource> _rows =
        database.GetCollection<StoredResource>(CollectionName);

    internal static string IdFor(TenantId tenant, string collection, string id) =>
        $"{tenant.Value}|{collection}|{id}";

    /// <inheritdoc />
    public void Save(TenantId tenant, ResourceDocument document) =>
        _rows.Upsert(new StoredResource
        {
            Id = IdFor(tenant, document.Collection, document.Id),
            Tenant = tenant.Value,
            Collection = document.Collection,
            Json = ResourceJson.Serialize(document),
        });

    /// <inheritdoc />
    public void Remove(TenantId tenant, string collection, string id) => _rows.Delete(IdFor(tenant, collection, id));

    /// <inheritdoc />
    public void Clear(TenantId tenant, string? collection) => _rows.DeleteMany(row =>
        row.Tenant == tenant.Value && (collection == null || row.Collection == collection));
}

/// <summary>Reloads what <see cref="LiteDbResourcePersistence"/> wrote.</summary>
public sealed class LiteDbResourcesLoader(LiteDatabase database) : IResourcesLoader
{
    private readonly ILiteCollection<StoredResource> _rows =
        database.GetCollection<StoredResource>(LiteDbResourcePersistence.CollectionName);

    /// <inheritdoc />
    public IReadOnlyDictionary<TenantId, IReadOnlyList<ResourceDocument>> LoadAll() =>
        ResourceJson.Group(_rows.FindAll()
            .Select(row => (Tenant: new TenantId(row.Tenant), Document: ResourceJson.Deserialize(row.Json)))
            .Where(row => row.Document is not null)
            .Select(row => (row.Tenant, row.Document!)));
}

// ---- PostgreSQL (G16c counterpart) -------------------------------------------------------------

/// <summary>
/// Creates the resources table if absent. The primary key is <c>(tenant, collection, id)</c>, so the
/// same document id lives independently in every tenant — tenant isolation expressed in the schema
/// rather than only in the code above it.
/// </summary>
internal static class PostgresResourceSchema
{
    private const string CreateTable =
        "CREATE TABLE IF NOT EXISTS resources (tenant text NOT NULL, collection text NOT NULL, id text NOT NULL, " +
        "json text NOT NULL, PRIMARY KEY (tenant, collection, id))";

    public static void Ensure(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(CreateTable, connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>PostgreSQL-backed resource persistence.</summary>
public sealed class PostgresResourcePersistence : IResourcePersistence
{
    private readonly string _connectionString;
    private readonly ChangeFeedIdentity? _writer;

    public PostgresResourcePersistence(string connectionString, ChangeFeedIdentity? writer = null)
    {
        _connectionString = connectionString;
        _writer = writer;
        PostgresResourceSchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public void Save(TenantId tenant, ResourceDocument document)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO resources (tenant, collection, id, json) VALUES (@tenant, @collection, @id, @json) " +
            "ON CONFLICT (tenant, collection, id) DO UPDATE SET json = @json",
            connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("collection", document.Collection);
        command.Parameters.AddWithValue("id", document.Id);
        command.Parameters.AddWithValue("json", ResourceJson.Serialize(document));
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, string collection, string id)
    {
        using var connection = Open();
        // Scoped by tenant as well: a DELETE on collection+id alone would reach across tenants.
        using var command = new NpgsqlCommand(
            "DELETE FROM resources WHERE tenant = @tenant AND collection = @collection AND id = @id", connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("collection", collection);
        command.Parameters.AddWithValue("id", id);
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant, string? collection)
    {
        using var connection = Open();
        using var command = collection is null
            ? new NpgsqlCommand("DELETE FROM resources WHERE tenant = @tenant", connection)
            : new NpgsqlCommand("DELETE FROM resources WHERE tenant = @tenant AND collection = @collection", connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        if (collection is not null)
        {
            command.Parameters.AddWithValue("collection", collection);
        }

        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }
}

/// <summary>Reloads what <see cref="PostgresResourcePersistence"/> wrote.</summary>
public sealed class PostgresResourcesLoader : IResourcesLoader
{
    private readonly string _connectionString;

    public PostgresResourcesLoader(string connectionString)
    {
        _connectionString = connectionString;
        PostgresResourceSchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<TenantId, IReadOnlyList<ResourceDocument>> LoadAll()
    {
        var rows = new List<(TenantId, ResourceDocument)>();
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT tenant, json FROM resources", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (ResourceJson.Deserialize(reader.GetString(1)) is { } document)
            {
                rows.Add((new TenantId(reader.GetString(0)), document));
            }
        }

        return ResourceJson.Group(rows);
    }
}

// ---- Redis (G16d counterpart) ------------------------------------------------------------------

/// <summary>
/// Redis-backed resource persistence: one hash per tenant and collection
/// (<c>mockifyr:resources:{tenant}:{collection}</c>) keyed by document id.
/// </summary>
public sealed class RedisResourcePersistence(IConnectionMultiplexer redis, ChangeFeedIdentity? writer = null)
    : IResourcePersistence
{
    internal const string Prefix = "mockifyr:resources:";

    internal static string HashKey(TenantId tenant, string collection) => $"{Prefix}{tenant.Value}:{collection}";

    /// <inheritdoc />
    public void Save(TenantId tenant, ResourceDocument document)
    {
        redis.GetDatabase().HashSet(
            HashKey(tenant, document.Collection), document.Id, ResourceJson.Serialize(document));
        ChangeFeedAnnouncement.Redis(redis, writer);
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, string collection, string id)
    {
        redis.GetDatabase().HashDelete(HashKey(tenant, collection), id);
        ChangeFeedAnnouncement.Redis(redis, writer);
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant, string? collection)
    {
        var database = redis.GetDatabase();
        if (collection is not null)
        {
            database.KeyDelete(HashKey(tenant, collection));
            ChangeFeedAnnouncement.Redis(redis, writer);
            return;
        }

        var endpoint = redis.GetEndPoints().FirstOrDefault();
        if (endpoint is null)
        {
            return;
        }

        foreach (var key in redis.GetServer(endpoint).Keys(pattern: $"{Prefix}{tenant.Value}:*"))
        {
            database.KeyDelete(key);
        }

        ChangeFeedAnnouncement.Redis(redis, writer);
    }
}

/// <summary>Reloads what <see cref="RedisResourcePersistence"/> wrote.</summary>
public sealed class RedisResourcesLoader(IConnectionMultiplexer redis) : IResourcesLoader
{
    /// <inheritdoc />
    public IReadOnlyDictionary<TenantId, IReadOnlyList<ResourceDocument>> LoadAll()
    {
        var rows = new List<(TenantId, ResourceDocument)>();
        var endpoint = redis.GetEndPoints().FirstOrDefault();
        if (endpoint is null)
        {
            return ResourceJson.Group(rows);
        }

        var database = redis.GetDatabase();
        foreach (var redisKey in redis.GetServer(endpoint).Keys(pattern: $"{RedisResourcePersistence.Prefix}*"))
        {
            // key = mockifyr:resources:{tenant}:{collection}; the tenant is the segment before the
            // LAST colon, since a collection name cannot contain one but a tenant might.
            var suffix = redisKey.ToString()[RedisResourcePersistence.Prefix.Length..];
            var separator = suffix.LastIndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var tenant = new TenantId(suffix[..separator]);
            foreach (var entry in database.HashGetAll(redisKey))
            {
                if (ResourceJson.Deserialize(entry.Value.ToString()) is { } document)
                {
                    rows.Add((tenant, document));
                }
            }
        }

        return ResourceJson.Group(rows);
    }
}
