using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for the tenant clock store (#290): tenant isolation, the resolver the serve
/// path reads, and the equivalence of "set real time" and "clear". The real clock is injected so the
/// assertions are exact instants rather than "about now".
/// </summary>
public sealed class ClockStoreTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");
    private static readonly DateTimeOffset HostNow = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Frozen = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (InMemoryClockStore Store, FixedClock Clock) NewStore()
    {
        var clock = new FixedClock(HostNow);
        return (new InMemoryClockStore(clock), clock);
    }

    [Fact]
    public void An_untouched_tenant_is_on_real_time()
    {
        var (store, _) = NewStore();

        Assert.True(store.Get(Acme).IsRealTime);
        Assert.Equal(HostNow, store.UtcNow(Acme));
    }

    [Fact]
    public void A_frozen_tenant_stops_while_the_host_keeps_going()
    {
        var (store, clock) = NewStore();
        store.Set(Acme, new ClockOverride(Frozen, TimeSpan.Zero));

        clock.Now = HostNow.AddHours(5);

        Assert.Equal(Frozen, store.UtcNow(Acme));
    }

    [Fact]
    public void An_offset_tenant_moves_with_the_host()
    {
        var (store, clock) = NewStore();
        store.Set(Acme, new ClockOverride(null, TimeSpan.FromDays(1)));

        Assert.Equal(HostNow.AddDays(1), store.UtcNow(Acme));
        clock.Now = HostNow.AddMinutes(10);
        Assert.Equal(HostNow.AddMinutes(10).AddDays(1), store.UtcNow(Acme));
    }

    [Fact]
    public void One_tenant_travelling_in_time_does_not_move_another()
    {
        var (store, _) = NewStore();

        store.Set(Acme, new ClockOverride(Frozen, TimeSpan.Zero));

        // The invariant the oracle cannot check and the one that makes a shared host safe: parallel
        // suites take a tenant each, and one freezing its clock must not reschedule everybody else.
        Assert.Equal(Frozen, store.UtcNow(Acme));
        Assert.Equal(HostNow, store.UtcNow(Globex));
        Assert.True(store.Get(Globex).IsRealTime);
    }

    [Fact]
    public void Clearing_returns_the_tenant_to_the_host_clock()
    {
        var (store, _) = NewStore();
        store.Set(Acme, new ClockOverride(Frozen, TimeSpan.Zero));

        store.Clear(Acme);

        Assert.True(store.Get(Acme).IsRealTime);
        Assert.Equal(HostNow, store.UtcNow(Acme));
    }

    [Fact]
    public void Setting_real_time_is_the_same_as_clearing()
    {
        var (store, _) = NewStore();
        store.Set(Acme, new ClockOverride(Frozen, TimeSpan.Zero));

        store.Set(Acme, ClockOverride.RealTime);

        // A client that always PUTs its whole configuration would otherwise leave the tenant marked as
        // overridden while behaving exactly like every other tenant — a difference visible on GET and
        // nowhere else, which is the kind that wastes an afternoon.
        Assert.True(store.Get(Acme).IsRealTime);
        Assert.Equal(HostNow, store.UtcNow(Acme));
    }

    [Fact]
    public void Real_time_never_accumulates_an_entry()
    {
        var (store, _) = NewStore();

        store.Set(Acme, ClockOverride.RealTime);
        store.Set(Globex, ClockOverride.RealTime);

        // Behaviourally a stored real-time override is indistinguishable from none, which is exactly why
        // this needs asserting: a host serving thousands of tenants would otherwise grow a dictionary
        // entry per tenant that only ever said "nothing special here".
        Assert.Equal(0, store.OverrideCount);

        store.Set(Acme, new ClockOverride(Frozen, TimeSpan.Zero));
        Assert.Equal(1, store.OverrideCount);

        store.Clear(Acme);
        Assert.Equal(0, store.OverrideCount);
    }

    [Fact]
    public void Clearing_a_tenant_that_never_set_one_is_a_no_op()
    {
        var (store, _) = NewStore();
        store.Set(Globex, new ClockOverride(Frozen, TimeSpan.Zero));

        store.Clear(Acme);

        Assert.Equal(Frozen, store.UtcNow(Globex));
    }

    [Fact]
    public void A_later_set_replaces_an_earlier_one()
    {
        var (store, _) = NewStore();
        store.Set(Acme, new ClockOverride(Frozen, TimeSpan.Zero));

        store.Set(Acme, new ClockOverride(null, TimeSpan.FromHours(2)));

        Assert.Null(store.Get(Acme).FrozenAt);
        Assert.Equal(HostNow.AddHours(2), store.UtcNow(Acme));
    }
}
