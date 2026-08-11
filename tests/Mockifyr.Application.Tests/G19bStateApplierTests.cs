using System.Text;
using Mockifyr.Core;
using Mockifyr.Stores.InMemory;
using Mockifyr.Templating;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for <see cref="StateDirectiveApplier"/> (G19b, ADR 0011). The wire loop is
/// proven end-to-end in <c>G19bStateDirectiveTests</c>; these tests own the semantics table the
/// wire only samples: per-operation dispatch, create-id generation vs explicit ids, the
/// request-body fallback, whitespace-id normalization, both serve-time guards at their boundary,
/// configurable miss statuses, and the 422 refusals for unknown operations and malformed
/// collection names.
/// </summary>
public sealed class G19bStateApplierTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly ResourceOptions SmallCap = new(MaxBodyBytes: 64);

    private static InMemoryResourceStore Store() => new();

    private static StateOutcome Apply(
        InMemoryResourceStore store, StateDirective directive,
        string? renderedId = null, string? renderedDocument = null, string requestBody = "{}") =>
        StateDirectiveApplier.Apply(
            directive, Acme, renderedId, renderedDocument, Encoding.UTF8.GetBytes(requestBody),
            store, new SequentialIds(), SmallCap);

    [Fact]
    public void Create_generates_an_id_when_none_is_rendered_and_stores_the_request_body()
    {
        var store = Store();
        var outcome = Apply(store, new StateDirective("create", "orders"), requestBody: """{"item":"book"}""");

        Assert.Null(outcome.ShortCircuitStatus);
        Assert.Equal("orders-1", outcome.Model!["id"]);
        Assert.Equal("""{"item":"book"}""", outcome.Model["body"]);
        Assert.Equal(1L, outcome.Model["version"]);
        Assert.Equal("""{"item":"book"}""", store.Get(Acme, "orders", "orders-1")!.Body);
    }

    [Fact]
    public void Create_prefers_the_rendered_id_and_document_over_the_defaults()
    {
        var store = Store();
        var outcome = Apply(store, new StateDirective("create", "orders"),
            renderedId: "  ord-7  ", renderedDocument: """{"explicit":true}""", requestBody: """{"ignored":true}""");

        Assert.Equal("ord-7", outcome.Model!["id"]);
        Assert.Equal("""{"explicit":true}""", store.Get(Acme, "orders", "ord-7")!.Body);
    }

    [Fact]
    public void Read_returns_the_document_or_short_circuits_to_the_miss_status()
    {
        var store = Store();
        store.Put(Acme, "orders", "ord-1", """{"n":1}""");

        Assert.Equal("""{"n":1}""", Apply(store, new StateDirective("read", "orders"), "ord-1").Model!["body"]);
        Assert.Equal(404, Apply(store, new StateDirective("read", "orders"), "missing").ShortCircuitStatus);
        Assert.Equal(410, Apply(store, new StateDirective("read", "orders", MissStatus: 410), "missing").ShortCircuitStatus);
        // No id rendered at all is a miss, not an exception.
        Assert.Equal(404, Apply(store, new StateDirective("read", "orders"), renderedId: "   ").ShortCircuitStatus);
    }

    [Fact]
    public void Update_misses_for_unknown_ids_and_replaces_known_ones()
    {
        var store = Store();
        store.Put(Acme, "orders", "ord-1", """{"v":1}""");

        Assert.Equal(404, Apply(store, new StateDirective("update", "orders"), "missing").ShortCircuitStatus);

        var outcome = Apply(store, new StateDirective("update", "orders"), "ord-1", requestBody: """{"v":2}""");
        Assert.Equal(2L, outcome.Model!["version"]);
        Assert.Equal("""{"v":2}""", store.Get(Acme, "orders", "ord-1")!.Body);
    }

    [Fact]
    public void Delete_reports_the_id_on_success_and_misses_otherwise()
    {
        var store = Store();
        store.Put(Acme, "orders", "ord-1", "{}");

        var outcome = Apply(store, new StateDirective("delete", "orders"), "ord-1");
        Assert.Equal("ord-1", outcome.Model!["id"]);
        Assert.Null(store.Get(Acme, "orders", "ord-1"));

        Assert.Equal(404, Apply(store, new StateDirective("delete", "orders"), "ord-1").ShortCircuitStatus);
        Assert.Equal(404, Apply(store, new StateDirective("delete", "orders")).ShortCircuitStatus);
    }

    [Fact]
    public void List_renders_a_count_and_a_json_array_of_bodies()
    {
        var store = Store();
        var empty = Apply(store, new StateDirective("list", "orders"));
        Assert.Equal(0, empty.Model!["count"]);
        Assert.Equal("[]", empty.Model["list"]);

        store.Put(Acme, "orders", "a", """{"n":1}""");
        store.Put(Acme, "orders", "b", """{"n":2}""");
        var listed = Apply(store, new StateDirective("list", "orders"));
        Assert.Equal(2, listed.Model!["count"]);
        Assert.Equal("""[{"n":1},{"n":2}]""", listed.Model["list"]);
    }

    [Fact]
    public void The_guards_hold_at_their_exact_boundaries_for_create_and_update()
    {
        var store = Store();
        var atCap = $$"""{"p":"{{new string('x', 56)}}"}""";
        Assert.Null(Apply(store, new StateDirective("create", "orders"), requestBody: atCap).ShortCircuitStatus);

        var overCap = $$"""{"p":"{{new string('x', 57)}}"}""";
        Assert.Equal(413, Apply(store, new StateDirective("create", "orders"), requestBody: overCap).ShortCircuitStatus);
        Assert.Equal(422, Apply(store, new StateDirective("create", "orders"), requestBody: "{nope").ShortCircuitStatus);

        store.Put(Acme, "orders", "ord-1", "{}");
        Assert.Equal(413, Apply(store, new StateDirective("update", "orders"), "ord-1", requestBody: overCap).ShortCircuitStatus);
        Assert.Equal(422, Apply(store, new StateDirective("update", "orders"), "ord-1", requestBody: "{nope").ShortCircuitStatus);
    }

    [Fact]
    public void Unknown_operations_and_malformed_collections_refuse_with_422()
    {
        var store = Store();
        Assert.Equal(422, Apply(store, new StateDirective("upsert", "orders")).ShortCircuitStatus);
        Assert.Equal(422, Apply(store, new StateDirective("create", "9bad name")).ShortCircuitStatus);
        Assert.Equal(422, Apply(store, new StateDirective("create", "a" + new string('x', 64))).ShortCircuitStatus);
        Assert.Empty(store.GetCollections(Acme));
    }

    [Fact]
    public void Operation_dispatch_is_case_insensitive()
    {
        var store = Store();
        Assert.Null(Apply(store, new StateDirective("CREATE", "orders")).ShortCircuitStatus);
        Assert.Null(Apply(store, new StateDirective("List", "orders")).ShortCircuitStatus);
    }

    [Fact]
    public void A_collection_name_of_exactly_64_characters_is_accepted()
    {
        var outcome = Apply(Store(), new StateDirective("create", "a" + new string('x', 63)));
        Assert.Null(outcome.ShortCircuitStatus);
    }

    [Fact]
    public void Update_prefers_the_rendered_document_over_the_request_body()
    {
        var store = Store();
        store.Put(Acme, "orders", "ord-1", """{"v":1}""");

        Apply(store, new StateDirective("update", "orders"), "ord-1",
            renderedDocument: """{"explicit":true}""", requestBody: """{"ignored":true}""");

        Assert.Equal("""{"explicit":true}""", store.Get(Acme, "orders", "ord-1")!.Body);
    }

    [Fact]
    public void A_read_with_no_rendered_id_never_queries_the_store()
    {
        var probe = new ProbeStore();
        var outcome = StateDirectiveApplier.Apply(
            new StateDirective("read", "orders"), Acme, renderedId: null, renderedDocument: null,
            requestBody: [], probe, new SequentialIds(), SmallCap);

        Assert.Equal(404, outcome.ShortCircuitStatus);
        Assert.Equal(0, probe.GetCalls);
    }

    /// <summary>Counts Get calls so "no id, no lookup" is observable, not assumed.</summary>
    private sealed class ProbeStore : IResourceStore
    {
        public int GetCalls { get; private set; }

        public IReadOnlyList<ResourceCollectionInfo> GetCollections(TenantId tenant) => [];
        public IReadOnlyList<ResourceDocument> List(TenantId tenant, string collection) => [];
        public IReadOnlyCollection<TenantId> GetTenants() => [];
        public ResourceDocument? Get(TenantId tenant, string collection, string id) { GetCalls++; return null; }
        public ResourceDocument Put(TenantId tenant, string collection, string id, string body, ResourceLink? parent = null) =>
            new(id, collection, body, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, parent);
        public void Restore(TenantId tenant, ResourceDocument document) { }
        public bool Delete(TenantId tenant, string collection, string id) => false;
        public void Reset(TenantId tenant, string collection) { }
        public void ResetAll(TenantId tenant) { }
    }

    private sealed class SequentialIds : IResourceIdGenerator
    {
        private int _next;

        public string NextId(string collection) => $"{collection}-{++_next}";
    }
}
