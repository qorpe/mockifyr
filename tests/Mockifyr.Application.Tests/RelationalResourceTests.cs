using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// The pure relational decisions from ADR 0015: where a document's parent key lives, whether a
/// reference resolves, and what a delete implies. Mockifyr-specific — no oracle has a sandbox
/// resource model at all — so a self-test, with the defect that motivated the ADR pinned first.
/// </summary>
public sealed class RelationalResourceTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");

    private static ResourceDocument Doc(string id, string collection, string body, ResourceLink? parent = null) =>
        new(id, collection, body, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, parent);

    // ---- where the key lives -------------------------------------------------------------------

    [Fact]
    public void A_key_declared_by_the_contract_is_read_from_the_body()
    {
        var order = Doc("1", "orders", """{"total":100,"customerId":"c1"}""");

        Assert.Equal("c1", ResourceRelations.ParentIdOf(order, new ResourceRelation("customers", "customerId")));
    }

    [Fact]
    public void A_numeric_key_names_the_same_parent_as_a_string_one()
    {
        // Whether the spec wrote customerId as an integer or a string is invisible to the person who
        // wrote it, and treating them as different parents would be a silent miss rather than an error.
        var order = Doc("1", "orders", """{"customerId":7}""");

        Assert.Equal("7", ResourceRelations.ParentIdOf(order, new ResourceRelation("customers", "customerId")));
    }

    [Fact]
    public void A_key_the_contract_does_not_declare_lives_in_the_metadata_pointer()
    {
        // The body is byte-identical to what the client sent — ADR 0011's promise, and the reason
        // POST /__admin/openapi/verify cannot report our own sandbox as drifted.
        var order = Doc("1", "orders", """{"total":100}""", new ResourceLink("customers", "c1"));

        Assert.Equal("c1", ResourceRelations.ParentIdOf(order, new ResourceRelation("customers", "customerId")));
        Assert.Equal("""{"total":100}""", order.Body);
    }

    [Fact]
    public void The_body_wins_over_the_pointer_because_it_is_what_a_client_can_see_and_edit()
    {
        var order = Doc("1", "orders", """{"customerId":"from-body"}""", new ResourceLink("customers", "from-pointer"));

        Assert.Equal("from-body", ResourceRelations.ParentIdOf(order, new ResourceRelation("customers", "customerId")));
    }

    [Fact]
    public void A_pointer_to_a_different_collection_does_not_answer_for_this_relation()
    {
        var order = Doc("1", "orders", "{}", new ResourceLink("baskets", "b1"));

        Assert.Null(ResourceRelations.ParentIdOf(order, new ResourceRelation("customers", "customerId")));
    }

    [Theory]
    [InlineData("[1,2,3]")]                 // not an object
    [InlineData("not json at all")]         // hand-edited into nonsense
    [InlineData("""{"customerId":null}""")] // present but empty
    [InlineData("""{"customerId":{}}""")]   // present but not a scalar
    public void A_body_that_cannot_yield_a_key_yields_none_rather_than_throwing(string body)
    {
        Assert.Null(ResourceRelations.ParentIdOf(Doc("1", "orders", body), new ResourceRelation("customers", "customerId")));
    }

    // ---- the defect this ADR exists for --------------------------------------------------------

    [Fact]
    public void One_customers_orders_are_not_another_customers_orders()
    {
        // The trace at the top of ADR 0015, as a test. Before relations, both lists were the whole
        // collection and each modelled customer saw the other's order.
        var store = new InMemoryResourceStore();
        store.Put(Acme, "orders", "o1", """{"total":100,"customerId":"c1"}""");
        store.Put(Acme, "orders", "o2", """{"total":250,"customerId":"c2"}""");

        Assert.Equal(["o1"], store.Find(Acme, "orders", "customerId", "c1").Select(d => d.Id));
        Assert.Equal(["o2"], store.Find(Acme, "orders", "customerId", "c2").Select(d => d.Id));
    }

    [Fact]
    public void A_scoped_list_finds_children_held_by_the_metadata_pointer_too()
    {
        // The failure this exists to stop: scoping built on a body-field lookup returns nothing at all
        // for a collection whose contract declares no key — which is precisely the collection the
        // pointer was added for. Both storage forms have to answer the same question.
        var store = new InMemoryResourceStore();
        var schema = new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]);
        store.Put(Acme, "orders", "o1", """{"total":100}""", new ResourceLink("customers", "c1"));
        store.Put(Acme, "orders", "o2", """{"total":250}""", new ResourceLink("customers", "c2"));
        store.Put(Acme, "orders", "o3", """{"total":50,"customerId":"c1"}""");

        var mine = ResourceRelations.ChildrenOf(Acme, "orders", schema, "customers", "c1", store);

        Assert.Equal(["o1", "o3"], mine.Select(d => d.Id));
        Assert.Empty(store.Find(Acme, "orders", "customerId", "c2"));
    }

    [Fact]
    public void A_collection_that_declares_nothing_still_lists_everything()
    {
        // The compatibility promise, as an assertion: no schema, no scoping, byte-for-byte 1.x.
        var store = new InMemoryResourceStore();
        store.Put(Acme, "orders", "o1", "{}");
        store.Put(Acme, "orders", "o2", "{}");

        Assert.Equal(["o1", "o2"],
            ResourceRelations.ChildrenOf(Acme, "orders", schema: null, "customers", "c1", store).Select(d => d.Id));
    }

    [Fact]
    public void A_relation_never_reaches_across_tenants()
    {
        // The one place a relation could have become a cross-tenant read: the parent exists, just not
        // for this tenant. It has to be a miss, not a hit.
        var store = new InMemoryResourceStore();
        var schema = new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]);
        store.Put(Globex, "customers", "c1", """{"name":"theirs"}""");

        var unresolved = ResourceRelations.UnresolvedReferences("""{"customerId":"c1"}""", schema, Acme, store);

        Assert.Equal(["customers"], unresolved.Select(r => r.Collection));
    }

    // ---- integrity is presence-triggered -------------------------------------------------------

    [Fact]
    public void A_reference_to_a_parent_that_does_not_exist_is_unresolved()
    {
        var store = new InMemoryResourceStore();
        var schema = new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]);

        Assert.Single(ResourceRelations.UnresolvedReferences("""{"customerId":"99"}""", schema, Acme, store));
    }

    [Fact]
    public void A_reference_to_a_parent_that_exists_resolves()
    {
        var store = new InMemoryResourceStore();
        store.Put(Acme, "customers", "c1", """{"name":"A"}""");
        var schema = new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]);

        Assert.Empty(ResourceRelations.UnresolvedReferences("""{"customerId":"c1"}""", schema, Acme, store));
    }

    [Fact]
    public void An_absent_key_is_not_checked_so_mutually_referencing_collections_stay_creatable()
    {
        // If a declared relation were mandatory, two collections that reference each other could never
        // be populated: neither can be created first. Presence-triggered enforcement is what avoids
        // that trap, and it is the reason cycles in the relation graph are legal.
        var store = new InMemoryResourceStore();
        var schema = new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]);

        Assert.Empty(ResourceRelations.UnresolvedReferences("""{"total":100}""", schema, Acme, store));
    }

    [Fact]
    public void A_collection_with_no_schema_is_unchanged_from_before_relations_existed()
    {
        var store = new InMemoryResourceStore();

        Assert.Empty(ResourceRelations.UnresolvedReferences("""{"customerId":"99"}""", schema: null, Acme, store));
    }

    // ---- where relations themselves live -------------------------------------------------------

    [Fact]
    public void A_relation_is_stored_as_a_document_so_it_persists_wherever_documents_do()
    {
        // The point of riding the resource store: relations held only in memory would vanish on
        // restart while their documents survived, and a scoped list would quietly answer with the
        // whole collection again — this ADR's defect, back at the moment nobody is watching.
        var store = new InMemoryResourceStore();
        var schemas = new ResourceBackedSchemaStore(store);

        schemas.Put(Acme, new ResourceSchema("orders",
            [new ResourceRelation("customers", "customerId", RelationDeleteRule.Cascade)]));

        // Reading it back through a SECOND instance is the assertion that nothing is held in the
        // store object itself: all of the state is in the documents a backend already persists.
        var reread = new ResourceBackedSchemaStore(store).Get(Acme, "orders");

        Assert.Equal("orders", reread!.Collection);
        // Asserted field by field: a record's generated equality compares IReadOnlyList by reference,
        // so comparing whole schemas would fail for two structurally identical values.
        var relation = Assert.Single(reread.BelongsTo);
        Assert.Equal("customers", relation.Collection);
        Assert.Equal("customerId", relation.Via);
        Assert.Equal(RelationDeleteRule.Cascade, relation.OnDelete);
    }

    [Fact]
    public void The_reserved_collection_never_appears_in_a_tenants_listing()
    {
        var store = new InMemoryResourceStore();
        new ResourceBackedSchemaStore(store).Put(Acme, new ResourceSchema("orders", []));
        store.Put(Acme, "orders", "o1", "{}");

        Assert.Equal(["orders"], store.GetCollections(Acme).Select(c => c.Name));
    }

    [Fact]
    public void No_tenant_can_name_a_collection_that_would_collide_with_it()
    {
        // Structural, not a convention: the reserved name fails the validator every user-facing path
        // applies, so there is no request that could reach it.
        Assert.False(ReservedEnvironmentKeys.IsWellFormed(ResourceRelations.SchemaCollection));
    }

    [Fact]
    public void A_declaration_that_does_not_parse_is_dropped_rather_than_taking_the_sandbox_down()
    {
        // This storage is reachable by a restore or a hand-edited file. One unreadable row must not
        // make every relation in the tenant unreadable.
        var store = new InMemoryResourceStore();
        var schemas = new ResourceBackedSchemaStore(store);
        schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId")]));
        store.Put(Acme, ResourceRelations.SchemaCollection, "broken", "not json");

        Assert.Equal(["orders"], schemas.List(Acme).Select(schema => schema.Collection));
        Assert.Null(schemas.Get(Acme, "broken"));
    }

    // ---- what a delete implies -----------------------------------------------------------------

    private static (InMemoryResourceStore Store, IResourceSchemaStore Schemas) Sandbox(
        RelationDeleteRule rule)
    {
        var store = new InMemoryResourceStore();
        var schemas = new ResourceBackedSchemaStore(store);
        schemas.Put(Acme, new ResourceSchema("orders", [new ResourceRelation("customers", "customerId", rule)]));
        store.Put(Acme, "customers", "c1", """{"name":"A"}""");
        store.Put(Acme, "orders", "o1", """{"customerId":"c1"}""");
        store.Put(Acme, "orders", "o2", """{"customerId":"c1"}""");
        store.Put(Acme, "orders", "o3", """{"customerId":"c2"}""");
        return (store, schemas);
    }

    [Fact]
    public void Restrict_refuses_the_delete_and_names_what_is_in_the_way()
    {
        var (store, schemas) = Sandbox(RelationDeleteRule.Restrict);

        var plan = ResourceRelations.PlanDelete(Acme, "customers", "c1", schemas, store);

        Assert.False(plan.IsAllowed);
        // The count is the whole value of reporting this rather than a bare 409: two orders, not "some".
        Assert.Equal([new RestrictedRelation("orders", "customerId", 2)], plan.Restricted);
        Assert.Empty(plan.Doomed);
    }

    [Fact]
    public void Cascade_collects_the_children_and_nothing_else()
    {
        var (store, schemas) = Sandbox(RelationDeleteRule.Cascade);

        var plan = ResourceRelations.PlanDelete(Acme, "customers", "c1", schemas, store);

        Assert.True(plan.IsAllowed);
        // o3 belongs to c2 and must survive — the assertion that a cascade is scoped, not a Reset.
        Assert.Equal(["o1", "o2"], plan.Doomed.Select(d => d.Id));
    }

    [Fact]
    public void Orphan_deletes_the_parent_and_leaves_the_children_alone()
    {
        var (store, schemas) = Sandbox(RelationDeleteRule.Orphan);

        var plan = ResourceRelations.PlanDelete(Acme, "customers", "c1", schemas, store);

        Assert.True(plan.IsAllowed);
        Assert.Empty(plan.Doomed);
    }

    [Fact]
    public void Restrict_allows_the_delete_once_nothing_points_at_it()
    {
        var (store, schemas) = Sandbox(RelationDeleteRule.Restrict);
        store.Delete(Acme, "orders", "o1");
        store.Delete(Acme, "orders", "o2");

        Assert.True(ResourceRelations.PlanDelete(Acme, "customers", "c1", schemas, store).IsAllowed);
    }

    [Fact]
    public void A_cascade_down_a_chain_reaches_the_bottom()
    {
        var store = new InMemoryResourceStore();
        var schemas = new ResourceBackedSchemaStore(store);
        schemas.Put(Acme, new ResourceSchema("accounts", [new ResourceRelation("clients", "clientId", RelationDeleteRule.Cascade)]));
        schemas.Put(Acme, new ResourceSchema("transactions", [new ResourceRelation("accounts", "accountId", RelationDeleteRule.Cascade)]));
        store.Put(Acme, "clients", "cl1", "{}");
        store.Put(Acme, "accounts", "a1", """{"clientId":"cl1"}""");
        store.Put(Acme, "transactions", "t1", """{"accountId":"a1"}""");

        var plan = ResourceRelations.PlanDelete(Acme, "clients", "cl1", schemas, store);

        Assert.True(plan.IsAllowed);
        Assert.Equal([("accounts", "a1"), ("transactions", "t1")], plan.Doomed.Select(d => (d.Collection, d.Id)));
    }

    [Fact]
    public void A_self_referencing_collection_is_a_real_model_and_terminates()
    {
        // employees.managerId → employees is legal, and a cycle in the data (two employees managing
        // each other) is what the visited set exists for. Without it this call never returns.
        var store = new InMemoryResourceStore();
        var schemas = new ResourceBackedSchemaStore(store);
        schemas.Put(Acme, new ResourceSchema("employees", [new ResourceRelation("employees", "managerId", RelationDeleteRule.Cascade)]));
        store.Put(Acme, "employees", "e1", """{"managerId":"e2"}""");
        store.Put(Acme, "employees", "e2", """{"managerId":"e1"}""");

        var plan = ResourceRelations.PlanDelete(Acme, "employees", "e1", schemas, store);

        Assert.True(plan.IsAllowed);
        Assert.Equal(["e2"], plan.Doomed.Select(d => d.Id));
    }

    [Fact]
    public void A_document_is_never_scheduled_for_deletion_twice()
    {
        // Two relations can reach the same child (an order belonging to a customer and to a basket the
        // same delete removes). Deleting it twice is harmless; reporting it twice is a wrong count.
        var store = new InMemoryResourceStore();
        var schemas = new ResourceBackedSchemaStore(store);
        schemas.Put(Acme, new ResourceSchema("orders",
        [
            new ResourceRelation("customers", "customerId", RelationDeleteRule.Cascade),
            new ResourceRelation("customers", "ownerId", RelationDeleteRule.Cascade),
        ]));
        store.Put(Acme, "customers", "c1", "{}");
        store.Put(Acme, "orders", "o1", """{"customerId":"c1","ownerId":"c1"}""");

        Assert.Equal(["o1"], ResourceRelations.PlanDelete(Acme, "customers", "c1", schemas, store).Doomed.Select(d => d.Id));
    }
}
