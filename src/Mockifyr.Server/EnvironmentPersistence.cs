using System.Text.Json;
using LiteDB;
using Mockifyr.Core;
using Npgsql;
using StackExchange.Redis;

namespace Mockifyr.Server;

/// <summary>
/// The wire form of a persisted environment key (G17). Environments are a domain type of our own — not
/// an imported dialect like stub mappings — so they serialize directly rather than storing a source
/// document. Shared by all four backends so a key written by one is shaped identically to the others.
/// </summary>
internal static class EnvironmentJson
{
    // Secret is last and optional (#348): a row written before secrets existed carries no such
    // property and reads back as false, which is what it was.
    private sealed record StoredValue(string Name, string Value, bool Secret = false);

    private sealed record StoredKey(string Key, string ActiveValue, IReadOnlyList<StoredValue> Values);

    public static string Serialize(EnvironmentKey key) =>
        System.Text.Json.JsonSerializer.Serialize(new StoredKey(
            key.Key, key.ActiveValue, [.. key.Values.Select(v => new StoredValue(v.Name, v.Value, v.Secret))]));

    /// <summary>Reads a key back, returning null for anything unparseable rather than failing startup.</summary>
    public static EnvironmentKey? Deserialize(string json)
    {
        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<StoredKey>(json);
            return stored is null || string.IsNullOrEmpty(stored.Key)
                ? null
                : new EnvironmentKey(
                    stored.Key,
                    stored.ActiveValue,
                    [.. (stored.Values ?? []).Select(v => new EnvironmentValue(v.Name, v.Value, v.Secret))]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Groups loaded keys by tenant, the shape <see cref="IEnvironmentsLoader"/> returns.</summary>
    public static IReadOnlyDictionary<TenantId, IReadOnlyList<EnvironmentKey>> Group(
        IEnumerable<(TenantId Tenant, EnvironmentKey Key)> rows) =>
        rows.GroupBy(row => row.Tenant)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<EnvironmentKey>)[.. group.Select(row => row.Key)]);
}

// ---- File system (G16a counterpart) -----------------------------------------------------------

/// <summary>
/// File-backed environment persistence (G17): one JSON file per key under
/// <c>&lt;root-dir&gt;/environments/&lt;tenant&gt;/</c>. A per-tenant directory — rather than one shared
/// file — is what makes tenant isolation visible on disk and keeps a delete from rewriting another
/// tenant's data. File I/O lives at the host edge, never in Core.
/// </summary>
public sealed class FileSystemEnvironmentPersistence(string environmentsDirectory) : IEnvironmentPersistence
{
    /// <inheritdoc />
    public void Save(TenantId tenant, EnvironmentKey key)
    {
        var directory = DirectoryFor(tenant);
        Directory.CreateDirectory(directory);
        File.WriteAllText(FileFor(directory, key.Key), EnvironmentJson.Serialize(key));
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, string key)
    {
        var file = FileFor(DirectoryFor(tenant), key);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant)
    {
        var directory = DirectoryFor(tenant);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal string DirectoryFor(TenantId tenant) => Path.Combine(environmentsDirectory, tenant.Value);

    // The key is already constrained to [A-Za-z0-9_-] by ReservedEnvironmentKeys.IsWellFormed, so it
    // is a safe file name — no traversal component can reach here.
    private static string FileFor(string directory, string key) => Path.Combine(directory, key + ".json");
}

/// <summary>Reloads what <see cref="FileSystemEnvironmentPersistence"/> wrote (G17).</summary>
public sealed class FileSystemEnvironmentsLoader(string environmentsDirectory) : IEnvironmentsLoader
{
    /// <inheritdoc />
    public IReadOnlyDictionary<TenantId, IReadOnlyList<EnvironmentKey>> LoadAll()
    {
        if (!Directory.Exists(environmentsDirectory))
        {
            return new Dictionary<TenantId, IReadOnlyList<EnvironmentKey>>();
        }

        var rows = new List<(TenantId, EnvironmentKey)>();
        foreach (var tenantDirectory in Directory.EnumerateDirectories(environmentsDirectory))
        {
            var tenant = new TenantId(Path.GetFileName(tenantDirectory));
            foreach (var file in Directory.EnumerateFiles(tenantDirectory, "*.json"))
            {
                if (EnvironmentJson.Deserialize(File.ReadAllText(file)) is { } key)
                {
                    rows.Add((tenant, key));
                }
            }
        }

        return EnvironmentJson.Group(rows);
    }
}

// ---- LiteDB (G16b counterpart) ----------------------------------------------------------------

/// <summary>A persisted environment row in LiteDB: composite id <c>tenant|key</c>, plus the key JSON.</summary>
internal sealed class StoredEnvironment
{
    public string Id { get; set; } = string.Empty;

