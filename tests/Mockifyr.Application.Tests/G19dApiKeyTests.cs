// The quota path moved to RateLimits/IRateCounter (#354); FixedWindowRateLimiter is deprecated but
// still shipped, so its behaviour stays covered until it is removed at the next major version.
#pragma warning disable CS0618
using System.Text;
using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for the sandbox-access building blocks (G19d, ADR 0011 addendum): key
/// material (CSPRNG shape, constant-time verification, display prefix), the fixed-window rate
/// limiter at its exact boundaries (including a genuinely parallel run), and the persistence
/// round-trip that makes an issued credential survive a restart.
/// </summary>
public sealed class G19dApiKeyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 27, 14, 30, 0, TimeSpan.Zero);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---- Key material -------------------------------------------------------------------------

    [Fact]
    public void Generated_tokens_are_prefixed_high_entropy_and_unique()
    {
        var (token, salt, hash) = ApiKeyMaterial.Generate();
        var (second, _, _) = ApiKeyMaterial.Generate();

        Assert.StartsWith("mfk_", token);
        // 32 random bytes in trimmed base64url: 4 + 43 characters.
        Assert.Equal(47, token.Length);
        Assert.NotEqual(token, second);
        Assert.False(string.IsNullOrEmpty(salt));
        Assert.Equal(32, Convert.FromBase64String(hash).Length);
    }

    [Fact]
    public void Verification_accepts_the_token_and_rejects_everything_else()
    {
        var (token, salt, hash) = ApiKeyMaterial.Generate();

        Assert.True(ApiKeyMaterial.Verify(token, salt, hash));
        Assert.False(ApiKeyMaterial.Verify(token + "x", salt, hash));
        Assert.False(ApiKeyMaterial.Verify("mfk_garbled", salt, hash));
        Assert.False(ApiKeyMaterial.Verify("", salt, hash));

        // The same token under a DIFFERENT salt must not verify — the salt is load-bearing.
        var (_, otherSalt, _) = ApiKeyMaterial.Generate();
        Assert.False(ApiKeyMaterial.Verify(token, otherSalt, hash));
    }

    [Fact]
    public void The_hash_format_is_a_stable_contract()
    {
        // Persisted hashes must keep verifying across versions, so the format is pinned here:
        // base64(SHA256(salt + "\n" + token)). The newline is load-bearing domain separation —
        // without it, ("ab","c") and ("a","bc") would collide.
        const string salt = "c2FsdA==";
        const string token = "mfk_pinned-token";
        var expected = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(salt + "\n" + token)));

        Assert.True(ApiKeyMaterial.Verify(token, salt, expected));
    }

    [Fact]
    public void The_display_prefix_is_the_first_twelve_characters()
    {
        var (token, _, _) = ApiKeyMaterial.Generate();

        Assert.Equal(token[..12], ApiKeyMaterial.DisplayPrefix(token));
        Assert.Equal("short", ApiKeyMaterial.DisplayPrefix("short"));
    }

    // ---- Fixed-window rate limiter --------------------------------------------------------------

    [Fact]
    public void The_quota_boundary_is_exact_and_reports_honest_headers()
    {
        var limiter = new FixedWindowRateLimiter(new TestClock());

        for (var i = 1; i <= 3; i++)
        {
            var decision = limiter.Count("k", quotaPerHour: 3);
            Assert.True(decision.Allowed);
            Assert.Equal(3, decision.Limit);
            Assert.Equal(3 - i, decision.Remaining);
            Assert.Equal(T0.AddMinutes(30), decision.ResetAt);
        }

        var refused = limiter.Count("k", quotaPerHour: 3);
        Assert.False(refused.Allowed);
        Assert.Equal(0, refused.Remaining);
        Assert.Equal(3, limiter.Used("k"));
    }

    [Fact]
    public void Unlimited_keys_are_always_allowed_and_windows_reset_on_the_hour()
    {
        var clock = new TestClock();
        var limiter = new FixedWindowRateLimiter(clock);

        var unlimited = limiter.Count("k", quotaPerHour: null);
        Assert.True(unlimited.Allowed);
        Assert.Equal(0, unlimited.Limit);

        // A zero (or negative) quota also means unlimited — never an instantly-exhausted key.
        Assert.True(limiter.Count("z", quotaPerHour: 0).Allowed);

        Assert.True(limiter.Count("q", 1).Allowed);
        Assert.False(limiter.Count("q", 1).Allowed);

        // Crossing the hour boundary opens a fresh window: the stale count is invisible to Used()
        // even before the next request arrives, and the next request is admitted.
        clock.Now = new DateTimeOffset(2026, 7, 27, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, limiter.Used("q"));
        Assert.True(limiter.Count("q", 1).Allowed);
        Assert.Equal(1, limiter.Used("q"));
    }

    [Fact]
    public void Keys_are_counted_independently()
    {
        var limiter = new FixedWindowRateLimiter(new TestClock());

        Assert.True(limiter.Count("a", 1).Allowed);
        Assert.True(limiter.Count("b", 1).Allowed);
        Assert.False(limiter.Count("a", 1).Allowed);
    }

    [Fact]
    public async Task Parallel_requests_across_the_boundary_never_exceed_the_budget()
    {
        var limiter = new FixedWindowRateLimiter(new TestClock());
        var allowed = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                if (limiter.Count("hot", quotaPerHour: 100).Allowed)
                {
                    Interlocked.Increment(ref allowed);
                }
            }
        })));

        Assert.Equal(100, allowed);
        Assert.Equal(100, limiter.Used("hot"));
    }

    // ---- Persistence round-trips ----------------------------------------------------------------

    private static ApiKey SampleKey(string id = "id-1") =>
        new(id, new TenantId("acme"), "ci-key", "c2FsdA==", Convert.ToBase64String(new byte[32]),
            "mfk_ab12cd34", T0, QuotaPerHour: 100);

    [Fact]
    public void The_file_backend_round_trips_and_skips_garbage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mockifyr-apikeys-" + Guid.NewGuid().ToString("N"));
        try
        {
            var persistence = new FileSystemApiKeyPersistence(directory);
            persistence.Save(SampleKey());
            persistence.Save(SampleKey("id-2"));
            File.WriteAllText(Path.Combine(directory, "junk.json"), "{not json");

            var loaded = new FileSystemApiKeyPersistence(directory).LoadAll();
            Assert.Equal(2, loaded.Count);
            var key = loaded.Single(k => k.Id == "id-1");
            Assert.Equal((new TenantId("acme"), "ci-key", "mfk_ab12cd34", 100), (key.Tenant, key.Name, key.Prefix, key.QuotaPerHour));

            persistence.Remove("id-1");
            Assert.Single(new FileSystemApiKeyPersistence(directory).LoadAll());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void The_litedb_backend_round_trips()
    {
        var file = Path.Combine(Path.GetTempPath(), "mockifyr-apikeys-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var database = new LiteDB.LiteDatabase(file))
            {
                var persistence = new LiteDbApiKeyPersistence(database);
                persistence.Save(SampleKey());
                persistence.Save(SampleKey("id-2"));
                persistence.Remove("id-2");
            }

            using var reopened = new LiteDB.LiteDatabase(file);
            var loaded = new LiteDbApiKeyPersistence(reopened).LoadAll();
            Assert.Equal("id-1", Assert.Single(loaded).Id);
        }
        finally
        {
            File.Delete(file);
        }
    }
    // ---- a renameable token marker (#396d) -------------------------------------------------------

    [Fact]
    public void The_default_prefix_and_fragment_are_exactly_what_shipped()
    {
        var (token, _, _) = ApiKeyMaterial.Generate();

        Assert.StartsWith("mfk_", token, StringComparison.Ordinal);
        // Four characters of marker plus eight of randomness — the twelve every stored fragment has.
        Assert.Equal(12, ApiKeyMaterial.DisplayPrefix(token).Length);
    }

    [Fact]
    public void A_configured_prefix_marks_new_tokens()
    {
        var (token, _, _) = ApiKeyMaterial.Generate("dfx_");

        Assert.StartsWith("dfx_", token, StringComparison.Ordinal);
        Assert.DoesNotContain("mfk_", token, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_issued_under_the_old_prefix_still_verifies_after_a_rename()
    {
        // The property that makes this safe to change at all: verification hashes the whole presented
        // token and never inspects the marker, so a partner holding an mfk_ key keeps working when the
        // host is reconfigured. Without it, renaming would mean a re-issue campaign.
        var (old, salt, hash) = ApiKeyMaterial.Generate("mfk_");

        Assert.True(ApiKeyMaterial.Verify(old, salt, hash));
    }

    [Fact]
    public void A_longer_prefix_does_not_eat_into_the_fragment()
    {
        // The fragment is how an operator tells two keys apart in a list. Counted from the start of
        // the token, a ten-character marker would leave two random characters and two keys could show
        // the same fragment — so it is counted from the random part instead.
        var (token, _, _) = ApiKeyMaterial.Generate("dfxsandbox_");
        var fragment = ApiKeyMaterial.DisplayPrefix(token, "dfxsandbox_");

        Assert.StartsWith("dfxsandbox_", fragment, StringComparison.Ordinal);
        Assert.Equal("dfxsandbox_".Length + 8, fragment.Length);
    }

    [Theory]
    [InlineData("mfk_")]
    [InlineData("dfx_")]
    [InlineData("k")]
    // Every boundary of every range in one prefix: a and z, A and Z, 0 and 9. Without it the six
    // comparisons could each be off by one and every test would still pass.
    [InlineData("azAZ09")]
    [InlineData("Partner-Key_")]    // exactly 12 — the longest that is still a marker
    public void Prefix_validation_admits_token_characters(string prefix)
    {
        Assert.True(ApiKeyMaterial.IsWellFormedPrefix(prefix));
    }

    [Fact]
    public void A_token_no_longer_than_its_fragment_is_returned_whole()
    {
        // The degenerate case the Min guards: nothing to trim.
        Assert.Equal("mfk_", ApiKeyMaterial.DisplayPrefix("mfk_"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dfx key_")]        // a quoted flag with a space in it
    [InlineData("dfx.")]            // '.' is not a token character here
    [InlineData("dfx/")]
    [InlineData("anahtar_öneki")]   // non-ASCII
    [InlineData("aaaaaaaaaaaaa")]   // 13 characters: a marker, not a token
    public void Prefix_validation_refuses_the_rest(string prefix)
    {
        Assert.False(ApiKeyMaterial.IsWellFormedPrefix(prefix));
    }

}
#pragma warning restore CS0618
