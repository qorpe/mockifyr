using Mockifyr.Core;

namespace Mockifyr.Core.Tests;

/// <summary>
/// Pure-logic coverage for the degradation plan (#289): the decision a profile makes about one
/// request. No oracle — the reference engine has no tenant-wide degradation — so this is a self-test
/// suite, and the rates are asserted over a sample with a fixed seed rather than a single coin flip.
/// </summary>
public class DegradationPlanTests
{
    private static DegradationProfile Profile(
        int fixedMs = 0, int jitterMs = 0, double errorRatio = 0d, int errorStatus = 503,
        double faultRatio = 0d, FaultKind fault = FaultKind.ConnectionResetByPeer, int seed = 42) =>
        new(fixedMs, jitterMs, errorRatio, errorStatus, faultRatio, fault, seed);

    [Fact]
    public void A_healthy_profile_decides_nothing()
    {
        Assert.True(DegradationProfile.Healthy.IsHealthy);
        Assert.Equal(DegradationDecision.None, DegradationPlan.For(DegradationProfile.Healthy, 0));
    }

    [Fact]
    public void A_profile_with_only_a_seed_is_still_healthy()
    {
        // Otherwise a client that always sends a seed would degrade a tenant by asking for nothing.
        Assert.True(Profile(seed: 12345).IsHealthy);
    }

    [Fact]
    public void Fixed_latency_applies_to_every_request()
    {
        var profile = Profile(fixedMs: 200);

        foreach (var ordinal in Enumerable.Range(0, 20))
        {
            Assert.Equal(200, DegradationPlan.For(profile, ordinal).DelayMs);
        }
    }

    [Fact]
    public void Jitter_stays_inside_its_bound_and_actually_varies()
    {
        var profile = Profile(fixedMs: 100, jitterMs: 400);
        var delays = Enumerable.Range(0, 200).Select(i => DegradationPlan.For(profile, i).DelayMs).ToList();

        Assert.All(delays, d => Assert.InRange(d, 100, 499));

        // A "jitter" that returned the same number every time would pass a bounds check and be useless.
        Assert.True(delays.Distinct().Count() > 50);
    }

    [Fact]
    public void The_same_seed_replays_the_same_sequence()
    {
        var first = Enumerable.Range(0, 100).Select(i => DegradationPlan.For(Profile(jitterMs: 50, errorRatio: 0.3), i));
        var again = Enumerable.Range(0, 100).Select(i => DegradationPlan.For(Profile(jitterMs: 50, errorRatio: 0.3), i));

        // This is what turns a chaos experiment into a regression test: the run that found the bug can
        // be run again.
        Assert.Equal(first, again);
    }

