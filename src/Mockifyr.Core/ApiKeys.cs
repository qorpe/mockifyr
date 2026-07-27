using System.Security.Cryptography;

namespace Mockifyr.Core;

/// <summary>
/// An operator-issued sandbox credential (G19d, ADR 0011): it scopes traffic to a tenant — nothing
/// more (no admin access, ever). Only the salted hash is stored; the token itself is shown exactly
/// once at issue time, and after that the <see cref="Prefix"/> is the only identifying fragment
/// that appears anywhere.
/// </summary>
public sealed record ApiKey(
    string Id,
    TenantId Tenant,
    string Name,
    string Salt,
    string Hash,
    string Prefix,
    DateTimeOffset CreatedAt,
    int? QuotaPerHour);

/// <summary>
/// Host-wide API key store (G19d). Listing is tenant-scoped (the admin surface), but resolution
/// reads across tenants by design — the presented key is what SELECTS the tenant.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>Adds or replaces a key by id.</summary>
    void Put(ApiKey key);

    /// <summary>One key by id, or null.</summary>
    ApiKey? Get(string id);

    /// <summary>The tenant's keys, newest first.</summary>
    IReadOnlyList<ApiKey> GetKeys(TenantId tenant);

    /// <summary>Every key on the host — the resolution path's search space.</summary>
    IReadOnlyList<ApiKey> GetAll();

    /// <summary>Revokes a key; false when the id is unknown.</summary>
    bool Remove(string id);
}

/// <summary>
/// Durability seam for API keys (ADR 0011 addendum: a credential that vanishes on redeploy is not
/// a credential). Rides the same G16 backends as environments.
/// </summary>
public interface IApiKeyPersistence
{
    /// <summary>Persists a key (hash only — never the token).</summary>
    void Save(ApiKey key);

    /// <summary>Removes a revoked key.</summary>
    void Remove(string id);

    /// <summary>Loads every persisted key at startup.</summary>
    IReadOnlyList<ApiKey> LoadAll();
}

/// <summary>The no-op default: keys live only in memory (no durable backend configured).</summary>
public sealed class NullApiKeyPersistence : IApiKeyPersistence
{
    /// <inheritdoc />
    public void Save(ApiKey key)
    {
    }

    /// <inheritdoc />
    public void Remove(string id)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<ApiKey> LoadAll() => [];
}

/// <summary>Turns key-based tenant resolution on (the <c>--sandbox-auth</c> flag).</summary>
public sealed record SandboxAuthOptions(bool Enabled = false);

/// <summary>
/// Key material (G19d, ADR 0011 addendum): 256-bit CSPRNG tokens with a recognizable prefix,
/// stored only as a salted SHA-256, compared in constant time. Tokens are high-entropy, so a fast
/// salted hash (not a KDF) is the right trade — verification sits on the serving hot path.
/// </summary>
public static class ApiKeyMaterial
{
    /// <summary>Every token starts with this marker.</summary>
    public const string TokenPrefix = "mfk_";

    /// <summary>How many characters of the token survive as the display prefix.</summary>
    public const int DisplayPrefixLength = 12;

    /// <summary>Mints a token plus the salt/hash pair to store. The token is never persisted.</summary>
    public static (string Token, string Salt, string Hash) Generate()
    {
        var token = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        return (token, salt, ComputeHash(salt, token));
    }

    /// <summary>The display fragment (<c>mfk_ab12cd34</c>) — the only part shown after issuance.</summary>
    public static string DisplayPrefix(string token) =>
        token.Length <= DisplayPrefixLength ? token : token[..DisplayPrefixLength];

    /// <summary>Constant-time verification of a presented token against a stored salt/hash pair.</summary>
    public static bool Verify(string presentedToken, string salt, string hash)
    {
        var computed = Convert.FromBase64String(ComputeHash(salt, presentedToken));
        var stored = Convert.FromBase64String(hash);
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    private static string ComputeHash(string salt, string token) =>
        Convert.ToBase64String(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(salt + "\n" + token)));
}

/// <summary>What the rate limiter decided for one request.</summary>
public sealed record QuotaDecision(bool Allowed, int Limit, int Remaining, DateTimeOffset ResetAt);

/// <summary>
/// Fixed-window per-key rate limiter (G19d, ADR 0011 addendum): the hour containing the request is
/// the window, the counter increments atomically under one lock, and the boundary is exact — N
/// parallel requests across the limit never admit more than the budget. Usage is in-memory by
/// design (counters reset on restart; the keys themselves persist).
/// </summary>
public sealed class FixedWindowRateLimiter(TimeProvider? clock = null)
{
    private readonly Dictionary<string, (DateTimeOffset WindowStart, int Count)> _windows = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();

    /// <summary>
    /// Counts one request against the key. A null <paramref name="quotaPerHour"/> is unlimited
    /// (always allowed, reported as limit 0). The first request past the budget flips to refused.
    /// </summary>
    public QuotaDecision Count(string keyId, int? quotaPerHour)
    {
        var now = _clock.GetUtcNow();
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        var resetAt = windowStart.AddHours(1);

        // The out-var stays a standalone statement (not folded into the condition) so Stryker's
        // condition mutants keep compiling — the pattern learned on EnvironmentJsonReader.
        var limit = quotaPerHour ?? 0;
        if (limit <= 0)
        {
            return new QuotaDecision(Allowed: true, Limit: 0, Remaining: 0, resetAt);
        }

        lock (_gate)
        {
            var hasWindow = _windows.TryGetValue(keyId, out var window);
            if (!hasWindow || window.WindowStart != windowStart)
            {
                window = (windowStart, Count: 0);
            }

            if (window.Count >= limit)
            {
                _windows[keyId] = window;
                return new QuotaDecision(Allowed: false, limit, Remaining: 0, resetAt);
            }

            window = (windowStart, window.Count + 1);
            _windows[keyId] = window;
            return new QuotaDecision(Allowed: true, limit, Remaining: limit - window.Count, resetAt);
        }
    }

    /// <summary>How many requests the key has used in the current window (0 when none).</summary>
    public int Used(string keyId)
    {
        var now = _clock.GetUtcNow();
        var windowStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        lock (_gate)
        {
            var hasWindow = _windows.TryGetValue(keyId, out var window);
            return hasWindow && window.WindowStart == windowStart ? window.Count : 0;
        }
    }
}
