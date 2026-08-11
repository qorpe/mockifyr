using System.Text;
using Mockifyr.Core;
using Mockifyr.Stores.InMemory;
using Mockifyr.Templating;

namespace Mockifyr.Application.Tests;

/// <summary>
/// The <c>state</c> directive once it knows about relations (ADR 0015): a nested route scopes what it
/// creates, lists, reads and deletes. Self-tested — no oracle has a sandbox resource model — and the
/// parent ids here are already rendered, because rendering is the renderer's business.
/// </summary>
public sealed class RelationalStateDirectiveTests
{
    private static readonly TenantId Acme = new("acme");

    private sealed class Fixture
    {
        public InMemoryResourceStore Store { get; } = new();

        // The production store, not a test double: relations ride the resource store (ADR 0015), and
        // a fixture that bypassed that would not be exercising what a host actually runs.
        public IResourceSchemaStore Schemas { get; }

        public Fixture() => Schemas = new ResourceBackedSchemaStore(Store);
        public IResourceIdGenerator Ids { get; } = new SequentialIds();

        public StateOutcome Run(StateDirective directive, string? parentId = null, string body = "{}") =>
            StateDirectiveApplier.Apply(
                directive,
                Acme,
                renderedId: directive.Id,
                renderedDocument: null,
                requestBody: Encoding.UTF8.GetBytes(body),
                Store,
                Ids,
                new ResourceOptions(),
                parent: directive.Parent is { } p && parentId is not null ? p with { Id = parentId } : null,
                schemas: Schemas);

        private sealed class SequentialIds : IResourceIdGenerator
        {
            private int _next;

            public string NextId(string collection) => $"{collection}-{++_next}";
        }
    }

    private static readonly StateParent UnderCustomer = new("customers", "unrendered");

    private static StateDirective Create(bool nested = true) =>
        new("create", "orders", Parent: nested ? UnderCustomer : null);

    private static StateDirective List(bool nested = true) =>
        new("list", "orders", Parent: nested ? UnderCustomer : null);

    // ---- creating under a route ----------------------------------------------------------------

    [Fact]
    public void Creating_under_a_customer_that_does_not_exist_is_a_404_on_the_route()
    {
        // POST /customers/99/orders. The route names a resource that is not there, so the answer is
        // about the route — not about the payload, which is fine.
        var fixture = new Fixture();

        Assert.Equal(404, fixture.Run(Create(), parentId: "99").ShortCircuitStatus);
    }