    public string Tenant { get; set; } = string.Empty;

    public string Json { get; set; } = string.Empty;
}

/// <summary>LiteDB-backed environment persistence (G17), mirroring <see cref="LiteDbStubPersistence"/>.</summary>
public sealed class LiteDbEnvironmentPersistence(LiteDatabase database) : IEnvironmentPersistence
{
    internal const string Collection = "environments";

    private readonly ILiteCollection<StoredEnvironment> _rows =
        database.GetCollection<StoredEnvironment>(Collection);

    internal static string IdFor(TenantId tenant, string key) => $"{tenant.Value}|{key}";

    /// <inheritdoc />
    public void Save(TenantId tenant, EnvironmentKey key) =>
        _rows.Upsert(new StoredEnvironment
        {
            Id = IdFor(tenant, key.Key),
            Tenant = tenant.Value,
            Json = EnvironmentJson.Serialize(key),
        });

    /// <inheritdoc />
    public void Remove(TenantId tenant, string key) => _rows.Delete(IdFor(tenant, key));

    /// <inheritdoc />
    public void Clear(TenantId tenant) => _rows.DeleteMany(row => row.Tenant == tenant.Value);
}

/// <summary>Reloads what <see cref="LiteDbEnvironmentPersistence"/> wrote (G17).</summary>
public sealed class LiteDbEnvironmentsLoader(LiteDatabase database) : IEnvironmentsLoader
{
    private readonly ILiteCollection<StoredEnvironment> _rows =
        database.GetCollection<StoredEnvironment>(LiteDbEnvironmentPersistence.Collection);

    /// <inheritdoc />
    public IReadOnlyDictionary<TenantId, IReadOnlyList<EnvironmentKey>> LoadAll() =>
        EnvironmentJson.Group(_rows.FindAll()
            .Select(row => (Tenant: new TenantId(row.Tenant), Key: EnvironmentJson.Deserialize(row.Json)))
            .Where(row => row.Key is not null)
            .Select(row => (row.Tenant, row.Key!)));
}

// ---- PostgreSQL (G16c counterpart) ------------------------------------------------------------

/// <summary>
/// Creates the environments table if absent. The primary key is <c>(tenant, key)</c> — not the key
/// alone — so the same key name can exist independently in every tenant, which is the database-level
/// expression of issue #166.
/// </summary>
internal static class PostgresEnvironmentSchema
{
    private const string CreateTable =
        "CREATE TABLE IF NOT EXISTS environments (tenant text NOT NULL, key text NOT NULL, json text NOT NULL, " +
        "PRIMARY KEY (tenant, key))";

