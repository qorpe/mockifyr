using System.Text;
using System.Text.Json;
using Mockifyr.Core;
using Mockifyr.Outbound;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for <see cref="RecordingSession"/>'s stub generation. The wire behavior is
/// pinned against the oracle in the differential suite
/// (<c>Recording_RepeatedIdenticalRequests_CaptureLikeTheOracle</c>); these tests cover the chaining
/// rules the diff cannot enumerate cheaply: chain lengths, state naming, identity boundaries
/// (method/URL/body), snapshot-then-continue regeneration, and session reset.
/// </summary>
public sealed class RecordingSessionTests
{
    private static CanonicalRequest Request(string method = "GET", string url = "/dup", string body = "") =>
        CanonicalRequestBuilder.Build(method, url, [], Encoding.UTF8.GetBytes(body));

    private static CanonicalResponse Response(string body) => new()
    {
        Status = 200,
        Headers = Array.Empty<KeyValuePair<string, string>>().ToLookup(p => p.Key, p => p.Value),
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static RecordingSession Recording()
    {
        var session = new RecordingSession();
        session.Start(TenantId.Default, "http://upstream.example");
        return session;
    }

    private static (string? Scenario, string? Required, string? Next, string? Body) Fields(string stubJson)
    {
        using var doc = JsonDocument.Parse(stubJson);
        string? Get(string name) => doc.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
        var body = doc.RootElement.GetProperty("response").TryGetProperty("body", out var b) ? b.GetString() : null;
        return (Get("scenarioName"), Get("requiredScenarioState"), Get("newScenarioState"), body);
    }

    [Fact]
    public void A_single_capture_stays_scenario_free()
    {
        var session = Recording();
        session.Record(TenantId.Default, Request(), Response("one"));

        var stub = Assert.Single(session.Stop(TenantId.Default));
        Assert.Equal((null, null, null, "one"), Fields(stub));
    }

    [Fact]
    public void Repeats_chain_started_to_next_and_the_last_does_not_advance()
    {
        var session = Recording();
        session.Record(TenantId.Default, Request(), Response("first"));
        session.Record(TenantId.Default, Request(), Response("second"));
        session.Record(TenantId.Default, Request(), Response("third"));

        var stubs = session.Stop(TenantId.Default).Select(Fields).ToList();

        Assert.Equal(3, stubs.Count);
        var scenario = stubs[0].Scenario;
        Assert.NotNull(scenario);
        Assert.All(stubs, s => Assert.Equal(scenario, s.Scenario));

        // Recorded order is replay order: Started -> -2 -> -3, and the last stub stops advancing.
        Assert.Equal(("Started", $"{scenario}-2", "first"), (stubs[0].Required, stubs[0].Next, stubs[0].Body));
        Assert.Equal(($"{scenario}-2", $"{scenario}-3", "second"), (stubs[1].Required, stubs[1].Next, stubs[1].Body));
        Assert.Equal(($"{scenario}-3", null, "third"), (stubs[2].Required, stubs[2].Next, stubs[2].Body));
    }

    [Fact]
    public void Each_repeated_request_gets_its_own_numbered_scenario()
    {
        var session = Recording();
        session.Record(TenantId.Default, Request(url: "/a"), Response("a1"));
        session.Record(TenantId.Default, Request(url: "/b?x=1"), Response("b1"));
        session.Record(TenantId.Default, Request(url: "/a"), Response("a2"));
        session.Record(TenantId.Default, Request(url: "/b?x=1"), Response("b2"));

        var scenarios = session.Stop(TenantId.Default).Select(Fields).Select(f => f.Scenario).ToList();

        Assert.Equal(["scenario-1-a", "scenario-2-b-x-1", "scenario-1-a", "scenario-2-b-x-1"], scenarios);
    }

    [Fact]
    public void Identity_is_method_url_and_body_together()
    {
        var session = Recording();
        session.Record(TenantId.Default, Request(method: "POST", url: "/same", body: "{\"a\":1}"), Response("one"));
        session.Record(TenantId.Default, Request(method: "POST", url: "/same", body: "{\"a\":2}"), Response("two"));
        session.Record(TenantId.Default, Request(method: "PUT", url: "/same", body: "{\"a\":1}"), Response("three"));

        // Different bodies / methods are different exchanges, not repeats — no scenario is invented.
        Assert.All(session.Stop(TenantId.Default).Select(Fields), s => Assert.Null(s.Scenario));
    }

    [Fact]
    public void Snapshot_regenerates_and_the_chain_grows_with_later_repeats()
    {
        var session = Recording();
        session.Record(TenantId.Default, Request(), Response("first"));
        session.Record(TenantId.Default, Request(), Response("second"));

        var mid = session.Snapshot(TenantId.Default).Select(Fields).ToList();
        Assert.Equal(2, mid.Count);
        Assert.Null(mid[1].Next);

        session.Record(TenantId.Default, Request(), Response("third"));
        var final = session.Stop(TenantId.Default).Select(Fields).ToList();

        // The former tail now advances to a -3 state; regeneration keeps the numbering consistent.
        Assert.Equal(3, final.Count);
        Assert.Equal($"{final[1].Scenario}-3", final[1].Next);
        Assert.Null(final[2].Next);
    }

    [Fact]
    public void Stop_clears_the_session_and_start_discards_prior_captures()
    {
        var session = Recording();
        session.Record(TenantId.Default, Request(), Response("one"));

        Assert.Single(session.Stop(TenantId.Default));
        Assert.Null(session.TargetBaseUrl(TenantId.Default));
        Assert.Empty(session.Stop(TenantId.Default));

        session.Start(TenantId.Default, "http://upstream.example");
        Assert.Equal("http://upstream.example", session.TargetBaseUrl(TenantId.Default));
        Assert.Empty(session.Snapshot(TenantId.Default));
    }

    [Fact]
    public void Restarting_without_stopping_discards_the_previous_session_captures()
    {
        var session = Recording();
        session.Record(TenantId.Default, Request(), Response("stale"));

        // A second start (no stop in between) begins a FRESH session — the stale capture must not
        // leak into it, or the first snapshot would report another target's traffic.
        session.Start(TenantId.Default, "http://other.example");
        Assert.Empty(session.Snapshot(TenantId.Default));
    }
}
