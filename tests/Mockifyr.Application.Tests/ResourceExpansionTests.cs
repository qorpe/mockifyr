using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Embedding a related document (#378): the pure half — how a relation is named, what a name that
/// names nothing does, and what the embedded envelope contains. Mockifyr-specific (no oracle has a
/// sandbox resource model), so a self-test, driven through the real in-memory store rather than a
/// stand-in so tenant scoping is exercised by the same code the host runs.
/// </summary>
public sealed class ResourceExpansionTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");

    private static readonly ResourceSchema OrdersSchema =
        new("orders", [new ResourceRelation("customers", "customerId")]);

    private static ResourceDocument Doc(string id, string collection, string body, ResourceLink? parent = null) =>
        new(id, collection, body, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, parent);

    private static IResourceStore StoreWithCustomer()
    {
        var store = new InMemoryResourceStore();
        store.Put(Acme, "customers", "c1", """{"id":"c1","name":"Ada"}""");
        return store;
    }

    // ---- how a relation is named ----------------------------------------------------------------

    [Theory]
    [InlineData("customerId", "customer")]
    [InlineData("customer_id", "customer")]
    [InlineData("customerID", "customer")]
    [InlineData("customer_ID", "customer")]
    [InlineData("customer", "customer")]
    [InlineData("Id", "Id")]
    [InlineData("_id", "_id")]
    public void A_relation_is_named_by_its_key_field_without_the_id_suffix(string via, string expected)
    {
        // The json-server spelling — the vocabulary ADR 0015 chose to adopt rather than reinvent — and
        // the form the embedded document reads as: `customer`, holding a customer. A `via` that IS the
        // suffix keeps its own name: stripping it would leave nothing to call the relation.
        Assert.Equal(expected, ResourceExpansions.NameOf(via));
    }

    // ---- resolving what was asked for ------------------------------------------------------------

    [Fact]
    public void A_relation_answers_to_its_canonical_name_and_to_its_key_field()
    {
        foreach (var spelling in (string[])["customer", "customerId"])
        {
            var plan = ResourceExpansions.Plan([spelling], OrdersSchema);

            Assert.False(plan.IsRefused);
            // Either spelling embeds under the SAME name, so the response shape does not depend on how
            // the caller happened to write the query.
            Assert.Equal(["customer"], plan.Expansions.Select(e => e.Name));
        }
    }

    [Fact]
    public void A_key_field_of_that_exact_name_wins_over_another_relations_stripped_form()
    {
        // A collection declaring both `customer` and `customerId` is odd but legal. Resolving the
        // verbatim field first is the reading with no inference in it.
        var schema = new ResourceSchema("orders", [
            new ResourceRelation("customerIds", "customerId"),
            new ResourceRelation("customers", "customer"),
        ]);

        var plan = ResourceExpansions.Plan(["customer"], schema);

        Assert.Equal("customers", Assert.Single(plan.Expansions).Relation.Collection);
    }

    [Fact]
    public void An_unknown_name_is_refused_by_name_and_says_what_would_have_worked()
    {
        var plan = ResourceExpansions.Plan(["custmoer"], OrdersSchema);

        // A silent no-op is indistinguishable from a typo, which is the whole reason this refuses.
        Assert.True(plan.IsRefused);
        Assert.Equal("custmoer", plan.UnknownName);
        Assert.Contains("custmoer", plan.RefusalMessage);
        Assert.Contains("customer", plan.RefusalMessage);
    }

    [Fact]
    public void The_refusal_reads_as_one_sentence()
    {
        // Pinned exactly rather than by substring: the message IS the feature here — a refusal a caller
        // cannot act on is barely better than the silent no-op it replaced.
        Assert.Equal(
            "Unknown relation 'custmoer'. Declared relations: customer.",
            ResourceExpansions.Plan(["custmoer"], OrdersSchema).RefusalMessage);

        // And a plan that refused nothing has nothing to say.
        Assert.Equal(string.Empty, ResourceExpansions.Plan(["customer"], OrdersSchema).RefusalMessage);

        // Several alternatives are listed as a list, not run together into one word.
        var twoWays = new ResourceSchema("orders", [
            new ResourceRelation("customers", "customerId"),
            new ResourceRelation("warehouses", "warehouseId"),
        ]);

        Assert.Equal(
            "Unknown relation 'custmoer'. Declared relations: customer, warehouse.",
            ResourceExpansions.Plan(["custmoer"], twoWays).RefusalMessage);
    }

    [Fact]
    public void A_collection_that_declares_no_relations_refuses_every_name()
    {
        var plan = ResourceExpansions.Plan(["customer"], schema: null);

        Assert.True(plan.IsRefused);
        Assert.Contains("(none)", plan.RefusalMessage);
    }

    [Fact]
    public void Depth_is_not_reachable_by_spelling_it()
    {
        // ADR 0015 ruled out ?expand=a.b.c. It is not special-cased: a dotted name simply names no
        // declared relation, and the refusal says so.
        Assert.True(ResourceExpansions.Plan(["customer.address"], OrdersSchema).IsRefused);
    }

    [Fact]
    public void Asking_twice_embeds_once()
    {
        var plan = ResourceExpansions.Plan(["customer", "customerId"], OrdersSchema);

        Assert.Single(plan.Expansions);
    }

    [Fact]
    public void Asking_for_nothing_plans_nothing()
    {
        // The shared instance, so the overwhelmingly common request — no _expand at all — allocates
        // nothing and every caller can compare against one value.
        Assert.Same(ExpansionPlan.None, ResourceExpansions.Plan([], OrdersSchema));
        Assert.True(ResourceExpansions.Plan([], OrdersSchema).IsEmpty);
        Assert.False(ResourceExpansions.Plan([], OrdersSchema).IsRefused);
    }

    // ---- what lands in the document --------------------------------------------------------------

    [Fact]
    public void The_parent_is_embedded_under_an_envelope_beside_the_untouched_document()
    {
        var store = StoreWithCustomer();
        var order = Doc("o1", "orders", """{"total":100,"customerId":"c1"}""");
        var plan = ResourceExpansions.Plan(["customer"], OrdersSchema);

        var body = ResourceExpansions.Embed(order.Body, order, plan, Acme, store);

        Assert.Equal(
            """{"total":100,"customerId":"c1","_expand":{"customer":{"id":"c1","name":"Ada"}}}""",
            body);
    }

    [Fact]
    public void A_key_that_lives_in_the_metadata_pointer_expands_the_same_way()
    {
        // The contract declaring no customerId is exactly the case ADR 0015 added the pointer for, and
        // reading it through ParentIdOf is what keeps the two storage shapes one feature.
        var store = StoreWithCustomer();
        var order = Doc("o1", "orders", """{"total":100}""", new ResourceLink("customers", "c1"));

        var body = ResourceExpansions.Embed(
            order.Body, order, ResourceExpansions.Plan(["customer"], OrdersSchema), Acme, store);

        Assert.Equal("""{"total":100,"_expand":{"customer":{"id":"c1","name":"Ada"}}}""", body);
    }

    [Fact]
    public void A_missing_parent_embeds_null_rather_than_failing_the_read()
    {
        var store = StoreWithCustomer();
        var plan = ResourceExpansions.Plan(["customer"], OrdersSchema);

        // Never set, and set to a document that is gone: the caller asked for THIS document, and
        // answering with an error because something beside it is absent would be the worse answer.
        var noKey = Doc("o1", "orders", """{"total":100}""");
        var dangling = Doc("o2", "orders", """{"total":100,"customerId":"gone"}""");

        Assert.Equal("""{"total":100,"_expand":{"customer":null}}""",
            ResourceExpansions.Embed(noKey.Body, noKey, plan, Acme, store));
        Assert.Equal("""{"total":100,"customerId":"gone","_expand":{"customer":null}}""",
            ResourceExpansions.Embed(dangling.Body, dangling, plan, Acme, store));
    }

    [Fact]
    public void A_parent_that_exists_for_another_tenant_is_absent_here()
    {
        var store = StoreWithCustomer();
        var order = Doc("o1", "orders", """{"customerId":"c1"}""");

        var body = ResourceExpansions.Embed(
            order.Body, order, ResourceExpansions.Plan(["customer"], OrdersSchema), Globex, store);

        Assert.Equal("""{"customerId":"c1","_expand":{"customer":null}}""", body);
    }

    [Fact]
    public void Selecting_fields_cannot_make_an_expansion_disappear()
    {
        // The projected body is what the caller receives; the STORED document is what the key is read
        // from. Reading the key from the projection would make ?_fields=total silently un-expand.
        var store = StoreWithCustomer();
        var order = Doc("o1", "orders", """{"total":100,"customerId":"c1"}""");
        var projected = ResourceQuery.Parse([new("_fields", "total")]).Project(order.Body);

        var body = ResourceExpansions.Embed(
            projected, order, ResourceExpansions.Plan(["customer"], OrdersSchema), Acme, store);

        Assert.Equal("""{"total":100,"_expand":{"customer":{"id":"c1","name":"Ada"}}}""", body);
    }

    [Fact]
    public void A_documents_own_expand_field_gives_way_to_the_one_the_request_asked_for()
    {
        // Two properties of one name is not JSON anybody can read reliably, so exactly one survives —
        // the one that answers the request.
        var store = StoreWithCustomer();
        var order = Doc("o1", "orders", """{"customerId":"c1","_expand":"mine"}""");

        var body = ResourceExpansions.Embed(
            order.Body, order, ResourceExpansions.Plan(["customer"], OrdersSchema), Acme, store);

        Assert.Equal("""{"customerId":"c1","_expand":{"customer":{"id":"c1","name":"Ada"}}}""", body);
    }

    [Fact]
    public void A_body_with_nowhere_to_embed_is_returned_untouched()
    {
        // The store accepts any JSON, so an array document is legal. Wrapping it in an object to make
        // room would hand the caller a shape they did not ask for.
        var store = StoreWithCustomer();
        var array = Doc("o1", "orders", """[1,2,3]""");

        Assert.Equal("""[1,2,3]""", ResourceExpansions.Embed(
            array.Body, array, ResourceExpansions.Plan(["customer"], OrdersSchema), Acme, store));
    }

    [Fact]
    public void A_body_that_is_not_json_at_all_is_returned_untouched()
    {
        // Defensive, and reachable: the store itself does not validate — the guards live at the edges —
        // so a document restored from a corrupted backing file must not take the read down with it.
        var store = StoreWithCustomer();
        var broken = Doc("o1", "orders", "not json");

        Assert.Equal("not json", ResourceExpansions.Embed(
            broken.Body, broken, ResourceExpansions.Plan(["customer"], OrdersSchema), Acme, store));
    }

    [Fact]
    public void A_parent_that_is_not_json_embeds_null_rather_than_corrupting_the_response()
    {
        // Writing it through raw would emit a body that is no longer JSON, so one unreadable document
        // would break every read that embeds it.
        var store = new InMemoryResourceStore();
        store.Put(Acme, "customers", "c1", "not json");
        var order = Doc("o1", "orders", """{"customerId":"c1"}""");

        Assert.Equal("""{"customerId":"c1","_expand":{"customer":null}}""", ResourceExpansions.Embed(
            order.Body, order, ResourceExpansions.Plan(["customer"], OrdersSchema), Acme, store));
    }

    [Fact]
    public void An_empty_plan_leaves_the_body_byte_identical()
    {
        var store = StoreWithCustomer();
        var order = Doc("o1", "orders", """{ "total" : 100 }""");

        Assert.Same(order.Body, ResourceExpansions.Embed(order.Body, order, ExpansionPlan.None, Acme, store));
    }

    [Fact]
    public void One_page_reads_a_shared_parent_once()
    {
        // A hundred orders of one customer are a hundred lookups without the memo. Counted through a
        // store that reports how often it was asked.
        var store = new CountingStore(StoreWithCustomer());
        var plan = ResourceExpansions.Plan(["customer"], OrdersSchema);
        var cache = new Dictionary<(string Collection, string Id), string?>();

        foreach (var index in Enumerable.Range(0, 10))
        {
            var order = Doc($"o{index}", "orders", """{"customerId":"c1"}""");
            ResourceExpansions.Embed(order.Body, order, plan, Acme, store, cache);
        }

        Assert.Equal(1, store.Gets);
    }

    [Fact]
    public void A_parent_that_is_absent_is_remembered_as_absent()
    {
        // Without this the memo only helps the happy path, and a page of orphans costs a lookup each.
        var store = new CountingStore(StoreWithCustomer());
        var plan = ResourceExpansions.Plan(["customer"], OrdersSchema);
        var cache = new Dictionary<(string Collection, string Id), string?>();

        foreach (var _ in Enumerable.Range(0, 5))
        {
            var order = Doc("o1", "orders", """{"customerId":"gone"}""");
            Assert.Equal("""{"customerId":"gone","_expand":{"customer":null}}""",
                ResourceExpansions.Embed(order.Body, order, plan, Acme, store, cache));
        }

        Assert.Equal(1, store.Gets);
    }

    /// <summary>A store that answers like the real one and counts how often it was read.</summary>
    private sealed class CountingStore(IResourceStore inner) : IResourceStore
    {
        public int Gets { get; private set; }

        public ResourceDocument? Get(TenantId tenant, string collection, string id)
        {
            Gets++;
            return inner.Get(tenant, collection, id);
        }

        public IReadOnlyList<ResourceCollectionInfo> GetCollections(TenantId tenant) => inner.GetCollections(tenant);
        public IReadOnlyList<ResourceDocument> List(TenantId tenant, string collection) => inner.List(tenant, collection);
        public IReadOnlyCollection<TenantId> GetTenants() => inner.GetTenants();
        public ResourceDocument Put(TenantId tenant, string collection, string id, string body, ResourceLink? parent = null) =>
            inner.Put(tenant, collection, id, body, parent);
        public void Restore(TenantId tenant, ResourceDocument document) => inner.Restore(tenant, document);
        public bool Delete(TenantId tenant, string collection, string id) => inner.Delete(tenant, collection, id);
        public void Reset(TenantId tenant, string collection) => inner.Reset(tenant, collection);
        public void ResetAll(TenantId tenant) => inner.ResetAll(tenant);
    }
}
