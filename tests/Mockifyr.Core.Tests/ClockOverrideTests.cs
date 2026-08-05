using Mockifyr.Core;

namespace Mockifyr.Core.Tests;

/// <summary>
/// Pure-logic coverage for the tenant clock override (#290). No oracle exists — the reference engine
/// has no clock surface — so this is a self-test suite in the G18 tradition, and the wire behaviour it
/// underpins is proven end to end in <c>TenantClockTests</c>.
/// </summary>
public class ClockOverrideTests
{
    private static readonly DateTimeOffset RealNow = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Real_time_changes_nothing()
    {
        Assert.True(ClockOverride.RealTime.IsRealTime);
        Assert.Equal(RealNow, ClockOverride.RealTime.Apply(RealNow));
    }

    [Fact]
    public void A_frozen_clock_ignores_the_host_clock_entirely()
    {
        var frozen = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var over = new ClockOverride(frozen, TimeSpan.Zero);

        // The point of freezing: two renders a second apart must answer identically, so the host clock
        // moving between them must not show through.
        Assert.Equal(frozen, over.Apply(RealNow));
        Assert.Equal(frozen, over.Apply(RealNow.AddSeconds(1)));
        Assert.False(over.IsRealTime);
    }

    [Fact]
    public void An_offset_clock_keeps_running()
    {
        var over = new ClockOverride(null, TimeSpan.FromDays(1));

        Assert.Equal(RealNow.AddDays(1), over.Apply(RealNow));
        Assert.Equal(RealNow.AddDays(1).AddSeconds(30), over.Apply(RealNow.AddSeconds(30)));
        Assert.False(over.IsRealTime);
    }

    [Fact]
    public void A_negative_offset_moves_the_tenant_into_the_past()
    {
        // "What did this look like before the migration" is as legitimate a question as "what happens
        // next month", so the offset is signed rather than a duration.
        var over = new ClockOverride(null, TimeSpan.FromHours(-3));

        Assert.Equal(RealNow.AddHours(-3), over.Apply(RealNow));
    }

    [Fact]
    public void A_frozen_clock_wins_over_an_offset_that_should_not_exist()
    {
        // The admin edge refuses this combination, so the value type never sees it in practice. Pinning
        // the resolution anyway means a future caller constructing the record directly gets the reading
        // the documentation promises rather than whatever the implementation happened to do.
        var frozen = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(frozen, new ClockOverride(frozen, TimeSpan.FromDays(5)).Apply(RealNow));
    }

    [Fact]
    public void A_zero_offset_is_real_time()
    {
        // Otherwise a client that PUTs {"offsetSeconds":0} to mean "back to normal" would leave the
        // tenant marked as overridden forever.
        Assert.True(new ClockOverride(null, TimeSpan.Zero).IsRealTime);
    }

    [Fact]
    public void The_frozen_instant_keeps_its_offset_from_utc()
    {
        // A caller who freezes at a local instant gets that instant back, not a silently re-zoned one:
        // the value is compared as an instant everywhere downstream.
        var frozen = new DateTimeOffset(2027, 1, 1, 9, 0, 0, TimeSpan.FromHours(3));
        var applied = new ClockOverride(frozen, TimeSpan.Zero).Apply(RealNow);

        Assert.Equal(frozen, applied);
        Assert.Equal(TimeSpan.FromHours(3), applied.Offset);
    }
}
