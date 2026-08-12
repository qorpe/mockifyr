using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Per-consumer usage (#356): bounded, aggregated, and never a second journal.
/// </summary>
public sealed class UsageTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Outcomes_are_counted_apart_because_they_are_different_conversations()
    {
        var recorder = new InMemoryUsageRecorder();

        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now);
        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now);
        recorder.Record(Acme, "k", "/nope", UsageOutcome.Unmatched, Now);
        recorder.Record(Acme, "k", "/orders", UsageOutcome.RateLimited, Now);
        recorder.Record(Acme, "k", "/orders", UsageOutcome.Forbidden, Now);
        recorder.Record(Acme, "k", "/orders", UsageOutcome.Unauthorized, Now);

        var usage = Assert.Single(recorder.Report(Acme, 24, Now));

        Assert.Equal(6, usage.Total);
        Assert.Equal(2, usage.Matched);
        Assert.Equal(1, usage.Unmatched);
        Assert.Equal(1, usage.RateLimited);
        Assert.Equal(1, usage.Forbidden);
        Assert.Equal(1, usage.Unauthorized);
    }

    [Fact]
    public void One_tenants_usage_is_not_another_tenants_to_read()
    {
        var recorder = new InMemoryUsageRecorder();

        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now);
        recorder.Record(new TenantId("other"), "k2", "/orders", UsageOutcome.Matched, Now);

        Assert.Equal("k", Assert.Single(recorder.Report(Acme, 24, Now)).KeyId);
    }

    [Fact]
    public void Unmatched_paths_are_tracked_apart_so_a_busy_path_cannot_crowd_them_out()
    {
        // The unmatched paths are the integration going wrong, which is the reason anybody opens this.
        var recorder = new InMemoryUsageRecorder();
        for (var i = 0; i < 500; i++)
        {
            recorder.Record(Acme, "k", "/health", UsageOutcome.Matched, Now);
        }

        recorder.Record(Acme, "k", "/v2/orders", UsageOutcome.Unmatched, Now);

        var usage = Assert.Single(recorder.Report(Acme, 24, Now));

        Assert.Equal("/health", usage.TopPaths[0].Path);
        Assert.Equal("/v2/orders", Assert.Single(usage.TopUnmatchedPaths).Path);
    }

    [Fact]
    public void An_hour_older_than_the_window_asked_for_is_not_reported()
    {
        var recorder = new InMemoryUsageRecorder();

        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now.AddHours(-5));
        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now);

        Assert.Equal(1, Assert.Single(recorder.Report(Acme, hours: 2, Now)).Total);
        Assert.Equal(2, Assert.Single(recorder.Report(Acme, hours: 24, Now)).Total);
    }

    [Fact]
    public void A_window_wider_than_what_is_retained_reports_what_is_retained()
    {
        // Asking for a year does not invent one: the bound is the answer, not an error.
        var recorder = new InMemoryUsageRecorder();
        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now);

        Assert.Equal(1, Assert.Single(recorder.Report(Acme, hours: 9000, Now)).Total);
    }

    [Fact]
    public void Buckets_older_than_a_day_are_dropped_rather_than_kept_forever()
    {
        var recorder = new InMemoryUsageRecorder();
        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now);

        // A day later, recording anything at all sweeps what has aged out.
        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now.AddHours(InMemoryUsageRecorder.RetainedHours));

        Assert.Equal(1, Assert.Single(recorder.Report(Acme, 24, Now.AddHours(InMemoryUsageRecorder.RetainedHours))).Total);
    }

    [Fact]
    public void The_path_table_stays_bounded_however_many_distinct_paths_arrive()
    {
        // The whole point of not being a journal: a consumer walking a million URLs must not be able
        // to grow this host's memory with them.
        var recorder = new InMemoryUsageRecorder();
        for (var i = 0; i < 10_000; i++)
        {
            recorder.Record(Acme, "k", $"/thing/{i}", UsageOutcome.Matched, Now);
        }

        var usage = Assert.Single(recorder.Report(Acme, 24, Now));

        Assert.Equal(10_000, usage.Total);
        Assert.True(usage.TopPaths.Count <= 10, $"reported {usage.TopPaths.Count} paths");
    }

    [Fact]
    public void The_heavy_hitters_survive_a_flood_of_one_off_paths()
    {
        // What the approximate counter is accurate about: which paths dominate, not the exact count of
        // a path that arrived after eviction started.
        var recorder = new InMemoryUsageRecorder();
        for (var i = 0; i < 200; i++)
        {
            recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now);
            recorder.Record(Acme, "k", $"/noise/{i}", UsageOutcome.Matched, Now);
        }

        var usage = Assert.Single(recorder.Report(Acme, 24, Now));

        Assert.Equal("/orders", usage.TopPaths[0].Path);
        Assert.Equal(200, usage.TopPaths[0].Count);
    }

    [Fact]
    public void Hours_are_merged_into_one_row_per_key()
    {
        var recorder = new InMemoryUsageRecorder();

        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now.AddHours(-3));
        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now.AddHours(-2));
        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now);

        var usage = Assert.Single(recorder.Report(Acme, 24, Now));

        Assert.Equal(3, usage.Total);
        Assert.Equal(3, Assert.Single(usage.TopPaths).Count);
    }

    [Fact]
    public void Keys_are_reported_busiest_first()
    {
        var recorder = new InMemoryUsageRecorder();
        recorder.Record(Acme, "quiet", "/a", UsageOutcome.Matched, Now);
        recorder.Record(Acme, "busy", "/a", UsageOutcome.Matched, Now);
        recorder.Record(Acme, "busy", "/a", UsageOutcome.Matched, Now);

        var report = recorder.Report(Acme, 24, Now);

        Assert.Equal("busy", report[0].KeyId);
        Assert.Equal("quiet", report[1].KeyId);
    }

    [Fact]
    public void Recording_holds_under_parallel_traffic()
    {
        var recorder = new InMemoryUsageRecorder();

        Parallel.For(0, 2000, i =>
            recorder.Record(Acme, "k", "/orders", i % 2 == 0 ? UsageOutcome.Matched : UsageOutcome.Unmatched, Now));

        var usage = Assert.Single(recorder.Report(Acme, 24, Now));

        Assert.Equal(2000, usage.Total);
        Assert.Equal(1000, usage.Matched);
        Assert.Equal(1000, usage.Unmatched);
    }

    [Fact]
    public void The_default_recorder_keeps_nothing()
    {
        // A host that was not asked to remember does not remember.
        var recorder = new NullUsageRecorder();

        recorder.Record(Acme, "k", "/orders", UsageOutcome.Matched, Now);

        Assert.Empty(recorder.Report(Acme, 24, Now));
    }

    [Fact]
    public void The_window_boundary_includes_the_hour_asked_for_and_excludes_the_one_before_it()
    {
        // Off by one here means "the last 24 hours" quietly means 23 or 25, and nobody notices until
        // two reports of the same traffic disagree.
        var recorder = new InMemoryUsageRecorder();

        recorder.Record(Acme, "k", "/a", UsageOutcome.Matched, Now.AddHours(-1));
        recorder.Record(Acme, "k", "/a", UsageOutcome.Matched, Now.AddHours(-2));

        Assert.Equal(1, Assert.Single(recorder.Report(Acme, hours: 2, Now)).Total);
        Assert.Equal(2, Assert.Single(recorder.Report(Acme, hours: 3, Now)).Total);
    }

    [Fact]
    public void Every_counter_sums_across_hours_rather_than_reporting_the_busiest_one()
    {
        var recorder = new InMemoryUsageRecorder();
        foreach (var offset in new[] { -2, -1, 0 })
        {
            recorder.Record(Acme, "k", "/a", UsageOutcome.Matched, Now.AddHours(offset));
            recorder.Record(Acme, "k", "/a", UsageOutcome.Unmatched, Now.AddHours(offset));
            recorder.Record(Acme, "k", "/a", UsageOutcome.Unauthorized, Now.AddHours(offset));
            recorder.Record(Acme, "k", "/a", UsageOutcome.RateLimited, Now.AddHours(offset));
            recorder.Record(Acme, "k", "/a", UsageOutcome.Forbidden, Now.AddHours(offset));
        }

        var usage = Assert.Single(recorder.Report(Acme, 24, Now));

        Assert.Equal(15, usage.Total);
        Assert.Equal(3, usage.Matched);
        Assert.Equal(3, usage.Unmatched);
        Assert.Equal(3, usage.Unauthorized);
        Assert.Equal(3, usage.RateLimited);
        Assert.Equal(3, usage.Forbidden);
    }

    [Fact]
    public void An_hour_exactly_at_the_retention_edge_is_dropped_by_the_next_record()
    {
        var recorder = new InMemoryUsageRecorder();
        recorder.Record(Acme, "old", "/a", UsageOutcome.Matched, Now);

        // Exactly RetainedHours later the first bucket is past the edge; recording anything sweeps it.
        recorder.Record(Acme, "new", "/a", UsageOutcome.Matched, Now.AddHours(InMemoryUsageRecorder.RetainedHours));

        var report = recorder.Report(Acme, 24, Now.AddHours(InMemoryUsageRecorder.RetainedHours));

        Assert.Equal("new", Assert.Single(report).KeyId);
    }

    [Fact]
    public void An_hour_one_short_of_the_edge_is_still_there()
    {
        var recorder = new InMemoryUsageRecorder();
        recorder.Record(Acme, "old", "/a", UsageOutcome.Matched, Now);

        recorder.Record(Acme, "new", "/a", UsageOutcome.Matched, Now.AddHours(InMemoryUsageRecorder.RetainedHours - 1));

        var report = recorder.Report(Acme, 24, Now.AddHours(InMemoryUsageRecorder.RetainedHours - 1));

        Assert.Equal(2, report.Count);
    }

    [Fact]
    public void The_key_table_stops_growing_and_what_is_already_there_keeps_counting()
    {
        // The bound exists so a tenant with a great many live keys cannot grow this without limit. A
        // key already being tracked must keep counting after it is reached, or the cap would silently
        // stop reporting the busiest consumers.
        var recorder = new InMemoryUsageRecorder();
        var capacity = InMemoryUsageRecorder.TrackedKeys * InMemoryUsageRecorder.RetainedHours;
        for (var i = 0; i < capacity; i++)
        {
            recorder.Record(Acme, $"key-{i}", "/a", UsageOutcome.Matched, Now);
        }

        recorder.Record(Acme, "one-too-many", "/a", UsageOutcome.Matched, Now);
        recorder.Record(Acme, "key-0", "/a", UsageOutcome.Matched, Now);

        var report = recorder.Report(Acme, 24, Now);

        Assert.Equal(capacity, report.Count);
        Assert.DoesNotContain(report, usage => usage.KeyId == "one-too-many");
        Assert.Equal(2, report.Single(usage => usage.KeyId == "key-0").Total);
    }

    [Fact]
    public void The_table_holds_exactly_its_bound_before_it_starts_replacing()
    {
        var recorder = new InMemoryUsageRecorder();
        recorder.Record(Acme, "k", "/busy", UsageOutcome.Matched, Now);
        recorder.Record(Acme, "k", "/busy", UsageOutcome.Matched, Now);
        recorder.Record(Acme, "k", "/busy", UsageOutcome.Matched, Now);
        for (var i = 1; i < InMemoryUsageRecorder.TrackedPaths; i++)
        {
            recorder.Record(Acme, "k", $"/quiet/{i}", UsageOutcome.Matched, Now);
        }

        // The table is exactly full: the busy path is untouched and still counts three.
        Assert.Equal(3, Assert.Single(recorder.Report(Acme, 24, Now)).TopPaths[0].Count);

        // One more distinct path replaces the smallest entry and inherits its count, which is what
        // keeps a long tail from being reported as brand new every time.
        recorder.Record(Acme, "k", "/the-51st", UsageOutcome.Matched, Now);
        var top = Assert.Single(recorder.Report(Acme, 24, Now)).TopPaths;

        Assert.Equal("/busy", top[0].Path);
        Assert.Equal(3, top[0].Count);
        Assert.Equal(2, top.Single(entry => entry.Path == "/the-51st").Count);
    }

    [Fact]
    public void Paths_that_tie_are_ordered_by_name_so_two_reads_agree()
    {
        var recorder = new InMemoryUsageRecorder();
        recorder.Record(Acme, "k", "/b", UsageOutcome.Matched, Now);
        recorder.Record(Acme, "k", "/a", UsageOutcome.Matched, Now);

        var top = Assert.Single(recorder.Report(Acme, 24, Now)).TopPaths;

        Assert.Equal("/a", top[0].Path);
        Assert.Equal("/b", top[1].Path);
    }

    [Fact]
    public void A_full_table_makes_room_by_dropping_what_has_aged_out()
    {
        // Why eviction runs before the cap is checked: a host up for a week is holding buckets nobody
        // can ask about any more, and refusing today's key while keeping last Tuesday's would report
        // nothing for the consumer somebody is actually looking at.
        var recorder = new InMemoryUsageRecorder();
        var capacity = InMemoryUsageRecorder.TrackedKeys * InMemoryUsageRecorder.RetainedHours;
        for (var i = 0; i < capacity; i++)
        {
            recorder.Record(Acme, $"key-{i}", "/a", UsageOutcome.Matched, Now);
        }

        var later = Now.AddHours(InMemoryUsageRecorder.RetainedHours);
        recorder.Record(Acme, "todays-key", "/a", UsageOutcome.Matched, later);

        var report = recorder.Report(Acme, 24, later);

        Assert.Equal("todays-key", Assert.Single(report).KeyId);
    }
}