    public static void Ensure(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(CreateTable, connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>PostgreSQL-backed environment persistence (G17), mirroring <see cref="PostgresStubPersistence"/>.</summary>
public sealed class PostgresEnvironmentPersistence : IEnvironmentPersistence
{
    private readonly string _connectionString;
    private readonly ChangeFeedIdentity? _writer;

    public PostgresEnvironmentPersistence(string connectionString, ChangeFeedIdentity? writer = null)
    {
        _connectionString = connectionString;
        _writer = writer;
        PostgresEnvironmentSchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public void Save(TenantId tenant, EnvironmentKey key)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO environments (tenant, key, json) VALUES (@tenant, @key, @json) " +
            "ON CONFLICT (tenant, key) DO UPDATE SET json = @json",
            connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("key", key.Key);
        command.Parameters.AddWithValue("json", EnvironmentJson.Serialize(key));
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, string key)
    {
        using var connection = Open();
        // Scoped by tenant as well as key: a DELETE on the key alone would reach across tenants.
        using var command = new NpgsqlCommand(
            "DELETE FROM environments WHERE tenant = @tenant AND key = @key", connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.Parameters.AddWithValue("key", key);
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("DELETE FROM environments WHERE tenant = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
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

/// <summary>Reloads what <see cref="PostgresEnvironmentPersistence"/> wrote (G17).</summary>
public sealed class PostgresEnvironmentsLoader : IEnvironmentsLoader
{
    private readonly string _connectionString;

    public PostgresEnvironmentsLoader(string connectionString)
    {
        _connectionString = connectionString;
        PostgresEnvironmentSchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<TenantId, IReadOnlyList<EnvironmentKey>> LoadAll()
    {
        var rows = new List<(TenantId, EnvironmentKey)>();
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT tenant, json FROM environments", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (EnvironmentJson.Deserialize(reader.GetString(1)) is { } key)
            {
                rows.Add((new TenantId(reader.GetString(0)), key));
            }
        }

        return EnvironmentJson.Group(rows);
    }
}

// ---- Redis (G16d counterpart) -----------------------------------------------------------------

/// <summary>
/// Redis-backed environment persistence (G17): one hash per tenant
/// (<c>mockifyr:environments:{tenant}</c>) keyed by the environment key name.
/// </summary>
public sealed class RedisEnvironmentPersistence(IConnectionMultiplexer redis, ChangeFeedIdentity? writer = null)
    : IEnvironmentPersistence
{
    internal static string HashKey(TenantId tenant) => $"mockifyr:environments:{tenant.Value}";

    /// <inheritdoc />
    public void Save(TenantId tenant, EnvironmentKey key)
    {
        redis.GetDatabase().HashSet(HashKey(tenant), key.Key, EnvironmentJson.Serialize(key));
        ChangeFeedAnnouncement.Redis(redis, writer);
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, string key)
    {
        redis.GetDatabase().HashDelete(HashKey(tenant), key);
        ChangeFeedAnnouncement.Redis(redis, writer);
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant)
    {
        redis.GetDatabase().KeyDelete(HashKey(tenant));
        ChangeFeedAnnouncement.Redis(redis, writer);
    }
}

/// <summary>Reloads what <see cref="RedisEnvironmentPersistence"/> wrote (G17).</summary>
public sealed class RedisEnvironmentsLoader(IConnectionMultiplexer redis) : IEnvironmentsLoader
{
    /// <inheritdoc />
    public IReadOnlyDictionary<TenantId, IReadOnlyList<EnvironmentKey>> LoadAll()
    {
        var rows = new List<(TenantId, EnvironmentKey)>();
        var endpoint = redis.GetEndPoints().FirstOrDefault();
        if (endpoint is null)
        {
            return EnvironmentJson.Group(rows);
        }

        var database = redis.GetDatabase();
        foreach (var redisKey in redis.GetServer(endpoint).Keys(pattern: "mockifyr:environments:*"))
        {
            var tenant = new TenantId(redisKey.ToString()["mockifyr:environments:".Length..]);
            foreach (var entry in database.HashGetAll(redisKey))
            {
                if (EnvironmentJson.Deserialize(entry.Value.ToString()) is { } key)
                {
                    rows.Add((tenant, key));
                }
            }
        }

        return EnvironmentJson.Group(rows);
    }
}
