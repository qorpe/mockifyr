using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for the two store entry points change-feed reload needs (#279):
/// <c>Restore</c> (mirroring another instance's document verbatim) and <c>GetTenants</c> (so a tenant
/// emptied elsewhere can be pruned here). No oracle exists for either — they are coherence mechanics,
/// not dialect behaviour — so this is a self-test suite, and the wire behaviour it underpins is proven
/// end to end against both shared backends in <c>ChangeFeedEnvironmentResourceTests</c>.
/// </summary>
public sealed class ResourceStoreRestoreTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");
    private static readonly DateTimeOffset Written = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static InMemoryResourceStore NewStore(int capacity = 100) =>
        new(capacity, new FixedClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    /// <summary>
    /// A clock set far from the documents' own timestamps, so a restore that stamped the local time
    /// instead of preserving the writer's would be unmistakable rather than a near-miss.
    /// </summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ResourceDocument Document(string id, string body, long version) =>
        new(id, "orders", body, Written, Written.AddMinutes(version), version);

    [Fact]
    public void Restore_keeps_the_writer_version_and_timestamps()
    {
        var store = NewStore();

        store.Restore(Acme, Document("ord-1", """{"total":10}""", 7));

        var restored = store.Get(Acme, "orders", "ord-1");
        Assert.NotNull(restored);
        Assert.Equal(7, restored.Version);
        Assert.Equal(Written, restored.CreatedAt);
        Assert.Equal(Written.AddMinutes(7), restored.UpdatedAt);
        Assert.Equal("""{"total":10}""", restored.Body);
    }

    [Fact]
    public void Restoring_the_same_document_repeatedly_does_not_advance_its_version()
    {
        var store = NewStore();
        var document = Document("ord-1", """{"total":10}""", 2);

        // Every announcement on the feed triggers a reload, including announcements about something
        // else entirely. If a repeated restore drifted, two replicas of one backend would disagree
        // about a document neither of them changed.
        store.Restore(Acme, document);
        store.Restore(Acme, document);
        store.Restore(Acme, document);

        Assert.Equal(2, store.Get(Acme, "orders", "ord-1")!.Version);
    }

    [Fact]
    public void Restore_replaces_in_place_and_keeps_the_insertion_order()
    {
        var store = NewStore();
        store.Restore(Acme, Document("first", "{}", 1));
        store.Restore(Acme, Document("second", "{}", 1));

        store.Restore(Acme, Document("first", """{"changed":true}""", 2));

        var listed = store.List(Acme, "orders");
        Assert.Equal(["first", "second"], listed.Select(document => document.Id));
        Assert.Equal("""{"changed":true}""", listed[0].Body);
        Assert.Equal(2, listed[0].Version);
    }

    [Fact]
    public void Restore_keeps_tenants_apart_under_one_id()
    {
        var store = NewStore();

        store.Restore(Acme, Document("ord-1", """{"total":10}""", 1));
        store.Restore(Globex, Document("ord-1", """{"total":99}""", 1));

        Assert.Equal("""{"total":10}""", store.Get(Acme, "orders", "ord-1")!.Body);
        Assert.Equal("""{"total":99}""", store.Get(Globex, "orders", "ord-1")!.Body);
    }

    [Fact]
    public void Restore_respects_the_per_collection_bound()
    {
        var store = NewStore(capacity: 2);

        store.Restore(Acme, Document("a", "{}", 1));
        store.Restore(Acme, Document("b", "{}", 1));
        store.Restore(Acme, Document("c", "{}", 1));

        // A restore is a document arriving like any other: an unattended replica must not grow past the
        // bound just because the documents came over the feed instead of the admin API.
        Assert.Equal(["b", "c"], store.List(Acme, "orders").Select(document => document.Id));
    }

    [Fact]
    public void GetTenants_reports_only_tenants_that_still_hold_a_document()
    {
        var store = NewStore();
        store.Restore(Acme, Document("ord-1", "{}", 1));
        store.Restore(Globex, Document("ord-2", "{}", 1));

        Assert.Equal(["acme", "globex"], store.GetTenants().Select(tenant => tenant.Value).Order());

        // Emptied, not merely reset: a tenant still listed here would be reconciled forever, and one
        // dropped too early would never have its leftovers pruned.
        store.Delete(Globex, "orders", "ord-2");

        Assert.Equal(["acme"], store.GetTenants().Select(tenant => tenant.Value));
    }

    [Fact]
    public void GetTenants_is_empty_on_a_fresh_store()
    {
        Assert.Empty(NewStore().GetTenants());
    }

    [Fact]
    public void A_tenant_whose_collections_were_reset_is_no_longer_reported()
    {
        var store = NewStore();
        store.Restore(Acme, Document("ord-1", "{}", 1));

        store.Reset(Acme, "orders");

        Assert.Empty(store.GetTenants());
    }
}
