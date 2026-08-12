using LiteDB;
using Mockifyr.Core;
using Npgsql;
using StackExchange.Redis;

namespace Mockifyr.Server;

/// <summary>
/// The wire form of a declared tenant (#357), shared by all four backends so a declaration written by
/// one is shaped identically to the others — the G17 pattern, as with API keys.
/// </summary>
internal static class TenantJson
{
    private sealed record StoredTenant(
        string Id, string DisplayName, DateTimeOffset CreatedAt, string Status, long? StorageLimitBytes,
        bool? Idempotency = null);

    public static string Serialize(TenantRecord tenant) =>
        System.Text.Json.JsonSerializer.Serialize(new StoredTenant(
            tenant.Id.Value,
            tenant.DisplayName,
            tenant.CreatedAt,
            tenant.Status == TenantStatus.Suspended ? "suspended" : "active",
            tenant.StorageLimitBytes,
            tenant.Idempotency));

    /// <summary>Reads a declaration back, returning null for anything unparseable rather than failing startup.</summary>
    public static TenantRecord? Deserialize(string json)
    {
        try
        {
            var stored = System.Text.Json.JsonSerializer.Deserialize<StoredTenant>(json);
            return stored is null || string.IsNullOrEmpty(stored.Id)
                ? null
                : new TenantRecord(
                    new TenantId(stored.Id),
                    stored.DisplayName,
                    stored.CreatedAt,
                    // Unknown status reads as active: a declaration written by a newer version must not
                    // silently suspend a tenant on an older one.
                    string.Equals(stored.Status, "suspended", StringComparison.Ordinal)
                        ? TenantStatus.Suspended
                        : TenantStatus.Active,
                    stored.StorageLimitBytes,
                    stored.Idempotency);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>File-backed tenant declarations (#357): one JSON file per tenant under <c>&lt;root-dir&gt;/tenants/</c>.</summary>
public sealed class FileSystemTenantPersistence(string directory) : ITenantPersistence
{
    /// <inheritdoc />
    public void Save(TenantRecord tenant)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FileFor(tenant.Id), TenantJson.Serialize(tenant));
    }

    /// <inheritdoc />
    public void Remove(TenantId id)
    {
        var file = FileFor(id);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TenantRecord> LoadAll() =>
        !Directory.Exists(directory)
            ? []
            : [.. Directory.EnumerateFiles(directory, "*.json")
                .Select(file => TenantJson.Deserialize(File.ReadAllText(file)))
                .Where(tenant => tenant is not null)
                .Select(tenant => tenant!)];

    // A tenant id is validated as identifier-shaped before it is ever declared, so it is a safe file
    // name — the same argument the API key store makes about a GUID.
    private string FileFor(TenantId id) => Path.Combine(directory, id.Value + ".json");
}

/// <summary>A persisted tenant declaration in LiteDB.</summary>
internal sealed class StoredTenantRow
{
    public string Id { get; set; } = string.Empty;

    public string Json { get; set; } = string.Empty;
}

/// <summary>LiteDB-backed tenant declarations (#357).</summary>
public sealed class LiteDbTenantPersistence(LiteDatabase database) : ITenantPersistence
{
    internal const string Collection = "tenants";

    private readonly ILiteCollection<StoredTenantRow> _rows = database.GetCollection<StoredTenantRow>(Collection);

    /// <inheritdoc />
    public void Save(TenantRecord tenant) =>
        _rows.Upsert(new StoredTenantRow { Id = tenant.Id.Value, Json = TenantJson.Serialize(tenant) });

    /// <inheritdoc />
    public void Remove(TenantId id) => _rows.Delete(id.Value);

    /// <inheritdoc />
    public IReadOnlyList<TenantRecord> LoadAll() =>
        [.. _rows.FindAll()
            .Select(row => TenantJson.Deserialize(row.Json))
            .Where(tenant => tenant is not null)
            .Select(tenant => tenant!)];
}

/// <summary>Creates the tenants table if absent.</summary>
internal static class PostgresTenantSchema
{
    private const string CreateTable =
        "CREATE TABLE IF NOT EXISTS tenants (id text PRIMARY KEY, json text NOT NULL)";

    public static void Ensure(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(CreateTable, connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>PostgreSQL-backed tenant declarations (#357), announcing on the change feed.</summary>
public sealed class PostgresTenantPersistence : ITenantPersistence
{
    private readonly string _connectionString;
    private readonly ChangeFeedIdentity? _writer;

    public PostgresTenantPersistence(string connectionString, ChangeFeedIdentity? writer = null)
    {
        _connectionString = connectionString;
        _writer = writer;
        PostgresTenantSchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public void Save(TenantRecord tenant)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO tenants (id, json) VALUES (@id, @json) ON CONFLICT (id) DO UPDATE SET json = @json",
            connection);
        command.Parameters.AddWithValue("id", tenant.Id.Value);
        command.Parameters.AddWithValue("json", TenantJson.Serialize(tenant));
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public void Remove(TenantId id)
    {
        using var connection = Open();
        using var command = new NpgsqlCommand("DELETE FROM tenants WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id.Value);
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public IReadOnlyList<TenantRecord> LoadAll()
    {
        var tenants = new List<TenantRecord>();
        using var connection = Open();
        using var command = new NpgsqlCommand("SELECT json FROM tenants", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (TenantJson.Deserialize(reader.GetString(0)) is { } tenant)
            {
                tenants.Add(tenant);
            }
        }

        return tenants;
    }

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }
}

/// <summary>
/// Redis-backed tenant declarations (#357): one hash keyed by tenant id, announcing on the change feed
/// so a suspension takes effect on every replica rather than only the one that decided it.
/// </summary>
public sealed class RedisTenantPersistence(IConnectionMultiplexer redis, ChangeFeedIdentity? writer = null)
    : ITenantPersistence
{
    internal const string HashKey = "mockifyr:tenants";

    /// <inheritdoc />
    public void Save(TenantRecord tenant)
    {
        redis.GetDatabase().HashSet(HashKey, tenant.Id.Value, TenantJson.Serialize(tenant));
        ChangeFeedAnnouncement.Redis(redis, writer);
    }

    /// <inheritdoc />
    public void Remove(TenantId id)
    {
        redis.GetDatabase().HashDelete(HashKey, id.Value);
        ChangeFeedAnnouncement.Redis(redis, writer);
    }

    /// <inheritdoc />
    public IReadOnlyList<TenantRecord> LoadAll() =>
        [.. redis.GetDatabase().HashGetAll(HashKey)
            .Select(entry => TenantJson.Deserialize(entry.Value.ToString()))
            .Where(tenant => tenant is not null)
            .Select(tenant => tenant!)];
}
