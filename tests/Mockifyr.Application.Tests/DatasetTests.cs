using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// The pure decisions a dataset load rests on (#351): the order collections are loaded in, and
/// whether a definition is usable at all. Mockifyr-specific, so a self-test.
/// </summary>
public sealed class DatasetTests
{
    private static DatasetItem Item(string collection, int count = 1) =>
        new(collection, count, """{"n":1}""");

    private static ResourceSchema BelongsTo(string collection, params string[] parents) =>
        new(collection, [.. parents.Select(p => new ResourceRelation(p, p + "Id"))]);

    private static IEnumerable<string> Order(IReadOnlyList<DatasetItem> items, params ResourceSchema[] schemas) =>
        Datasets.InDependencyOrder(items, schemas).Select(i => i.Collection);

    // ---- ordering -------------------------------------------------------------------------------

    [Fact]
    public void A_parent_is_loaded_before_the_child_that_points_at_it()
    {
        // Referential integrity refuses a child whose parent does not exist yet, so a dataset written
        // in the wrong order would fail on its second item. Asking the author to sort it themselves is
        // asking them to know a relation graph they did not write.
        Assert.Equal(
            ["customers", "orders"],
            Order([Item("orders"), Item("customers")], BelongsTo("orders", "customers")));
    }

    [Fact]
    public void A_chain_is_ordered_all_the_way_down()
    {
        Assert.Equal(
            ["clients", "accounts", "transactions"],
            Order(
                [Item("transactions"), Item("accounts"), Item("clients")],
                BelongsTo("accounts", "clients"),
                BelongsTo("transactions", "accounts")));
    }

    [Fact]
    public void A_parent_that_is_not_part_of_this_dataset_does_not_hold_the_child_back()
    {
        // The customers already exist in the sandbox; waiting for a load that never comes would stall
        // the whole dataset on a dependency nothing here can satisfy.
        Assert.Equal(["orders"], Order([Item("orders")], BelongsTo("orders", "customers")));
    }

    [Fact]
    public void A_cycle_is_loaded_rather_than_refused()
    {
        // employees.managerId -> employees is a real model, and mutual references are legal (ADR 0015).
        // Enforcement is presence-triggered, so a cycle loads with its keys unresolved rather than
        // failing — refusing it here would reject a dataset the store would happily accept.
        var order = Order(
            [Item("a"), Item("b")],
            BelongsTo("a", "b"),
            BelongsTo("b", "a")).ToList();

        Assert.Equal(2, order.Count);
        Assert.Contains("a", order);
        Assert.Contains("b", order);
    }

    [Fact]
    public void A_self_reference_does_not_stall_the_item_on_itself()
    {
        Assert.Equal(["employees"], Order([Item("employees")], BelongsTo("employees", "employees")));
    }

    [Fact]
    public void Independent_collections_keep_the_order_they_were_written_in()
    {
        // Nothing to sort by, so the author's order is the only meaningful one — and a reader
        // comparing two loads should not see them shuffle.
        Assert.Equal(["b", "a", "c"], Order([Item("b"), Item("a"), Item("c")]));
    }

    [Fact]
    public void Every_item_survives_the_ordering()
    {
        // The failure that would be silent: an item dropped by the sort loads nothing and says nothing.
        var items = (IReadOnlyList<DatasetItem>)[Item("orders"), Item("customers"), Item("invoices")];

        Assert.Equal(3, Datasets.InDependencyOrder(items, [BelongsTo("orders", "customers")]).Count);
    }

    [Fact]
    public void An_item_whose_parent_is_absent_still_waits_for_nothing_and_goes_first()
    {
        // The ordering test that a fallback cannot fake. `invoices` depends on nothing in this dataset,
        // `orders` depends on `customers` which IS here — so the only correct order puts customers
        // before orders, with invoices free to go first. Emitting them as written would be wrong.
        Assert.Equal(
            ["invoices", "customers", "orders"],
            Order(
                [Item("invoices"), Item("orders"), Item("customers")],
                BelongsTo("orders", "customers")));
    }

