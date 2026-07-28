using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for the bounded journal (#220): eviction order and exactness at the cap,
/// per-tenant independence, the id index (including the tenant gate the oracle cannot check), the
/// unbounded opt-out, and the disabled journal.
/// </summary>
public sealed class JournalLimitUnitTests
{
    private static ServeEvent Event(string tenant, string url) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = new TenantId(tenant),
        Request = CanonicalRequestBuilder.Build("GET", url, [], [], "http"),
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Eviction_is_oldest_first_and_exact_at_the_cap()
    {
        var journal = new InMemoryRequestJournal(limit: 3);
        var events = Enumerable.Range(1, 5).Select(i => Event("acme", $"/j/{i}")).ToList();
        foreach (var e in events)
        {
            journal.Record(e);
        }

        var kept = journal.Query(new TenantId("acme"), new ServeEventQuery());
        Assert.Equal(new[] { "/j/3", "/j/4", "/j/5" }, kept.Select(e => e.Request.Url));

        // Evicted events also leave the id index; the survivors stay resolvable.
        Assert.Empty(journal.Query(new TenantId("acme"), new ServeEventQuery { Id = events[0].Id }));
        Assert.Single(journal.Query(new TenantId("acme"), new ServeEventQuery { Id = events[4].Id }));
    }

    [Fact]
    public void Tenants_are_bounded_independently()
    {
        var journal = new InMemoryRequestJournal(limit: 2);
        journal.Record(Event("a", "/a1"));
        journal.Record(Event("a", "/a2"));
        journal.Record(Event("b", "/b1"));
        journal.Record(Event("a", "/a3"));

        Assert.Equal(new[] { "/a2", "/a3" }, journal.Query(new TenantId("a"), new ServeEventQuery()).Select(e => e.Request.Url));
        Assert.Equal(new[] { "/b1" }, journal.Query(new TenantId("b"), new ServeEventQuery()).Select(e => e.Request.Url));
    }

    [Fact]
    public void The_id_lookup_is_tenant_gated()
    {
        var journal = new InMemoryRequestJournal(limit: 10);
        var evt = Event("acme", "/secret");
        journal.Record(evt);

        // The owning tenant resolves it; another tenant addressing the same id sees nothing —
        // the isolation invariant the oracle cannot check.
        Assert.Single(journal.Query(new TenantId("acme"), new ServeEventQuery { Id = evt.Id }));
        Assert.Empty(journal.Query(new TenantId("globex"), new ServeEventQuery { Id = evt.Id }));
    }

    [Fact]
    public void Null_limit_means_unbounded()
    {
        var journal = new InMemoryRequestJournal(limit: null);
        foreach (var i in Enumerable.Range(1, InMemoryRequestJournal.DefaultLimit + 50))
        {
            journal.Record(Event("acme", $"/j/{i}"));
        }

        Assert.Equal(InMemoryRequestJournal.DefaultLimit + 50, journal.Query(new TenantId("acme"), new ServeEventQuery()).Count);
    }

    [Fact]
    public void Query_filters_compose_on_the_bounded_journal()
    {
        var journal = new InMemoryRequestJournal(limit: 10);
        var stub = Mockifyr.Adapters.MappingJson.MappingJsonReader.Read(
            """{"request":{"method":"GET","url":"/matched"},"response":{"status":200}}""",
            new TenantId("acme"))[0];
        journal.Record(Event("acme", "/unmatched-1"));
        journal.Record(Event("acme", "/matched") with { MatchedStub = stub });
        journal.Record(Event("acme", "/unmatched-2"));

        // Unknown tenant → empty, never a throw.
        Assert.Empty(journal.Query(new TenantId("nobody"), new ServeEventQuery()));

        // UnmatchedOnly keeps exactly the events without a stub.
        Assert.Equal(new[] { "/unmatched-1", "/unmatched-2" },
            journal.Query(new TenantId("acme"), new ServeEventQuery { UnmatchedOnly = true }).Select(e => e.Request.Url));

        // MatchingStubId keeps exactly the events matched by that stub.
        Assert.Equal(new[] { "/matched" },
            journal.Query(new TenantId("acme"), new ServeEventQuery { MatchingStubId = stub.Id }).Select(e => e.Request.Url));

        // Limit truncates from the front of the retained window.
        Assert.Equal(2, journal.Query(new TenantId("acme"), new ServeEventQuery { Limit = 2 }).Count);
    }

    [Fact]
    public void The_disabled_journal_records_nothing()
    {
        var journal = new NullRequestJournal();
        var evt = Event("acme", "/j/1");
        journal.Record(evt);

        Assert.Empty(journal.Query(new TenantId("acme"), new ServeEventQuery()));
        Assert.Empty(journal.Query(new TenantId("acme"), new ServeEventQuery { Id = evt.Id }));
    }
}
