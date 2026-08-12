using LiteDB;
using Mockifyr.Core;
using Npgsql;
using StackExchange.Redis;

namespace Mockifyr.Server;

/// <summary>
/// The wire form of a persisted API key (G19d, ADR 0011 addendum): the salted hash rides along —
/// the token itself never exists after issuance, so there is nothing secret to persist. Shared by
/// all four backends so a key written by one is shaped identically to the others (the G17 pattern).
/// </summary>
internal static class ApiKeyJson
{
    /// <remarks>
    /// The lifecycle fields (#355) are nullable with defaults that reproduce pre-#355 behaviour, so a
    /// row written by an older host reads back as a never-expiring, unrevoked, read-write key — which
    /// is exactly what it was. One shape for all four providers, as in G17.
    /// </remarks>
    private sealed record StoredKey(
        string Id, string Tenant, string Name, string Salt, string Hash, string Prefix,
        DateTimeOffset CreatedAt, int? QuotaPerHour,
        DateTimeOffset? ExpiresAt = null,
        DateTimeOffset? RevokedAt = null,
        string? RevokedBy = null,
        string? RevokedReason = null,
        string? Scope = null);

    public static string Serialize(ApiKey key) =>
        System.Text.Json.JsonSerializer.Serialize(new StoredKey(
            key.Id, key.Tenant.Value, key.Name, key.Salt, key.Hash, key.Prefix, key.CreatedAt, key.QuotaPerHour,
            key.ExpiresAt,
            key.Revocation?.At, key.Revocation?.By, key.Revocation?.Reason,
            key.Scope == ApiKeyScope.ReadOnly ? "read" : null));

