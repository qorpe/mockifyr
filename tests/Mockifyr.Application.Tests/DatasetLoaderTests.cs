using Mockifyr.Core;
using Mockifyr.Stores.InMemory;
using Mockifyr.Templating;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Loading and unloading a dataset (#351): dependency order, atomicity, and taking back exactly what
/// was put in. Mockifyr-specific, so a self-test.
/// </summary>
public sealed class DatasetLoaderTests
{
    private static readonly TenantId Acme = new("acme");

    private sealed class Fixture
    {
        public InMemoryResourceStore Documents { get; } = new();
        public IResourceSchemaStore Schemas { get; }
        public DatasetLoader Loader { get; }

        public Fixture(ResourceOptions? options = null)
        {
            Schemas = new ResourceBackedSchemaStore(Documents);
            Loader = new DatasetLoader(Documents, Schemas, new SequentialIds(), options);
        }

        public IEnumerable<string> Ids(string collection) =>
            Documents.List(Acme, collection).Select(d => d.Id);

        private sealed class SequentialIds : IResourceIdGenerator
        {
            private int _next;

            public string NextId(string collection) => $"{collection}-{++_next}";
        }
    }

    private static DatasetItem Item(string collection, int count, string body, string? id = null) =>
        new(collection, count, body, id);

    private const string RandomName = """{"name":"{{random 'Name.fullName'}}"}""";

    [Fact]
    public void A_dataset_lands_in_dependency_order_so_integrity_never_refuses_it()
    {
        // Written child-first on purpose: loading in that order would refuse the order whose customer
        // does not exist yet, so the ordering is what makes the dataset loadable at all.
        var fixture = new Fixture();
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));

        var result = fixture.Loader.Load(Acme, new DatasetDefinition("delinquent", [
            Item("orders", 1, """{"customerId":"customers-1","total":100}"""),
            Item("customers", 1, """{"name":"Ada"}"""),
        ]));

        Assert.True(result.IsLoaded, result.Refusal);
        Assert.Equal(["customers-1"], fixture.Ids("customers"));
        Assert.Equal(["orders-2"], fixture.Ids("orders"));
    }

    [Fact]
    public void A_repeat_count_produces_that_many_documents()
    {
        var fixture = new Fixture();

        var result = fixture.Loader.Load(
            Acme, new DatasetDefinition("many", [Item("customers", 5, RandomName)], Seed: 42));

        Assert.True(result.IsLoaded, result.Refusal);
        Assert.Equal(5, fixture.Documents.List(Acme, "customers").Count);
    }

    [Fact]
    public void The_same_seed_loads_the_same_documents()
    {
        // The reason a dataset can be the basis of a regression test at all.
        var dataset = new DatasetDefinition("people", [Item("customers", 3, RandomName)], Seed: 7);

        var first = new Fixture();
        var second = new Fixture();
        first.Loader.Load(Acme, dataset);
        second.Loader.Load(Acme, dataset);

        Assert.Equal(
            first.Documents.List(Acme, "customers").Select(d => d.Body),
            second.Documents.List(Acme, "customers").Select(d => d.Body));
    }

    [Fact]
    public void Documents_within_one_load_differ_from_each_other()
    {
        // Deterministic must not mean identical: five customers with the same name is not a scenario.
        var fixture = new Fixture();
        fixture.Loader.Load(Acme, new DatasetDefinition("people", [Item("customers", 5, RandomName)], Seed: 7));

        Assert.True(fixture.Documents.List(Acme, "customers").Select(d => d.Body).Distinct().Count() > 1);
    }

    [Fact]
    public void A_load_that_fails_halfway_leaves_nothing_behind()
    {
        // The atomicity requirement, and the one that matters: a half-loaded dataset puts the sandbox in
        // a state no scenario describes, and whoever ran it cannot tell which half they got.
        var fixture = new Fixture();
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));

        var result = fixture.Loader.Load(Acme, new DatasetDefinition("broken", [
            Item("customers", 2, """{"name":"Ada"}"""),
            Item("orders", 1, """{"customerId":"nobody"}"""),
        ]));

        Assert.False(result.IsLoaded);
        Assert.Contains("customers.customerId", result.Refusal!, StringComparison.Ordinal);
        // The two customers that DID land are gone again.
        Assert.Empty(fixture.Documents.List(Acme, "customers"));
        Assert.Empty(fixture.Documents.List(Acme, "orders"));
    }

    [Fact]
    public void A_template_that_renders_to_something_that_is_not_json_is_refused_and_rolled_back()
    {
        var fixture = new Fixture();

        var result = fixture.Loader.Load(Acme, new DatasetDefinition("broken", [
            Item("customers", 1, """{"name":"Ada"}"""),
            Item("notes", 1, """{"text": {{random 'Lorem.word'}} }"""),
        ]));

        Assert.False(result.IsLoaded);
        Assert.Contains("did not render as JSON", result.Refusal!, StringComparison.Ordinal);
        Assert.Contains("notes", result.Refusal!, StringComparison.Ordinal);
        Assert.Empty(fixture.Documents.List(Acme, "customers"));
    }

    [Fact]
    public void Unloading_removes_children_before_parents()
    {
        // A relation defaults to restrict, so unloading in load order would refuse on the customer and
        // leave the whole dataset behind. Reverse order is what makes "reset in one call" true.
        var fixture = new Fixture();
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));

        var result = fixture.Loader.Load(Acme, new DatasetDefinition("d", [
            Item("customers", 1, """{"name":"Ada"}"""),
            Item("orders", 1, """{"customerId":"customers-1"}"""),
        ]));
        Assert.True(result.IsLoaded, result.Refusal);

        var removed = fixture.Loader.Unload(Acme, result.Created);

        Assert.Equal(2, removed);
        Assert.Empty(fixture.Documents.List(Acme, "customers"));
        Assert.Empty(fixture.Documents.List(Acme, "orders"));
    }

    [Fact]
    public void Unloading_leaves_documents_somebody_else_added()
    {
        // "Reset my dataset" must not mean "clear the collections it happens to use" — that would take a
        // colleague's work with it, and people would stop loading datasets.
        var fixture = new Fixture();
        fixture.Documents.Put(Acme, "customers", "not-mine", """{"name":"Grace"}""");

        var result = fixture.Loader.Load(Acme, new DatasetDefinition("d", [Item("customers", 1, """{"name":"Ada"}""")]));
        fixture.Loader.Unload(Acme, result.Created);

        Assert.Equal(["not-mine"], fixture.Ids("customers"));
    }

    [Fact]
    public void A_document_over_the_body_cap_is_refused_and_rolled_back()
    {
        // The cap is the resource store's own (ADR 0011), applied here too: a dataset is just another
        // way to write documents, and a way in that skipped the limit would make the limit decorative.
        var fixture = new Fixture(new ResourceOptions(MaxBodyBytes: 100));
        var oversized = "{\"padding\":\"" + new string('x', 2_000) + "\"}";

        var result = fixture.Loader.Load(Acme, new DatasetDefinition("big", [
            Item("customers", 1, """{"name":"Ada"}"""),
            Item("blobs", 1, oversized),
        ]));

        Assert.False(result.IsLoaded);
        Assert.Contains("exceeds the body limit", result.Refusal!, StringComparison.Ordinal);
        Assert.Contains("blobs", result.Refusal!, StringComparison.Ordinal);
        Assert.Empty(fixture.Documents.List(Acme, "customers"));
    }

    [Fact]
    public void A_document_can_name_the_dataset_it_came_from()
    {
        // So a scenario's data is traceable back to the scenario without a separate convention.
        var fixture = new Fixture();

        fixture.Loader.Load(Acme, new DatasetDefinition("delinquent", [
            Item("customers", 1, """{"scenario":"{{dataset}}"}"""),
        ]));

        Assert.Equal("""{"scenario":"delinquent"}""", fixture.Documents.List(Acme, "customers").Single().Body);
    }

    [Fact]
    public void A_template_that_throws_is_refused_rather_than_taking_the_load_down()
    {
        // An unknown helper throws out of the engine. That has to become a refusal naming the
        // collection, not an exception surfacing as a 500 with the dataset half-loaded.
        var fixture = new Fixture();

        var result = fixture.Loader.Load(Acme, new DatasetDefinition("broken", [
            Item("customers", 1, """{"name":"Ada"}"""),
            Item("notes", 1, """{"text":"{{noSuchHelper 'x'}}"}"""),
        ]));

        Assert.False(result.IsLoaded);
        Assert.Contains("did not render", result.Refusal!, StringComparison.Ordinal);
        Assert.Contains("notes", result.Refusal!, StringComparison.Ordinal);
        Assert.Empty(fixture.Documents.List(Acme, "customers"));
    }

    [Fact]
    public void A_refusal_lists_every_reference_that_does_not_resolve()
    {
        // With one it reads the same either way; two is what pins the separator, and an operator
        // fixing a dataset wants both names at once rather than one per attempt.
        var fixture = new Fixture();
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [
            new ResourceRelation("customers", "customerId"),
            new ResourceRelation("products", "productId"),
        ]));

        var result = fixture.Loader.Load(Acme, new DatasetDefinition("d", [
            Item("orders", 1, """{"customerId":"nobody","productId":"also-nobody"}"""),
        ]));

        Assert.False(result.IsLoaded);
        Assert.Contains("customers.customerId, products.productId", result.Refusal!, StringComparison.Ordinal);
        // The sentence around them, and which collection they were on: the refusal is what an operator
        // reads to find the line they mistyped.
        Assert.Contains("A document for 'orders' references", result.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_definition_is_refused_before_anything_is_written()
    {
        var result = new Fixture().Loader.Load(Acme, new DatasetDefinition("d", []));

        Assert.False(result.IsLoaded);
        Assert.Empty(result.Created);
    }

    [Fact]
    public void An_id_template_may_name_the_documents()
    {
        // So a dataset can reference its own documents: an order pointing at "customer-0" needs that
        // customer to have been given exactly that id.
        var fixture = new Fixture();

        fixture.Loader.Load(Acme, new DatasetDefinition("d", [
            Item("customers", 3, """{"name":"Ada"}""", id: "customer-{{index}}"),
        ]));

        Assert.Equal(["customer-0", "customer-1", "customer-2"], fixture.Ids("customers"));
    }
}
