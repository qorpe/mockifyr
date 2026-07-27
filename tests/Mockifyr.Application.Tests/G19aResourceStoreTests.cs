using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for the sandbox resource store (G19a, ADR 0011). No oracle exists —
/// WireMock has no resource concept — so per the G18 honesty rule this is a self-test suite:
/// create/replace semantics (last-write-wins, version/timestamps), tenant and collection
/// isolation, ring-buffer eviction, deterministic ids via the seam, opaque-body round-trips,
/// and concurrency safety.
/// </summary>
public sealed class G19aResourceStoreTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");
    private static readonly DateTimeOffset T0 = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static (InMemoryResourceStore Store, TestClock Clock) NewStore(int capacity = 100)
    {
        var clock = new TestClock();
        return (new InMemoryResourceStore(capacity, clock), clock);
    }

    /// <summary>A settable clock so create/replace timestamps are asserted exactly, not "recently".</summary>
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = T0;

        public void Advance(TimeSpan by) => Now += by;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Put_creates_with_version_one_and_identical_timestamps()
    {
        var (store, _) = NewStore();

        var doc = store.Put(Acme, "orders", "ord-1", """{"total":10}""");

        Assert.Equal(("ord-1", "orders", """{"total":10}""", 1L), (doc.Id, doc.Collection, doc.Body, doc.Version));
        Assert.Equal(T0, doc.CreatedAt);
        Assert.Equal(T0, doc.UpdatedAt);
    }

    [Fact]
    public void Put_replaces_keeping_created_at_and_position_advancing_version_and_updated_at()
    {
        var (store, clock) = NewStore();
        store.Put(Acme, "orders", "ord-1", """{"v":1}""");
        store.Put(Acme, "orders", "ord-2", """{"v":1}""");

        clock.Advance(TimeSpan.FromMinutes(5));
        var updated = store.Put(Acme, "orders", "ord-1", """{"v":2}""");

        Assert.Equal(2L, updated.Version);
        Assert.Equal(T0, updated.CreatedAt);
        Assert.Equal(T0 + TimeSpan.FromMinutes(5), updated.UpdatedAt);
        // Replacement keeps insertion order — a sandbox listing must not reshuffle on update.
        Assert.Equal(["ord-1", "ord-2"], store.List(Acme, "orders").Select(d => d.Id));
        Assert.Equal("""{"v":2}""", store.Get(Acme, "orders", "ord-1")!.Body);
    }

    [Fact]
    public void Get_and_delete_answer_honestly_for_unknown_ids_and_collections()
    {
        var (store, _) = NewStore();
        store.Put(Acme, "orders", "ord-1", "{}");

        Assert.Null(store.Get(Acme, "orders", "missing"));
        Assert.Null(store.Get(Acme, "unknown", "ord-1"));
        Assert.False(store.Delete(Acme, "orders", "missing"));
        Assert.False(store.Delete(Acme, "unknown", "ord-1"));
        Assert.True(store.Delete(Acme, "orders", "ord-1"));
        Assert.False(store.Delete(Acme, "orders", "ord-1"));
    }

    [Fact]
    public void Tenants_are_fully_isolated()
    {
        var (store, _) = NewStore();
        store.Put(Acme, "orders", "ord-1", """{"owner":"acme"}""");
        store.Put(Globex, "orders", "ord-1", """{"owner":"globex"}""");

        Assert.Equal("""{"owner":"acme"}""", store.Get(Acme, "orders", "ord-1")!.Body);
        Assert.Equal("""{"owner":"globex"}""", store.Get(Globex, "orders", "ord-1")!.Body);

        Assert.True(store.Delete(Acme, "orders", "ord-1"));
        Assert.NotNull(store.Get(Globex, "orders", "ord-1"));
        Assert.DoesNotContain(store.GetCollections(Acme), c => c.Count > 0);
    }

    [Fact]
    public void Collections_are_isolated_within_a_tenant_and_reset_is_scoped()
    {
        var (store, _) = NewStore();
        store.Put(Acme, "orders", "a", "{}");
        store.Put(Acme, "customers", "a", "{}");

        store.Reset(Acme, "orders");

        Assert.Empty(store.List(Acme, "orders"));
        Assert.Single(store.List(Acme, "customers"));

        store.Put(Acme, "orders", "b", "{}");
        store.ResetAll(Acme);
        Assert.Empty(store.GetCollections(Acme));
    }

    [Fact]
    public void GetCollections_reports_name_ordered_counts_per_tenant()
    {
        var (store, _) = NewStore();
        store.Put(Acme, "orders", "a", "{}");
        store.Put(Acme, "orders", "b", "{}");
        store.Put(Acme, "customers", "c", "{}");
        store.Put(Globex, "payments", "p", "{}");

        var collections = store.GetCollections(Acme);

        Assert.Equal([("customers", 1), ("orders", 2)], collections.Select(c => (c.Name, c.Count)));
        Assert.DoesNotContain(collections, c => c.Name == "payments");
    }

    [Fact]
    public void Eviction_drops_the_oldest_document_first_and_updates_do_not_evict()
    {
        var (store, _) = NewStore(capacity: 3);
        store.Put(Acme, "orders", "a", "{}");
        store.Put(Acme, "orders", "b", "{}");
        store.Put(Acme, "orders", "c", "{}");

        // An update at capacity is a replacement, not an addition — nothing may be evicted.
        store.Put(Acme, "orders", "b", """{"v":2}""");
        Assert.Equal(["a", "b", "c"], store.List(Acme, "orders").Select(d => d.Id));

        // The fourth document evicts the oldest (a), ring-buffer style.
        store.Put(Acme, "orders", "d", "{}");
        Assert.Equal(["b", "c", "d"], store.List(Acme, "orders").Select(d => d.Id));
    }

    [Fact]
    public void Hostile_and_unicode_ids_round_trip_as_opaque_keys()
    {
        var (store, _) = NewStore();
        var ids = new[] { "ötö-🙂", "a b/c", "..%2F..", "ID-case", "id-case" };
        foreach (var id in ids)
        {
            store.Put(Acme, "orders", id, $$"""{"id":"{{id}}"}""");
        }

        Assert.Equal(ids.Length, store.List(Acme, "orders").Count);
        foreach (var id in ids)
        {
            Assert.Contains(id, store.Get(Acme, "orders", id)!.Body);
        }
    }

    [Fact]
    public void Sequential_generator_seam_makes_ids_deterministic()
    {
        var generator = new SequentialIds();

        Assert.Equal("orders-1", generator.NextId("orders"));
        Assert.Equal("orders-2", generator.NextId("orders"));
        Assert.Equal("customers-1", generator.NextId("customers"));
    }

    [Fact]
    public async Task Concurrent_puts_and_reads_neither_corrupt_nor_throw()
    {
        var (store, _) = NewStore(capacity: 10_000);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
            {
                store.Put(Acme, "orders", $"w{worker}-{i}", $$"""{"worker":{{worker}},"i":{{i}}}""");
                _ = store.List(Acme, "orders");
                _ = store.Get(Acme, "orders", $"w{worker}-{i}");
            }
        })));

        var documents = store.List(Acme, "orders");
        Assert.Equal(8 * 250, documents.Count);
        Assert.All(documents, d => Assert.EndsWith("}", d.Body));
    }

    [Fact]
    public void Nonpositive_capacity_falls_back_to_the_default_and_positive_capacity_is_kept()
    {
        Assert.Equal(InMemoryResourceStore.DefaultCapacity, new InMemoryResourceStore(0).Capacity);
        Assert.Equal(InMemoryResourceStore.DefaultCapacity, new InMemoryResourceStore(-5).Capacity);
        Assert.Equal(3, new InMemoryResourceStore(3).Capacity);
    }

    [Fact]
    public void A_collection_emptied_by_deletes_disappears_from_the_listing()
    {
        var (store, _) = NewStore();
        store.Put(Acme, "orders", "only", "{}");
        store.Delete(Acme, "orders", "only");

        Assert.Empty(store.GetCollections(Acme));
    }

    private sealed class SequentialIds : IResourceIdGenerator
    {
        private readonly Dictionary<string, int> _next = [];

        public string NextId(string collection)
        {
            var n = _next.GetValueOrDefault(collection) + 1;
            _next[collection] = n;
            return $"{collection}-{n}";
        }
    }
}