    /// <summary>Reads a key back, returning null for anything unparseable rather than failing startup.</summary>
    public static ApiKey? Deserialize(string json)
    {
        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<StoredKey>(json);
            return stored is null || string.IsNullOrEmpty(stored.Id) || string.IsNullOrEmpty(stored.Hash)
                ? null
                : new ApiKey(
                    stored.Id, new TenantId(stored.Tenant), stored.Name, stored.Salt, stored.Hash,
                    stored.Prefix, stored.CreatedAt, stored.QuotaPerHour,
                    stored.ExpiresAt,
                    stored.RevokedAt is { } at
                        ? new ApiKeyRevocation(at, stored.RevokedBy ?? "unknown", stored.RevokedReason)
                        : null,
                    string.Equals(stored.Scope, "read", StringComparison.Ordinal)
                        ? ApiKeyScope.ReadOnly
                        : ApiKeyScope.ReadWrite);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

// ---- File system ------------------------------------------------------------------------------

/// <summary>
/// File-backed API key persistence (G19d): one JSON file per key under
/// <c>&lt;root-dir&gt;/apikeys/</c>. Host-level (not per tenant): the key id is the file name, and
/// resolution reads across tenants by design. Ids are generated GUID strings — safe file names.
/// </summary>
public sealed class FileSystemApiKeyPersistence(string apiKeysDirectory) : IApiKeyPersistence
{
    /// <inheritdoc />
    public void Save(ApiKey key)
    {
        Directory.CreateDirectory(apiKeysDirectory);
        File.WriteAllText(FileFor(key.Id), ApiKeyJson.Serialize(key));
    }

    /// <inheritdoc />
    public void Remove(string id)
    {
        var file = FileFor(id);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ApiKey> LoadAll()
    {
        if (!Directory.Exists(apiKeysDirectory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(apiKeysDirectory, "*.json")
            .Select(file => ApiKeyJson.Deserialize(File.ReadAllText(file)))
            .Where(key => key is not null)
            .Select(key => key!)];
    }

    private string FileFor(string id) => Path.Combine(apiKeysDirectory, id + ".json");
}

// ---- LiteDB -----------------------------------------------------------------------------------

/// <summary>A persisted API key row in LiteDB.</summary>
internal sealed class StoredApiKey
{
    public string Id { get; set; } = string.Empty;

    public string Json { get; set; } = string.Empty;
}

/// <summary>LiteDB-backed API key persistence (G19d), mirroring the G17 environment pattern.</summary>
public sealed class LiteDbApiKeyPersistence(LiteDatabase database) : IApiKeyPersistence
{
    internal const string Collection = "apikeys";

    private readonly ILiteCollection<StoredApiKey> _rows = database.GetCollection<StoredApiKey>(Collection);

    /// <inheritdoc />
    public void Save(ApiKey key) => _rows.Upsert(new StoredApiKey { Id = key.Id, Json = ApiKeyJson.Serialize(key) });

    /// <inheritdoc />
    public void Remove(string id) => _rows.Delete(id);

    /// <inheritdoc />
    public IReadOnlyList<ApiKey> LoadAll() =>
        [.. _rows.FindAll()
            .Select(row => ApiKeyJson.Deserialize(row.Json))
            .Where(key => key is not null)
            .Select(key => key!)];
}

// ---- PostgreSQL -------------------------------------------------------------------------------

/// <summary>Creates the apikeys table if absent (id is the primary key — keys are host-level rows).</summary>
internal static class PostgresApiKeySchema
{
    private const string CreateTable =
        "CREATE TABLE IF NOT EXISTS apikeys (id text PRIMARY KEY, json text NOT NULL)";

    public static void Ensure(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(CreateTable, connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>PostgreSQL-backed API key persistence (G19d).</summary>
/// <remarks>
/// Writes announce on the change feed (#354): a key issued on one replica has to be usable on the
/// next request whichever replica serves it, or a partner behind a load balancer sees intermittent
/// 401s — and a revoked key has to stop working everywhere at once, which is the half that matters.
/// </remarks>
public sealed class PostgresApiKeyPersistence : IApiKeyPersistence
{
    private readonly string _connectionString;
    private readonly ChangeFeedIdentity? _writer;

    public PostgresApiKeyPersistence(string connectionString, ChangeFeedIdentity? writer = null)
    {
        _connectionString = connectionString;
        _writer = writer;
        PostgresApiKeySchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public void Save(ApiKey key)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO apikeys (id, json) VALUES (@id, @json) ON CONFLICT (id) DO UPDATE SET json = @json",
            connection);
        command.Parameters.AddWithValue("id", key.Id);
        command.Parameters.AddWithValue("json", ApiKeyJson.Serialize(key));
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public void Remove(string id)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("DELETE FROM apikeys WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public IReadOnlyList<ApiKey> LoadAll()
    {
        var keys = new List<ApiKey>();
        using var connection = Open();
        using var command = new NpgsqlCommand("SELECT json FROM apikeys", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (ApiKeyJson.Deserialize(reader.GetString(0)) is { } key)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }
}

// ---- Redis ------------------------------------------------------------------------------------

/// <summary>Redis-backed API key persistence (G19d): one hash (<c>mockifyr:apikeys</c>) keyed by id.</summary>
public sealed class RedisApiKeyPersistence(IConnectionMultiplexer redis, ChangeFeedIdentity? writer = null)
    : IApiKeyPersistence
{
    internal const string HashKey = "mockifyr:apikeys";

    /// <inheritdoc />
    public void Save(ApiKey key)
    {
        redis.GetDatabase().HashSet(HashKey, key.Id, ApiKeyJson.Serialize(key));
        ChangeFeedAnnouncement.Redis(redis, writer);
    }

    /// <inheritdoc />
    public void Remove(string id)
    {
        redis.GetDatabase().HashDelete(HashKey, id);
        ChangeFeedAnnouncement.Redis(redis, writer);
    }

    /// <inheritdoc />
    public IReadOnlyList<ApiKey> LoadAll() =>
        [.. redis.GetDatabase().HashGetAll(HashKey)
            .Select(entry => ApiKeyJson.Deserialize(entry.Value.ToString()))
            .Where(key => key is not null)
            .Select(key => key!)];
}