    [Fact]
    public void A_different_seed_gives_a_different_sequence()
    {
        var a = Enumerable.Range(0, 100).Select(i => DegradationPlan.For(Profile(errorRatio: 0.5, seed: 1), i).ErrorStatus);
        var b = Enumerable.Range(0, 100).Select(i => DegradationPlan.For(Profile(errorRatio: 0.5, seed: 2), i).ErrorStatus);

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    public void The_error_ratio_is_the_share_of_requests_that_fail(double ratio)
    {
        var profile = Profile(errorRatio: ratio);
        var failures = Enumerable.Range(0, 10_000).Count(i => DegradationPlan.For(profile, i).ErrorStatus is not null);

        // Within one percentage point over ten thousand requests: a generator that was merely "roughly"
        // right would make a 5% profile indistinguishable from a 7% one, which is the difference a
        // resilience test is measuring.
        Assert.InRange(failures / 10_000d, ratio - 0.01, ratio + 0.01);
    }

    [Fact]
    public void The_fault_ratio_is_the_share_of_requests_that_break()
    {
        var profile = Profile(faultRatio: 0.1, fault: FaultKind.EmptyResponse);
        var decisions = Enumerable.Range(0, 10_000).Select(i => DegradationPlan.For(profile, i)).ToList();

        Assert.InRange(decisions.Count(d => d.Fault is not null) / 10_000d, 0.09, 0.11);
        Assert.All(decisions.Where(d => d.Fault is not null), d => Assert.Equal(FaultKind.EmptyResponse, d.Fault));
    }

    [Fact]
    public void A_ratio_of_one_fails_every_request()
    {
        var profile = Profile(errorRatio: 1d, errorStatus: 500);

        Assert.All(
            Enumerable.Range(0, 50).Select(i => DegradationPlan.For(profile, i)),
            d => Assert.Equal(500, d.ErrorStatus));
    }

    [Fact]
    public void A_ratio_of_zero_never_fires()
    {
        var profile = Profile(fixedMs: 10, errorRatio: 0d, faultRatio: 0d);

        Assert.All(
            Enumerable.Range(0, 500).Select(i => DegradationPlan.For(profile, i)),
            d =>
            {
                Assert.Null(d.ErrorStatus);
                Assert.Null(d.Fault);
            });
    }

    [Fact]
    public void A_broken_connection_outranks_an_error_status()
    {
        // Both gates wide open: a dependency that resets the connection does not first politely explain
        // itself with a 503, so the fault must win every time rather than most of the time.
        var profile = Profile(errorRatio: 1d, faultRatio: 1d);

        Assert.All(
            Enumerable.Range(0, 50).Select(i => DegradationPlan.For(profile, i)),
            d =>
            {
                Assert.NotNull(d.Fault);
                Assert.Null(d.ErrorStatus);
            });
    }

    [Fact]
    public void Latency_still_applies_to_a_request_that_then_fails()
    {
        var profile = Profile(fixedMs: 75, errorRatio: 1d);

        // A degraded dependency is usually slow *and* failing; answering instantly with a 503 would
        // exercise the client's timeout handling less than the real thing does.
        Assert.Equal(75, DegradationPlan.For(profile, 0).DelayMs);
        Assert.Equal(503, DegradationPlan.For(profile, 0).ErrorStatus);
    }

    [Fact]
    public void The_error_status_is_whatever_was_asked_for()
    {
        Assert.Equal(429, DegradationPlan.For(Profile(errorRatio: 1d, errorStatus: 429), 0).ErrorStatus);
    }

    [Fact]
    public void The_sequence_for_a_seed_is_pinned()
    {
        // A seed is a promise. An operator records one because a run turned up something interesting,
        // and replays it weeks later on a newer build — so the stream itself is a compatibility surface,
        // not an implementation detail. Changing the generator must break this test loudly rather than
        // quietly hand everyone a different sequence under the same number.
        // A jitter this wide is deliberate: the delay is an int, so a narrow range would round away the
        // low mantissa bits and let a changed generator slip through a test that looks strict. All three
        // draws are exercised — fault, error and jitter — because a golden that only covered two would
        // pin two thirds of the promise.
        var profile = Profile(jitterMs: 1_000_000, errorRatio: 0.5, faultRatio: 0.25, seed: 4242);
        var sequence = Enumerable.Range(0, 8)
            .Select(i => DegradationPlan.For(profile, i))
            .Select(d => (d.DelayMs, Outcome: d.Fault is not null ? "fault" : d.ErrorStatus?.ToString() ?? "ok"))
            .ToArray();

        Assert.Equal(
            [
                (84957, "503"), (863347, "503"), (270003, "503"), (353950, "fault"),
                (750752, "503"), (374697, "ok"), (527042, "fault"), (499689, "ok"),
            ],
            sequence);
    }

    [Fact]
    public void A_nonsensical_negative_delay_is_treated_as_none()
    {
        // The admin edge refuses negatives, but the value type is public and reachable from the library
        // API; answering with a negative delay would send Task.Delay a value it throws on.
        Assert.Equal(0, DegradationPlan.For(Profile(fixedMs: -100, errorRatio: 1d), 0).DelayMs);
    }

    [Fact]
    public void Ordinals_far_apart_still_hold_the_ratio()
    {
        // The ordinal is a counter that never resets while a profile is live, so the generator must not
        // drift or fall into a cycle after the first few thousand requests.
        var profile = Profile(errorRatio: 0.2);
        var late = Enumerable.Range(1_000_000, 10_000).Count(i => DegradationPlan.For(profile, i).ErrorStatus is not null);

        Assert.InRange(late / 10_000d, 0.19, 0.21);
    }
}
