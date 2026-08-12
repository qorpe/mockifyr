using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Idempotent replay (#358): the rules that decide whether a retried write runs again.
/// </summary>
public sealed class IdempotencyTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    private static IdempotentResponse Stored(string fingerprint, DateTimeOffset? at = null) =>
        new(fingerprint, 201, [], "{}"u8.ToArray(), at ?? Now);

    [Theory]
    [InlineData("POST", true)]
    [InlineData("PUT", true)]
    [InlineData("PATCH", true)]
    [InlineData("DELETE", true)]
    [InlineData("GET", false)]
    [InlineData("head", false)]
    [InlineData("OPTIONS", false)]
    public void Only_unsafe_methods_replay(string method, bool applies)
    {
        // Replaying a read would hide the state the caller is asking about, and no API this stands in
        // for does it.
        Assert.Equal(applies, Idempotency.AppliesTo(method));
    }

    [Fact]
    public void The_same_request_under_the_same_key_replays()
    {
        var print = Idempotency.Fingerprint("POST", "/payments", "", "{\"amount\":10}"u8.ToArray());

        Assert.Equal(IdempotencyOutcome.Replay, Idempotency.Decide(Stored(print), print));
    }

    [Fact]
    public void A_different_request_under_the_same_key_is_a_conflict()
    {
        // The alternative is answering a caller with somebody else's payment.
        var first = Idempotency.Fingerprint("POST", "/payments", "", "{\"amount\":10}"u8.ToArray());
        var second = Idempotency.Fingerprint("POST", "/payments", "", "{\"amount\":99}"u8.ToArray());

        Assert.Equal(IdempotencyOutcome.Conflict, Idempotency.Decide(Stored(first), second));
    }

    [Fact]
    public void Nothing_stored_means_serve_it()
    {
        Assert.Equal(IdempotencyOutcome.Fresh, Idempotency.Decide(null, "whatever"));
    }

    [Fact]
    public void The_path_and_query_are_part_of_what_a_key_was_used_with()
    {
        var body = "{}"u8.ToArray();

        Assert.NotEqual(
            Idempotency.Fingerprint("POST", "/payments", "", body),
            Idempotency.Fingerprint("POST", "/refunds", "", body));
        Assert.NotEqual(
            Idempotency.Fingerprint("POST", "/payments", "?live=1", body),
            Idempotency.Fingerprint("POST", "/payments", "?live=0", body));
        Assert.NotEqual(
            Idempotency.Fingerprint("POST", "/payments", "", body),
            Idempotency.Fingerprint("PUT", "/payments", "", body));
    }

    [Fact]
    public void The_method_is_compared_without_regard_to_case()
    {
        var body = "{}"u8.ToArray();

        Assert.Equal(
            Idempotency.Fingerprint("post", "/payments", "", body),
            Idempotency.Fingerprint("POST", "/payments", "", body));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("key-1", true)]
    [InlineData("has\tcontrol", false)]
    public void A_key_must_be_usable_before_it_is_used(string? key, bool wellFormed)
    {
        Assert.Equal(wellFormed, Idempotency.IsWellFormed(key));
    }

    [Fact]
    public void A_key_longer_than_the_bound_is_refused_rather_than_truncated()
    {
        // Truncating would make two different keys the same key, which is the one failure this whole
        // mechanism exists to prevent.
        Assert.True(Idempotency.IsWellFormed(new string('k', Idempotency.MaxKeyLength)));
        Assert.False(Idempotency.IsWellFormed(new string('k', Idempotency.MaxKeyLength + 1)));
    }

    [Fact]
    public void A_stored_response_stops_replaying_once_the_window_passes()
    {
        var store = new InMemoryIdempotencyStore(window: TimeSpan.FromHours(1));
        store.Put(Acme, "key-1", Stored("print"), Now);

        Assert.NotNull(store.Get(Acme, "key-1", Now.AddMinutes(59)));
        Assert.Null(store.Get(Acme, "key-1", Now.AddHours(1)));
    }

    [Fact]
    public void One_tenants_key_is_not_another_tenants()
    {
        var store = new InMemoryIdempotencyStore();
        store.Put(Acme, "key-1", Stored("print"), Now);

        Assert.Null(store.Get(new TenantId("other"), "key-1", Now));
    }

    [Fact]
    public void The_store_stays_bounded_when_every_request_brings_a_fresh_key()
    {
        // A window alone is not a bound: a day of unique keys would otherwise sit in memory.
        var store = new InMemoryIdempotencyStore(capacity: 10);
        for (var i = 0; i < 50; i++)
        {
            store.Put(Acme, $"key-{i}", Stored("print"), Now);
        }

        Assert.Null(store.Get(Acme, "key-0", Now));
        Assert.NotNull(store.Get(Acme, "key-49", Now));
    }

    [Fact]
    public void Re_storing_a_key_does_not_count_against_the_bound_twice()
    {
        var store = new InMemoryIdempotencyStore(capacity: 2);
        store.Put(Acme, "key-1", Stored("a"), Now);
        store.Put(Acme, "key-1", Stored("b"), Now);
        store.Put(Acme, "key-2", Stored("c"), Now);

        Assert.NotNull(store.Get(Acme, "key-1", Now));
        Assert.NotNull(store.Get(Acme, "key-2", Now));
    }

    [Fact]
    public void A_tenants_own_answer_beats_the_host_default_in_both_directions()
    {
        // On a shared host one team testing double submission has to be able to keep it off while the
        // partner beside them keeps it on.
        var declared = new TenantRecord(Acme, "Acme", Now, Idempotency: false);
        var opted = new TenantRecord(Acme, "Acme", Now, Idempotency: true);

        Assert.False(TenantIdempotency.EnabledFor(declared, hostDefault: true));
        Assert.True(TenantIdempotency.EnabledFor(opted, hostDefault: false));
        Assert.True(TenantIdempotency.EnabledFor(null, hostDefault: true));
        Assert.False(TenantIdempotency.EnabledFor(new TenantRecord(Acme, "Acme", Now), hostDefault: false));
    }

    [Fact]
    public void A_nonsensical_capacity_falls_back_to_the_default_rather_than_keeping_nothing()
    {
        // A store that silently kept zero entries would look exactly like a store that works, right up
        // until a retry created a second payment.
        var store = new InMemoryIdempotencyStore(capacity: 0);
        store.Put(Acme, "key-1", Stored("print"), Now);

        Assert.NotNull(store.Get(Acme, "key-1", Now));
    }

    [Fact]
    public void An_expired_entry_is_dropped_rather_than_left_to_be_re_read()
    {
        // Reading past the window must not leave the entry behind: a caller polling with an old key
        // would otherwise keep a dead response alive in memory for as long as they kept asking.
        var store = new InMemoryIdempotencyStore(window: TimeSpan.FromMinutes(1), capacity: 2);
        store.Put(Acme, "stale", Stored("print"), Now);

        Assert.Null(store.Get(Acme, "stale", Now.AddMinutes(2)));

        // Two fresh keys now fit inside a capacity of two, which they could not if the dead entry were
        // still occupying a slot.
        store.Put(Acme, "a", Stored("print", Now.AddMinutes(2)), Now.AddMinutes(2));
        store.Put(Acme, "b", Stored("print", Now.AddMinutes(2)), Now.AddMinutes(2));

        Assert.NotNull(store.Get(Acme, "a", Now.AddMinutes(2)));
        Assert.NotNull(store.Get(Acme, "b", Now.AddMinutes(2)));
    }
}
