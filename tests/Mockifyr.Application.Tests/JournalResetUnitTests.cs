using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for journal reset (<c>DELETE /__admin/requests</c>): the id index is pruned
/// along with the tenant's entries, other tenants are untouched, and resetting nothing is not an
/// error. The wire behaviour these underpin is proven against the oracle in
/// <c>JournalResetTests</c>; what the oracle cannot judge — tenant scoping and the index — is pinned
/// here.
/// </summary>
public sealed class JournalResetUnitTests
{
    private static ServeEvent Event(string tenant, string url) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = new TenantId(tenant),
        Request = CanonicalRequestBuilder.Build("GET", url, [], [], "http"),
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Clear_empties_the_tenant()
    {
        var journal = new InMemoryRequestJournal();
        journal.Record(Event("acme", "/a1"));
        journal.Record(Event("acme", "/a2"));

        journal.Clear(new TenantId("acme"));

        Assert.Empty(journal.Query(new TenantId("acme"), new ServeEventQuery()));
    }

    [Fact]
    public void Clear_prunes_the_id_index_too()
    {
        var journal = new InMemoryRequestJournal();
        var recorded = Event("acme", "/a1");
        journal.Record(recorded);

        journal.Clear(new TenantId("acme"));

        // The subtle half: the id index spans tenants, so a Clear that only dropped the tenant's list
        // would leave the detail lookup answering — a "cleared" journal still handing back the request
        // body somebody asked to be rid of.
        Assert.Empty(journal.Query(new TenantId("acme"), new ServeEventQuery { Id = recorded.Id }));
    }

    [Fact]
    public void Clear_leaves_other_tenants_alone()
    {
        var journal = new InMemoryRequestJournal();
        journal.Record(Event("acme", "/a1"));
        var neighbour = Event("globex", "/g1");
        journal.Record(neighbour);

        journal.Clear(new TenantId("acme"));

        // Parallel suites share a host by taking a tenant each; a reset that reached across them would
        // corrupt a neighbour's counts with nothing to show for it.
        Assert.Single(journal.Query(new TenantId("globex"), new ServeEventQuery()));
        Assert.Single(journal.Query(new TenantId("globex"), new ServeEventQuery { Id = neighbour.Id }));
    }

    [Fact]
    public void Clearing_an_unknown_tenant_is_a_no_op()
    {
        var journal = new InMemoryRequestJournal();
        journal.Record(Event("acme", "/a1"));

        journal.Clear(new TenantId("never-used"));

        Assert.Single(journal.Query(new TenantId("acme"), new ServeEventQuery()));
    }

    [Fact]
    public void Recording_after_a_clear_starts_from_empty()
    {
        var journal = new InMemoryRequestJournal(limit: 2);
        journal.Record(Event("acme", "/a1"));
        journal.Record(Event("acme", "/a2"));

        journal.Clear(new TenantId("acme"));
        journal.Record(Event("acme", "/a3"));

        // The bound applies to what is there now, not to what was there before — a reset that left the
        // list object around with a stale count would evict the next test's first request.
        Assert.Equal(["/a3"], journal.Query(new TenantId("acme"), new ServeEventQuery()).Select(e => e.Request.Url));
    }

    [Fact]
    public void The_disabled_journal_accepts_a_clear()
    {
        // Teardown runs whether or not the host kept a journal; --journal-disabled must not turn a
        // suite's cleanup step into a failure.
        var journal = new NullRequestJournal();
        journal.Clear(new TenantId("acme"));

        Assert.Empty(journal.Query(new TenantId("acme"), new ServeEventQuery()));
    }

    [Fact]
    public void Masking_passes_a_clear_through_to_the_real_journal()
    {
        var inner = new InMemoryRequestJournal();
        var journal = new MaskingRequestJournal(inner, JournalMaskingOptions.Parse("Authorization", null));
        journal.Record(Event("acme", "/a1"));

        journal.Clear(new TenantId("acme"));

        // The decorator is transparent for everything but Record; a Clear it swallowed would leave a
        // masked host unable to reset at all.
        Assert.Empty(inner.Query(new TenantId("acme"), new ServeEventQuery()));
    }
}