    [Fact]
    public void Creating_with_a_body_reference_that_does_not_resolve_is_a_422_on_the_payload()
    {
        // POST /orders {"customerId":"99"}. The request reached a real place and its content is what
        // is wrong. Collapsing this into the 404 above would misreport one of the two.
        var fixture = new Fixture();
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));

        var outcome = fixture.Run(Create(nested: false), body: """{"customerId":"99"}""");

        Assert.Equal(422, outcome.ShortCircuitStatus);
    }

    [Fact]
    public void A_contract_that_declares_no_key_gets_a_pointer_and_an_untouched_body()
    {
        var fixture = new Fixture();
        fixture.Store.Put(Acme, "customers", "c1", """{"name":"A"}""");
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));

        fixture.Run(Create(), parentId: "c1", body: """{"total":100}""");

        var stored = Assert.Single(fixture.Store.List(Acme, "orders"));
        Assert.Equal(new ResourceLink("customers", "c1"), stored.Parent);
        // The byte-for-byte promise: we did not add a field the modelled contract never declared,
        // which is what would make POST /__admin/openapi/verify report our own sandbox as drifted.
        Assert.Equal("""{"total":100}""", stored.Body);
    }

    [Fact]
    public void A_contract_that_does_declare_the_key_keeps_it_in_the_body_and_stores_no_pointer()
    {
        // Writing it in both places would let the two disagree after an update.
        var fixture = new Fixture();
        fixture.Store.Put(Acme, "customers", "c1", """{"name":"A"}""");
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));

        fixture.Run(Create(), parentId: "c1", body: """{"total":100,"customerId":"c1"}""");

        Assert.Null(Assert.Single(fixture.Store.List(Acme, "orders")).Parent);
    }

    // ---- the defect, end to end ----------------------------------------------------------------

    [Fact]
    public void Each_customer_lists_only_their_own_orders()
    {
        var fixture = new Fixture();
        fixture.Store.Put(Acme, "customers", "c1", """{"name":"A"}""");
        fixture.Store.Put(Acme, "customers", "c2", """{"name":"B"}""");
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));
        fixture.Run(Create(), parentId: "c1", body: """{"total":100}""");
        fixture.Run(Create(), parentId: "c2", body: """{"total":250}""");

        var first = fixture.Run(List(), parentId: "c1").Model!;
        var second = fixture.Run(List(), parentId: "c2").Model!;

        Assert.Equal(1, first["count"]);
        Assert.Equal("""[{"total":100}]""", first["list"]);
        Assert.Equal(1, second["count"]);
        Assert.Equal("""[{"total":250}]""", second["list"]);
    }

    [Fact]
    public void A_real_id_under_the_wrong_parent_is_a_miss()
    {
        // GET /customers/c2/orders/<c1's order>. Guessing an id must not cross the boundary the route
        // draws, or scoping the list was decoration.
        var fixture = new Fixture();
        fixture.Store.Put(Acme, "customers", "c1", "{}");
        fixture.Store.Put(Acme, "customers", "c2", "{}");
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));
        fixture.Run(Create(), parentId: "c1", body: """{"total":100}""");

        var read = new StateDirective("read", "orders", Id: "orders-1", Parent: UnderCustomer);

        Assert.Equal(404, fixture.Run(read, parentId: "c2").ShortCircuitStatus);
        Assert.Null(fixture.Run(read, parentId: "c1").ShortCircuitStatus);
    }

    // ---- deleting ------------------------------------------------------------------------------

    [Fact]
    public void Deleting_a_customer_with_orders_is_refused_by_default()
    {
        var fixture = new Fixture();
        fixture.Store.Put(Acme, "customers", "c1", "{}");
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));
        fixture.Run(Create(), parentId: "c1", body: """{"total":100}""");

        var outcome = fixture.Run(new StateDirective("delete", "customers", Id: "c1"));

        Assert.Equal(409, outcome.ShortCircuitStatus);
        Assert.NotNull(fixture.Store.Get(Acme, "customers", "c1"));
        Assert.Single(fixture.Store.List(Acme, "orders"));
    }

    [Fact]
    public void A_declared_cascade_removes_the_children_with_the_parent()
    {
        var fixture = new Fixture();
        fixture.Store.Put(Acme, "customers", "c1", "{}");
        fixture.Schemas.Put(Acme, new ResourceSchema("orders",
            [new ResourceRelation("customers", "customerId", RelationDeleteRule.Cascade)]));
        fixture.Run(Create(), parentId: "c1", body: """{"total":100}""");

        var outcome = fixture.Run(new StateDirective("delete", "customers", Id: "c1"));

        Assert.Null(outcome.ShortCircuitStatus);
        Assert.Null(fixture.Store.Get(Acme, "customers", "c1"));
        Assert.Empty(fixture.Store.List(Acme, "orders"));
    }

    [Fact]
    public void An_update_that_would_point_a_document_at_nothing_is_refused()
    {
        var fixture = new Fixture();
        fixture.Store.Put(Acme, "customers", "c1", "{}");
        fixture.Schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));
        fixture.Store.Put(Acme, "orders", "o1", """{"customerId":"c1"}""");

        var update = new StateDirective("update", "orders", Id: "o1");
        var outcome = fixture.Run(update, body: """{"customerId":"gone"}""");

        Assert.Equal(422, outcome.ShortCircuitStatus);
        Assert.Equal("""{"customerId":"c1"}""", fixture.Store.Get(Acme, "orders", "o1")!.Body);
    }

    // ---- the compatibility promise -------------------------------------------------------------

    [Fact]
    public void With_no_parent_and_no_schema_nothing_about_the_directive_has_changed()
    {
        // The 1.x guarantee, asserted rather than assumed: a flat collection creates, lists and
        // deletes exactly as it did before relations existed.
        var fixture = new Fixture();
        fixture.Run(Create(nested: false), body: """{"total":100}""");
        fixture.Run(Create(nested: false), body: """{"total":250}""");

        var listed = fixture.Run(List(nested: false)).Model!;
        Assert.Equal(2, listed["count"]);
        Assert.Equal("""[{"total":100},{"total":250}]""", listed["list"]);

        Assert.Null(fixture.Run(new StateDirective("delete", "orders", Id: "orders-1")).ShortCircuitStatus);
        Assert.Equal(1, fixture.Run(List(nested: false)).Model!["count"]);
    }
}
