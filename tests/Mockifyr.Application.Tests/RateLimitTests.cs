using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Multi-window quota (#354): which limit binds, what the caller is told, and the property that makes
/// a shared counter worth having — the same number behind two replicas as behind one.
/// </summary>
public sealed class RateLimitTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 10, 30, 15, TimeSpan.Zero);

    private static RateWindow Minute(int limit) => new(TimeSpan.FromMinutes(1), limit);

    private static RateWindow Hour(int limit) => new(TimeSpan.FromHours(1), limit);

    private static QuotaDecision Count(IRateCounter counter, params RateWindow[] windows) =>
        RateLimits.Count("key-1", windows, counter, Now);

    // ---- windows ---------------------------------------------------------------------------------

    [Fact]
    public void A_window_buckets_the_same_instant_the_same_way_wherever_it_runs()
    {
        // Aligned to the epoch, not to the first request: two hosts that started minutes apart have to
        // agree on which bucket an instant belongs to, or a shared counter is shared in name only.
        var window = Hour(10);

        Assert.Equal(window.BucketFor(Now), window.BucketFor(Now.AddMinutes(20)));
        Assert.NotEqual(window.BucketFor(Now), window.BucketFor(Now.AddHours(1)));
    }

    [Fact]
    public void A_window_reports_when_it_reopens()
    {
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 11, 0, 0, TimeSpan.Zero), Hour(10).ResetAt(Now));
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 10, 31, 0, TimeSpan.Zero), Minute(10).ResetAt(Now));
    }

    // ---- which limit binds -----------------------------------------------------------------------

    [Fact]
    public void With_nothing_configured_everything_is_allowed()
    {
        Assert.True(Count(new InMemoryRateCounter()).Allowed);
    }

    [Fact]
    public void The_tightest_window_is_reported_while_everything_is_still_allowed()
    {
        // The caller wants to know which limit they are about to meet, not the roomiest one.
        var counter = new InMemoryRateCounter();

        var decision = Count(counter, Hour(1000), Minute(5));

        Assert.True(decision.Allowed);
        Assert.Equal(5, decision.Limit);
        Assert.Equal(4, decision.Remaining);
    }

    [Fact]
    public void The_burst_window_refuses_before_the_sustained_one_does()
    {
        var counter = new InMemoryRateCounter();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(Count(counter, Hour(1000), Minute(3)).Allowed);
        }

        var decision = Count(counter, Hour(1000), Minute(3));

        Assert.False(decision.Allowed);
        Assert.Equal(3, decision.Limit);
        Assert.Equal(0, decision.Remaining);
    }

    [Fact]
    public void Every_window_counts_even_after_one_has_already_refused()
    {
        // Counting only until the first refusal would let a caller who is over their burst limit spend
        // the rest of the hour invisible to the sustained window.
        var counter = new InMemoryRateCounter();
        for (var i = 0; i < 6; i++)
        {
            Count(counter, Hour(4), Minute(2));
        }

        // Six requests, and the hourly window saw all six rather than the two the burst window let by.
        Assert.Equal(6, counter.Peek("key-1", Hour(4), Now));
    }

    [Fact]
    public void When_two_windows_refuse_the_later_reset_is_reported()
    {
        // Retrying when the burst window reopens would still fail the hourly one, and a Retry-After
        // that is too short invites a client to hammer a door that is still shut.
        var counter = new InMemoryRateCounter();
        for (var i = 0; i < 5; i++)
        {
            Count(counter, Hour(2), Minute(2));
        }

        var decision = Count(counter, Hour(2), Minute(2));

        Assert.False(decision.Allowed);
        Assert.Equal(Hour(2).ResetAt(Now), decision.ResetAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_window_with_no_positive_limit_is_ignored_rather_than_refusing_everything(int limit)
    {
        Assert.True(Count(new InMemoryRateCounter(), Minute(limit)).Allowed);
    }

    [Fact]
    public void A_window_with_no_duration_is_ignored_rather_than_dividing_by_zero()
    {
        Assert.True(Count(new InMemoryRateCounter(), new RateWindow(TimeSpan.Zero, 10)).Allowed);
    }

    // ---- what a key is subject to ----------------------------------------------------------------

    [Fact]
    public void A_keys_hourly_quota_keeps_meaning_exactly_what_it_meant()
    {
        var windows = RateLimits.For(quotaPerHour: 100, burst: null);

        Assert.Equal([new RateWindow(TimeSpan.FromHours(1), 100)], windows);
    }

    [Fact]
    public void A_burst_ceiling_applies_to_a_key_with_no_quota_at_all()
    {
        // "Unlimited" is a statement about a consumer's budget, not permission to melt the host.
        var windows = RateLimits.For(quotaPerHour: null, burst: Minute(20));

        Assert.Equal([Minute(20)], windows);
    }

    [Fact]
    public void Neither_configured_means_no_windows()
    {
        Assert.Empty(RateLimits.For(quotaPerHour: null, burst: null));
        Assert.Empty(RateLimits.For(quotaPerHour: 0, burst: Minute(0)));
    }

    // ---- the property the whole issue is about ---------------------------------------------------

    [Fact]
    public void Two_hosts_sharing_one_counter_enforce_the_sum_rather_than_twice_the_limit()
    {
        // The exact thing an in-process counter cannot do: behind a load balancer, the number in the
        // key's configuration must be the number the partner gets — not that number per pod.
        var shared = new InMemoryRateCounter();
        var replicaA = shared;
        var replicaB = shared;

        Assert.True(RateLimits.Count("key-1", [Hour(2)], replicaA, Now).Allowed);
        Assert.True(RateLimits.Count("key-1", [Hour(2)], replicaB, Now).Allowed);

        // The third request is over the budget whichever replica it lands on.
        Assert.False(RateLimits.Count("key-1", [Hour(2)], replicaA, Now).Allowed);
        Assert.False(RateLimits.Count("key-1", [Hour(2)], replicaB, Now).Allowed);
    }

    [Fact]
    public void Two_keys_do_not_share_a_budget()
    {
        var counter = new InMemoryRateCounter();
        RateLimits.Count("key-1", [Hour(1)], counter, Now);

        Assert.True(RateLimits.Count("key-2", [Hour(1)], counter, Now).Allowed);
    }

    [Fact]
    public void The_window_rolls_over_and_the_budget_returns()
    {
        var counter = new InMemoryRateCounter();
        RateLimits.Count("key-1", [Minute(1)], counter, Now);
        Assert.False(RateLimits.Count("key-1", [Minute(1)], counter, Now).Allowed);

        Assert.True(RateLimits.Count("key-1", [Minute(1)], counter, Now.AddMinutes(1)).Allowed);
    }

    [Fact]
    public void Counting_is_atomic_under_concurrency()
    {
        // Eight writers past a budget of 100: exactly 100 may be allowed, or the limit is advisory.
        var counter = new InMemoryRateCounter();
        var allowed = 0;

        Parallel.For(0, 800, _ =>
        {
            if (RateLimits.Count("key-1", [Hour(100)], counter, Now).Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(100, allowed);
    }

    [Fact]
    public void Peeking_does_not_count()
    {
        // Usage reporting must not spend the caller's budget to look at it.
        var counter = new InMemoryRateCounter();
        RateLimits.Count("key-1", [Hour(5)], counter, Now);

        Assert.Equal(1, counter.Peek("key-1", Hour(5), Now));
        Assert.Equal(1, counter.Peek("key-1", Hour(5), Now));
    }

    [Fact]
    public void A_key_never_seen_has_used_nothing()
    {
        Assert.Equal(0, new InMemoryRateCounter().Peek("never", Hour(5), Now));
    }

    [Fact]
    public void A_burst_of_the_same_length_as_the_hourly_quota_collapses_to_the_tighter_one()
    {
        // A counter identifies a bucket by key and duration, so two windows an hour long would both
        // land on it and count every request twice — enforcing half the configured number.
        var windows = RateLimits.For(quotaPerHour: 100, burst: new RateWindow(TimeSpan.FromHours(1), 3));

        var only = Assert.Single(windows);
        Assert.Equal(3, only.Limit);
    }

    [Fact]
    public void The_hourly_quota_survives_a_same_length_burst_that_is_roomier()
    {
        var windows = RateLimits.For(quotaPerHour: 5, burst: new RateWindow(TimeSpan.FromHours(1), 900));

        Assert.Equal(5, Assert.Single(windows).Limit);
    }

    [Fact]
    public void A_burst_of_a_different_length_stands_beside_the_hourly_quota()
    {
        var windows = RateLimits.For(quotaPerHour: 100, burst: Minute(20));

        Assert.Equal(2, windows.Count);
        Assert.Contains(windows, w => w.Duration == TimeSpan.FromHours(1) && w.Limit == 100);
        Assert.Contains(windows, w => w.Duration == TimeSpan.FromMinutes(1) && w.Limit == 20);
    }

    [Fact]
    public void A_zero_length_burst_is_ignored_rather_than_dividing_by_it()
    {
        var windows = RateLimits.For(quotaPerHour: 100, burst: new RateWindow(TimeSpan.Zero, 20));

        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(windows).Duration);
    }

    [Fact]
    public void The_shorter_window_refuses_first_and_the_longer_one_keeps_counting()
    {
        // The point of two windows: a caller stopped by the burst limit must not spend the rest of
        // the hour invisible to the sustained one.
        var counter = new InMemoryRateCounter();
        var windows = RateLimits.For(quotaPerHour: 10, burst: Minute(2));

        var first = RateLimits.Count("k", windows, counter, Now);
        var second = RateLimits.Count("k", windows, counter, Now);
        var third = RateLimits.Count("k", windows, counter, Now);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.False(third.Allowed);
        Assert.Equal(2, third.Limit);
        // Three requests reached the hourly window too, even though the third was refused.
        Assert.Equal(3, counter.Peek("k", new RateWindow(TimeSpan.FromHours(1), 10), Now));
    }

    /// <summary>A counter that always reports the same total, for pinning tie-breaks exactly.</summary>
    private sealed class FixedCounter(int count) : IRateCounter
    {
        public int Increment(string key, RateWindow window, DateTimeOffset now) => count;

        public int Peek(string key, RateWindow window, DateTimeOffset now) => count;
    }

    [Fact]
    public void When_two_windows_refuse_the_reported_reset_is_the_later_one()
    {
        // Retrying when the hour reopens would still fail the day's budget, and a Retry-After that is
        // too short invites a client to hammer a door that is still shut.
        var counter = new InMemoryRateCounter();
        var hour = new RateWindow(TimeSpan.FromHours(1), 1);
        var day = new RateWindow(TimeSpan.FromDays(1), 1);

        RateLimits.Count("k", [hour, day], counter, Now);
        var second = RateLimits.Count("k", [hour, day], counter, Now);

        Assert.False(second.Allowed);
        Assert.Equal(day.ResetAt(Now), second.ResetAt);
    }

    [Fact]
    public void Two_refusals_that_reopen_together_report_the_first_window_so_the_answer_is_stable()
    {
        // A tie has no better answer, but it must have a fixed one: the same request must not report
        // a different limit on a different replica.
        var windows = new[] { new RateWindow(TimeSpan.FromHours(1), 1), new RateWindow(TimeSpan.FromHours(1), 2) };

        var decision = RateLimits.Count("k", windows, new FixedCounter(5), Now);

        Assert.False(decision.Allowed);
        Assert.Equal(1, decision.Limit);
    }

    [Fact]
    public void The_reported_window_is_the_tightest_even_when_it_is_not_the_last_one_counted()
    {
        var windows = new[] { new RateWindow(TimeSpan.FromMinutes(1), 3), new RateWindow(TimeSpan.FromHours(1), 100) };

        var decision = RateLimits.Count("k", windows, new FixedCounter(1), Now);

        Assert.True(decision.Allowed);
        Assert.Equal(3, decision.Limit);
        Assert.Equal(2, decision.Remaining);
    }

    [Fact]
    public void Equally_roomy_windows_report_the_first_rather_than_the_last()
    {
        var hour = new RateWindow(TimeSpan.FromHours(1), 10);
        var windows = new[] { hour, new RateWindow(TimeSpan.FromMinutes(1), 10) };

        var decision = RateLimits.Count("k", windows, new FixedCounter(3), Now);

        Assert.Equal(hour.ResetAt(Now), decision.ResetAt);
    }

    [Fact]
    public void A_count_from_a_window_that_has_rolled_over_is_not_reported_as_current_usage()
    {
        // The stale-bucket case: an operator reading a key's usage must not be shown what was spent in
        // the previous hour, or a quiet consumer looks permanently near their limit.
        var counter = new InMemoryRateCounter();
        var window = new RateWindow(TimeSpan.FromHours(1), 10);

        counter.Increment("k", window, Now);

        Assert.Equal(0, counter.Peek("k", window, Now.AddHours(1)));
    }
}
