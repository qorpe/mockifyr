using Microsoft.Extensions.Hosting;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;
using Npgsql;

namespace Mockifyr.Server;

/// <summary>
/// Creates the persistence table if it is absent — shared by the Postgres provider and loader so
/// whichever runs first (the loader runs at startup, before any mutation) finds the schema in place.
/// </summary>
internal static class PostgresSchema
{
    private const string CreateTable =
        "CREATE TABLE IF NOT EXISTS stubs (id uuid PRIMARY KEY, tenant text NOT NULL, json text NOT NULL)";

    public static void Ensure(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(CreateTable, connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// PostgreSQL-backed stub persistence (G16c): the third provider behind the <see cref="IStubPersistence"/>
/// seam — a real SQL backend. Each stub is a row <c>(id, tenant, json)</c>; the stored JSON is
/// id-stamped (shared <see cref="PersistableJson"/>) so ids round-trip identically to the file/LiteDB
/// backends. Connections are opened per operation from Npgsql's pool (thread-safe). The counterpart
/// <see cref="PostgresMappingsLoader"/> reloads rows on startup.
/// </summary>
public sealed class PostgresStubPersistence : IStubPersistence
{
    /// <summary>The <c>NOTIFY</c>/<c>LISTEN</c> channel a mutation announces on, so other instances reload (G16f).</summary>
    internal const string ChangeChannel = "mockifyr_changes";

    private readonly string _connectionString;
    private readonly ChangeFeedIdentity? _writer;

    public PostgresStubPersistence(string connectionString, ChangeFeedIdentity? writer = null)
    {
        _connectionString = connectionString;
        _writer = writer;
        PostgresSchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public void Save(StubMapping stub, string mappingJson)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            "INSERT INTO stubs (id, tenant, json) VALUES (@id, @tenant, @json) " +
            "ON CONFLICT (id) DO UPDATE SET tenant = @tenant, json = @json",
            connection);
        command.Parameters.AddWithValue("id", stub.Id);
        command.Parameters.AddWithValue("tenant", stub.TenantId.Value);
        command.Parameters.AddWithValue("json", PersistableJson.WithId(mappingJson, stub.Id));
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public void Remove(TenantId tenant, Guid id)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("DELETE FROM stubs WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }

    /// <inheritdoc />
    public void Clear(TenantId tenant)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("DELETE FROM stubs WHERE tenant = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);
        command.ExecuteNonQuery();
        ChangeFeedAnnouncement.Postgres(connection, _writer);
    }
}

/// <summary>
/// The Postgres change-feed reloader (G16f): the <see cref="RedisChangeFeedReloader"/> counterpart using
/// PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c>. It holds a dedicated connection that <c>LISTEN</c>s on the
/// change channel and, on every notification another instance emits, reconciles this host's in-memory
/// state from the loaders (<see cref="ChangeFeedReconciler"/>) — so a mutation on one instance is
/// reflected by the others without a restart. Environments and sandbox resources reload alongside stubs
/// since #279. A background loop drives Npgsql's notification delivery.
/// </summary>
public sealed class PostgresChangeFeedReloader : IHostedService
{
    private readonly string _connectionString;
    private readonly ChangeFeedTargets _targets;
    private readonly CancellationTokenSource _stopping = new();
    private NpgsqlConnection? _connection;
    private Task? _listenLoop;

    public PostgresChangeFeedReloader(string connectionString, ChangeFeedTargets targets)
    {
        _connectionString = connectionString;
        _targets = targets;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _connection = new NpgsqlConnection(_connectionString);
        await _connection.OpenAsync(cancellationToken);
        _connection.Notification += (_, args) => ChangeFeedReconciler.Reload(_targets, args.Payload);

        using (var listen = new NpgsqlCommand($"LISTEN {PostgresStubPersistence.ChangeChannel}", _connection))
        {
            await listen.ExecuteNonQueryAsync(cancellationToken);
        }

        // Npgsql only delivers notifications while a wait is in flight, so drive it in a loop.
        _listenLoop = ListenLoopAsync(_stopping.Token);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        if (_listenLoop is not null)
        {
            await Task.WhenAny(_listenLoop, Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _connection!.WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception)
        {
            // The connection was disposed on shutdown; nothing to recover.
        }
    }
}

/// <summary>
/// Reloads the stubs a <see cref="PostgresStubPersistence"/> saved (G16c) — the
/// <see cref="IMappingsLoader"/> counterpart, run at startup. Reads the tenant's rows and parses each
/// stored JSON back into a <see cref="StubMapping"/> (ids preserved via the id-stamped JSON).
/// </summary>
public sealed class PostgresMappingsLoader : IMappingsLoader, IMultiTenantMappingsLoader
{
    private readonly string _connectionString;
    private readonly IMatcherRegistry? _matchers;

    public PostgresMappingsLoader(string connectionString, IMatcherRegistry? matchers = null)
    {
        _connectionString = connectionString;
        _matchers = matchers;
        PostgresSchema.Ensure(connectionString);
    }

    /// <inheritdoc />
    public IReadOnlyList<StubMapping> Load(TenantId tenant)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT json FROM stubs WHERE tenant = @tenant", connection);
        command.Parameters.AddWithValue("tenant", tenant.Value);

        var stubs = new List<StubMapping>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            stubs.AddRange(MappingJsonReader.Read(reader.GetString(0), tenant, _matchers));
        }

        return stubs;
    }

    /// <inheritdoc />
    public IReadOnlyList<StubMapping> LoadAllTenants()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("SELECT tenant, json FROM stubs", connection);

        var stubs = new List<StubMapping>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var tenant = new TenantId(reader.GetString(0));
            stubs.AddRange(MappingJsonReader.Read(reader.GetString(1), tenant, _matchers));
        }

        return stubs;
    }
}