    [Fact]
    public void An_item_that_depends_on_two_others_waits_for_both()
    {
        Assert.Equal(
            ["customers", "products", "orders"],
            Order(
                [Item("orders"), Item("customers"), Item("products")],
                BelongsTo("orders", "customers", "products")));
    }

    // ---- validation -----------------------------------------------------------------------------

    [Fact]
    public void A_usable_dataset_is_accepted()
    {
        Assert.Null(Datasets.Invalid(new DatasetDefinition("delinquent-customer", [Item("customers")])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("9starts-with-a-digit")]
    public void A_name_that_is_not_usable_is_refused(string name)
    {
        var refusal = Datasets.Invalid(new DatasetDefinition(name, [Item("customers")]));

        Assert.NotNull(refusal);
        // The message names what was wrong with it — an operator reading a 422 should not have to
        // guess which of the several rules they broke.
        Assert.Contains("dataset name", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_refused()
    {
        Assert.Null(Datasets.Invalid(new DatasetDefinition(new string('d', 64), [Item("customers")])));
        Assert.NotNull(Datasets.Invalid(new DatasetDefinition(new string('d', 65), [Item("customers")])));
    }

    [Theory]
    [InlineData("has spaces")]
    [InlineData("9starts-with-a-digit")]
    public void A_collection_name_that_is_not_usable_is_refused_too(string collection)
    {
        // The dataset names the collections it writes into, so those go through the same rule the
        // resource store applies — otherwise a dataset could create a collection the admin API cannot
        // address.
        var refusal = Datasets.Invalid(new DatasetDefinition("d", [Item(collection)]));

        Assert.NotNull(refusal);
        Assert.Contains("collection name", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_collection_name_longer_than_the_limit_is_refused()
    {
        Assert.Null(Datasets.Invalid(new DatasetDefinition("d", [Item(new string('c', 64))])));
        Assert.NotNull(Datasets.Invalid(new DatasetDefinition("d", [Item(new string('c', 65))])));
    }

    [Fact]
    public void A_dataset_with_no_collections_is_refused_because_it_would_load_nothing()
    {
        var refusal = Datasets.Invalid(new DatasetDefinition("empty", []));

        Assert.NotNull(refusal);
        Assert.Contains("load nothing", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_count_that_is_not_positive_is_refused(int count)
    {
        var refusal = Datasets.Invalid(new DatasetDefinition("d", [Item("customers", count)]));

        Assert.NotNull(refusal);
        // Naming the collection matters in a dataset with several: "asks for 0 documents" alone
        // leaves the author hunting for which line they mistyped.
        Assert.Contains("customers", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_template_that_is_not_json_is_refused_at_declaration()
    {
        // Caught once, up front, rather than halfway through a load — the same guard the state
        // directive applies, applied before anything is written.
        var refusal = Datasets.Invalid(new DatasetDefinition("d", [new DatasetItem("customers", 1, "not json")]));

        Assert.NotNull(refusal);
        Assert.Contains("customers", refusal, StringComparison.Ordinal);
        Assert.Contains("not JSON", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dataset_larger_than_the_cap_is_refused_and_the_message_says_the_number()
    {
        var dataset = new DatasetDefinition("d", [Item("customers", Datasets.MaxDocuments + 1)]);

        var refusal = Datasets.Invalid(dataset);

        Assert.NotNull(refusal);
        Assert.Contains(Datasets.MaxDocuments.ToString(), refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cap_is_on_the_whole_dataset_not_one_collection()
    {
        // Two items of six thousand each are twelve thousand documents. Checking them separately would
        // let a dataset walk past the bound one collection at a time.
        var half = Datasets.MaxDocuments / 2 + 1;

        Assert.NotNull(Datasets.Invalid(new DatasetDefinition("d", [Item("a", half), Item("b", half)])));
    }

    [Fact]
    public void Exactly_the_cap_is_allowed()
    {
        Assert.Null(Datasets.Invalid(new DatasetDefinition("d", [Item("customers", Datasets.MaxDocuments)])));
    }
}
