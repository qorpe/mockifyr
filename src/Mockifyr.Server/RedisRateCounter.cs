using Mockifyr.Core;
using StackExchange.Redis;

namespace Mockifyr.Server;

/// <summary>
/// A request counter shared by every replica, backed by the Redis this host is already connected to
/// (#354).
/// </summary>
/// <remarks>
/// <para>
/// The reason this exists: an in-process counter behind two pods means a partner gets twice the quota
/// their key says, and a deploy forgives whatever they had spent. A quota that changes when we scale
/// is not a contract.
/// </para>
/// <para>
/// No new infrastructure — Redis is already one of this project's persistence providers, so this is a
/// second use of a connection an operator has already configured. Without <c>--redis</c> the in-memory
/// counter stays the default, because a laptop must not need a server to run a sandbox.
/// </para>
/// <para>
/// The counter is a plain <c>INCR</c> on a key that names the window's bucket, which is atomic across
/// clients — that atomicity is the whole point. The key is given a lifetime of twice the window so a
/// bucket nobody revisits disappears on its own; Redis is being used as a shared counter here, not as
/// a store, and nothing here needs to survive.
/// </para>
/// </remarks>
public sealed class RedisRateCounter(IConnectionMultiplexer redis) : IRateCounter
{
    private const string Prefix = "mockifyr:rate:";

    /// <inheritdoc />
    public int Increment(string key, RateWindow window, DateTimeOffset now)
    {
        var database = redis.GetDatabase();
        var bucket = (RedisKey)KeyFor(key, window, now);

        var count = database.StringIncrement(bucket);

        // Set on the first increment only: re-arming the expiry on every request would slide the
        // window forward and a busy caller would never see it reset.
        if (count == 1)
        {
            database.KeyExpire(bucket, window.Duration + window.Duration);
        }

        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    /// <inheritdoc />
    public int Peek(string key, RateWindow window, DateTimeOffset now)
    {
        // TryGetInt64 rather than long.TryParse: a RedisValue converts implicitly to both string and
        // ReadOnlySpan<byte>, so the parse call is ambiguous and the typed accessor says what is meant.
        var stored = redis.GetDatabase().StringGet(KeyFor(key, window, now));
        return stored.TryParse(out long value) && value <= int.MaxValue ? (int)value : 0;
    }

    private static string KeyFor(string key, RateWindow window, DateTimeOffset now) =>
        $"{Prefix}{key}:{(long)window.Duration.TotalSeconds}:{window.BucketFor(now)}";
}
