using Npgsql;
using StackExchange.Redis;

namespace Mockifyr.Server;

/// <summary>
/// Who wrote a change, so a host can tell its own announcements from everybody else's (#279).
/// </summary>
/// <remarks>
/// <para>
/// Without it a host reloads because of its own write, and that reload can restore what it read a moment
/// ago over what it has just written: read the backend at version 1, write version 2, then let the older
/// read land. The host then serves its own write back at the previous version — a host failing to read
/// its own write is worse than the staleness this feed exists to remove.
/// </para>
/// <para>
/// One identity per host rather than per process: the test suite runs two hosts in one process, and so
/// does anyone embedding Mockifyr twice. A process-wide identity would make them deaf to each other,
/// which is the same bug wearing the fix's clothes.
/// </para>
/// </remarks>
public sealed record ChangeFeedIdentity(string Id)
{
    /// <summary>A fresh identity. Uniqueness only has to hold among the hosts sharing one backend.</summary>
    public static ChangeFeedIdentity New() => new(Guid.NewGuid().ToString("N"));
}

/// <summary>
/// How a mutation tells the other instances that something changed (#279).
/// </summary>
/// <remarks>
/// <para>
/// One announcement per backend, shared by all three kinds of persisted state, so the reload path is the
/// same one the stub feed has been exercising since G16e/f rather than a parallel mechanism per kind.
/// Emitting is unconditional: a <c>NOTIFY</c> nobody is listening on, and a publish with no subscribers,
/// are both cheap no-ops, so a provider never has to know whether the change feed is enabled.
/// </para>
/// <para>
/// It lives here rather than on the stub providers because it stopped being about stubs — an environment
/// key and a sandbox document announce on the same channel, and a constant named for one of the three
/// would be a comment that lies.
/// </para>
/// </remarks>
internal static class ChangeFeedAnnouncement
{
    /// <summary>Announces on an already-open connection, carrying the writer's identity as the payload.</summary>
    public static void Postgres(NpgsqlConnection connection, ChangeFeedIdentity? writer)
    {
        // pg_notify rather than NOTIFY so the payload is a parameter: the identity never becomes SQL text.
        using var notify = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);
        notify.Parameters.AddWithValue("channel", PostgresStubPersistence.ChangeChannel);
        notify.Parameters.AddWithValue("payload", writer?.Id ?? string.Empty);
        notify.ExecuteNonQuery();
    }

    /// <summary>Announces on the shared multiplexer, carrying the writer's identity as the message.</summary>
    public static void Redis(IConnectionMultiplexer redis, ChangeFeedIdentity? writer) =>
        redis.GetSubscriber().Publish(RedisStubPersistence.ChangeChannel, writer?.Id ?? string.Empty);

    /// <summary>
    /// Whether an announcement carrying <paramref name="payload"/> is this host's own, and so already
    /// reflected in memory. An empty payload is somebody who did not identify themselves — always reload,
    /// since missing a real change is the worse failure.
    /// </summary>
    public static bool IsOwn(string? payload, ChangeFeedIdentity self) =>
        !string.IsNullOrEmpty(payload) && string.Equals(payload, self.Id, StringComparison.Ordinal);
}
