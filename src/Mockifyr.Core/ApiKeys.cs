using System.Security.Cryptography;

namespace Mockifyr.Core;

/// <summary>
/// An operator-issued sandbox credential (G19d, ADR 0011): it scopes traffic to a tenant — nothing
/// more (no admin access, ever). Only the salted hash is stored; the token itself is shown exactly
/// once at issue time, and after that the <see cref="Prefix"/> is the only identifying fragment
/// that appears anywhere.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle fields (#355) are all optional and default to what a key issued before them meant:
/// no expiry, not revoked, full access. A row written by an older version therefore reads back
/// unchanged on every one of the four persistence providers, which is the whole reason they are
/// optional rather than required.
/// </para>
/// </remarks>
public sealed record ApiKey(
    string Id,
    TenantId Tenant,
    string Name,
    string Salt,
    string Hash,
    string Prefix,
    DateTimeOffset CreatedAt,
    int? QuotaPerHour,
    DateTimeOffset? ExpiresAt = null,
    ApiKeyRevocation? Revocation = null,
    ApiKeyScope Scope = ApiKeyScope.ReadWrite)
{
    /// <summary>Whether this key may still be used at <paramref name="now"/>, and if not, why.</summary>
    /// <remarks>
    /// Why the reason travels with the answer: "expired" and "unknown" send an integrator to entirely
    /// different places — one re-reads their config, the other asks for a new credential — and a
    /// single 401 for both costs a support round trip every time.
    /// </remarks>
    public ApiKeyStatus StatusAt(DateTimeOffset now) =>
        Revocation is not null ? ApiKeyStatus.Revoked
        : ExpiresAt is { } expiry && now >= expiry ? ApiKeyStatus.Expired
        : ApiKeyStatus.Active;
}

/// <summary>Why a key was withdrawn, and by whom (#355).</summary>
/// <remarks>
/// Revocation is a state rather than a delete: the audit trail could otherwise show a key being used
/// and then not, without ever showing the decision that ended it — and "when did we turn this off,
/// and who decided?" is the first question asked after an incident.
/// </remarks>
public sealed record ApiKeyRevocation(DateTimeOffset At, string By, string? Reason = null);

/// <summary>Whether a key may change anything, or only read (#355).</summary>
public enum ApiKeyScope
{
    /// <summary>Full access to the key's tenant — what every key issued before #355 had.</summary>
    ReadWrite = 0,

    /// <summary>
    /// Safe methods only. GET/HEAD/OPTIONS pass; anything else is refused before it reaches a stub.
    /// </summary>
    /// <remarks>
    /// The rule is the HTTP method, not the effect: a stub can be written whose GET mutates sandbox
    /// state through the <c>state</c> directive, and this will not stop it. Method-based is the rule a
    /// gateway states and an integrator can predict; an effect-based one would have to read a
    /// response template to answer whether a request is allowed, which is not a rule anybody can hold.
    /// </remarks>
    ReadOnly = 1,
}

/// <summary>Whether a presented key is usable, and if not, why (#355).</summary>
public enum ApiKeyStatus
{
    /// <summary>Usable.</summary>
    Active = 0,

    /// <summary>Past its <see cref="ApiKey.ExpiresAt"/>.</summary>
    Expired = 1,

    /// <summary>Withdrawn by an operator.</summary>
    Revoked = 2,
}

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
    /// <summary>The historical token marker, and the default.</summary>
    public const string DefaultTokenPrefix = "mfk_";

    /// <summary>
    /// How many characters of the random part survive in the display fragment.
    /// </summary>
    /// <remarks>
    /// Counted from the random part rather than from the start of the token (#396). The fragment is
    /// how an operator tells two keys apart in a list, so a longer prefix must not eat into it — with
    /// a fixed total length, a ten-character prefix would leave two random characters and two keys
    /// could show the same fragment. The default prefix is four characters, so the total stays twelve
    /// and every stored fragment reads exactly as it did.
    /// </remarks>
    public const int DisplayRandomLength = 8;

    /// <summary>
    /// Whether a prefix can be used. Token characters only, and short enough to stay a marker rather
    /// than become the token.
    /// </summary>
    public static bool IsWellFormedPrefix(string prefix)
    {
        if (prefix.Length is 0 or > 12)
        {
            return false;
        }

        foreach (var c in prefix)
        {
            var legal = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c is '_' or '-';
            if (!legal)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Mints a token plus the salt/hash pair to store. The token is never persisted.</summary>
    public static (string Token, string Salt, string Hash) Generate(string? prefix = null)
    {
        var token = (prefix ?? DefaultTokenPrefix) + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        return (token, salt, ComputeHash(salt, token));
    }

    /// <summary>The display fragment (<c>mfk_ab12cd34</c>) — the only part shown after issuance.</summary>
    public static string DisplayPrefix(string token, string? prefix = null) =>
        // Min rather than a length test: a token exactly as long as the fragment makes both branches
        // of the test return the same string, so the branch is an equivalent mutant waiting to happen.
        token[..Math.Min((prefix ?? DefaultTokenPrefix).Length + DisplayRandomLength, token.Length)];

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

/// <summary>
/// The marker every issued sandbox token starts with (#396). Configurable for the same reason the
/// tenant header is: a partner sees it in their own configuration, and it is our product's initials.
/// </summary>
/// <remarks>
/// Only <em>newly issued</em> tokens are affected. Verification hashes the whole presented token and
/// compares it to what was stored, so it never inspects the prefix — which means keys issued before a
/// rename keep working, and an operator can change this without a re-issue campaign.
/// </remarks>
public sealed record ApiKeyOptions
{
    /// <summary>The marker new tokens start with.</summary>
    public string TokenPrefix { get; init; } = ApiKeyMaterial.DefaultTokenPrefix;

    /// <summary>An unconfigured host.</summary>
    public static ApiKeyOptions Default { get; } = new();
}

/// <summary>What the rate limiter decided for one request.</summary>
public sealed record QuotaDecision(bool Allowed, int Limit, int Remaining, DateTimeOffset ResetAt);

/// <summary>
/// Fixed-window per-key rate limiter (G19d, ADR 0011 addendum): the hour containing the request is
/// the window, the counter increments atomically under one lock, and the boundary is exact — N
/// parallel requests across the limit never admit more than the budget. Usage is in-memory by
/// design (counters reset on restart; the keys themselves persist).
/// </summary>
[Obsolete("Superseded by RateLimits.Count over an IRateCounter (#354), which counts the same fixed "
    + "window through a shared counter so two replicas enforce one budget. Kept for source compatibility; "
    + "it is no longer on the serving path and will be removed in the next major version.")]
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
