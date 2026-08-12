using System.Security.Cryptography;
using System.Text;

namespace Mockifyr.Core;

/// <summary>
/// A response kept so a retried write can be replayed rather than re-run (#358).
/// </summary>
/// <remarks>
/// The fingerprint is what makes reuse safe: every payment API that accepts an idempotency key also
/// refuses to reuse one for a different request, because the alternative is answering a caller with
/// somebody else's payment.
/// </remarks>
public sealed record IdempotentResponse(
    string Fingerprint,
    int Status,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    DateTimeOffset StoredAt);

/// <summary>
/// Tenant-scoped store of replayable responses, bounded and time-limited (#358).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>The stored response for this key, or null when there is none inside the window.</summary>
    IdempotentResponse? Get(TenantId tenant, string key, DateTimeOffset now);

    /// <summary>Remembers a response against a key.</summary>
    void Put(TenantId tenant, string key, IdempotentResponse response, DateTimeOffset now);
}

/// <summary>
/// What a request carrying an <c>Idempotency-Key</c> should do (#358).
/// </summary>
public enum IdempotencyOutcome
{
    /// <summary>Nothing stored under this key: serve normally and remember the answer.</summary>
    Fresh = 0,

    /// <summary>The same request under the same key: replay the stored response.</summary>
    Replay = 1,

    /// <summary>A different request under the same key: refuse rather than answer somebody else's.</summary>
    Conflict = 2,
}

/// <summary>
/// The pure rules of idempotent replay (#358).
/// </summary>
/// <remarks>
/// <para>
/// The gap this closes: every payment API a partner integrates against accepts an
/// <c>Idempotency-Key</c> on writes, and their client library sends one and retries on timeouts. A
/// sandbox that ignores it creates a second payment on a retry — behaviour the partner's production
/// integration is built specifically never to see, and which looks like their bug.
/// </para>
/// <para>
/// Only unsafe methods take part. A GET carrying the header is served normally: replaying reads would
/// hide state the caller is asking about, and no API this stands in for does it.
/// </para>
/// </remarks>
public static class Idempotency
{
    /// <summary>The header every one of these APIs uses.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>How long a stored response stays replayable by default.</summary>
    /// <remarks>
    /// Twenty-four hours is what the APIs this stands in for publish, and matching them is the whole
    /// point — a window a partner's client does not expect is its own surprise.
    /// </remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

    /// <summary>The longest key this will accept; longer is refused rather than truncated.</summary>
    public const int MaxKeyLength = 255;

    /// <summary>Whether this method's requests may be replayed at all.</summary>
    public static bool AppliesTo(string method) =>
        !(string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Identifies the request a key was used with, so reusing the key for a different one is
    /// detectable.
    /// </summary>
    /// <remarks>
    /// Method, path, query and body — the things that decide what a write does. Headers are excluded:
    /// a client that retries with a fresh trace id or a refreshed token is making the same request,
    /// and treating it as a different one would turn every real retry into a conflict.
    /// </remarks>
    public static string Fingerprint(string method, string path, string query, byte[] body)
    {
        var head = Encoding.UTF8.GetBytes($"{method.ToUpperInvariant()}\n{path}\n{query}\n");
        var buffer = new byte[head.Length + body.Length];
        head.CopyTo(buffer, 0);
        body.CopyTo(buffer, head.Length);
        return Convert.ToHexString(SHA256.HashData(buffer));
    }

    /// <summary>Whether a presented key is usable at all.</summary>
    public static bool IsWellFormed(string? key) =>
        key is { Length: > 0 and <= MaxKeyLength } && !key.Any(char.IsControl);

    /// <summary>What to do with a request whose key resolved to <paramref name="stored"/>.</summary>
    public static IdempotencyOutcome Decide(IdempotentResponse? stored, string fingerprint) =>
        stored is null ? IdempotencyOutcome.Fresh
        : string.Equals(stored.Fingerprint, fingerprint, StringComparison.Ordinal)
            ? IdempotencyOutcome.Replay
            : IdempotencyOutcome.Conflict;
}

/// <summary>
/// Whether this host replays retried writes by default, and for how long (#358).
/// </summary>
/// <remarks>
/// Off unless an operator asks: a sandbox that quietly stopped creating a second payment would be a
/// behaviour change nobody opted into, and some suites exist precisely to test double submission.
/// </remarks>
public sealed record IdempotencyOptions(bool Enabled, TimeSpan Window);

/// <summary>
/// The default store: in-process, per tenant, bounded and expiring (#358).
/// </summary>
/// <remarks>
/// Bounded by count as well as by time, because a window alone is not a bound: a caller sending a
/// fresh key per request would otherwise hold a day's traffic in memory. Beyond
/// <see cref="Capacity"/> the oldest entry goes, which is the message inbox's ethos — an unattended
/// host cannot grow without limit.
/// </remarks>
public sealed class InMemoryIdempotencyStore(TimeSpan? window = null, int capacity = 10_000) : IIdempotencyStore
{
    private readonly Dictionary<(TenantId Tenant, string Key), IdempotentResponse> _stored = [];
    private readonly LinkedList<(TenantId Tenant, string Key)> _order = new();
    private readonly Lock _gate = new();

    /// <summary>How long a stored response stays replayable.</summary>
    public TimeSpan Window { get; } = window is { Ticks: > 0 } configured ? configured : Idempotency.DefaultWindow;

    /// <summary>How many responses are kept across all tenants.</summary>
    public int Capacity { get; } = capacity > 0 ? capacity : 10_000;

    /// <inheritdoc />
    public IdempotentResponse? Get(TenantId tenant, string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_stored.TryGetValue((tenant, key), out var response))
            {
                return null;
            }

            if (now - response.StoredAt >= Window)
            {
                // Dropped on read rather than swept on a timer: the entry is here, we already know it
                // is stale, and a background sweep would be a thread doing what this line does free.
                _stored.Remove((tenant, key));
                _order.Remove((tenant, key));
                return null;
            }

            return response;
        }
    }

    /// <inheritdoc />
    public void Put(TenantId tenant, string key, IdempotentResponse response, DateTimeOffset now)
    {
        lock (_gate)
        {
            var slot = (tenant, key);
            if (!_stored.ContainsKey(slot))
            {
                _order.AddLast(slot);
            }

            _stored[slot] = response;

            while (_order.Count > Capacity && _order.First is { } oldest)
            {
                _stored.Remove(oldest.Value);
                _order.RemoveFirst();
            }
        }
    }
}
