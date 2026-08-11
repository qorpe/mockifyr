using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Resource querying (#353): filter, sort and field selection over a collection. Pure, so unit-tested;
/// the same parsed query serves the admin listing and the serve-time <c>list</c>.
/// </summary>
public sealed class ResourceQueryTests
{
    private static ResourceDocument Doc(string id, string body) =>
        new(id, "orders", body, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1);

    private static readonly IReadOnlyList<ResourceDocument> Orders =
    [
        Doc("o1", """{"status":"settled","total":100,"note":"first order"}"""),
        Doc("o2", """{"status":"pending","total":9}"""),
        Doc("o3", """{"status":"settled","total":250,"note":"rush"}"""),
    ];

    private static ResourceQuery Query(params (string Key, string? Value)[] parameters) =>
        ResourceQuery.Parse(parameters.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)));

    private static IEnumerable<string> Ids(IReadOnlyList<ResourceDocument> documents) => documents.Select(d => d.Id);

    // ---- filtering ------------------------------------------------------------------------------

    [Fact]
    public void A_bare_parameter_is_an_equality_filter()
    {
        Assert.Equal(["o1", "o3"], Ids(Query(("status", "settled")).Apply(Orders)));
    }

    [Fact]
    public void A_number_compares_the_way_it_was_written_in_the_document()
    {
        // ?total=100 has to find {"total":100}. Requiring the caller to know whether the spec wrote it
        // as a number or a string would make the filter unusable.
        Assert.Equal(["o1"], Ids(Query(("total", "100")).Apply(Orders)));
    }

    [Fact]
    public void Filters_combine_with_and()
    {
        // What a query string means everywhere else, and the only reading that needs no explaining.
        Assert.Equal(["o3"], Ids(Query(("status", "settled"), ("total", "250")).Apply(Orders)));
    }

    [Fact]
    public void Contains_and_matches_use_the_words_the_dialect_already_proves()
    {
        Assert.Equal(["o1"], Ids(Query(("note:contains", "first")).Apply(Orders)));
        Assert.Equal(["o3"], Ids(Query(("note:matches", "^ru.h$")).Apply(Orders)));
    }

    [Fact]
    public void Absent_asks_whether_the_field_is_there_at_all()
    {
        Assert.Equal(["o2"], Ids(Query(("note:absent", "true")).Apply(Orders)));
        Assert.Equal(["o1", "o3"], Ids(Query(("note:absent", "false")).Apply(Orders)));
    }

    [Fact]
    public void An_unknown_suffix_is_part_of_the_field_name_rather_than_a_refusal()
    {
        // "?created:at=x" is somebody filtering a field called created:at. Guessing otherwise would
        // refuse a legitimate query for looking like a typo.
        var documents = (IReadOnlyList<ResourceDocument>)[Doc("a", """{"created:at":"today"}""")];

        Assert.Equal(["a"], Ids(Query(("created:at", "today")).Apply(documents)));
    }

    [Fact]
    public void A_pattern_that_does_not_compile_matches_nothing_instead_of_throwing()
    {
        // The pattern arrives in a query string, so it is caller-controlled and reaches the serving
        // path. An exception there would turn a bad filter into a broken sandbox.
        Assert.Empty(Query(("note:matches", "([unclosed")).Apply(Orders));
    }

    // ---- sorting --------------------------------------------------------------------------------

    [Fact]
    public void Sorting_is_numeric_when_both_values_are_numbers()
    {
        // The reason this needs saying: as text, "9" sorts after "250" — correct for strings and wrong
        // for the column of totals people actually sort by.
        Assert.Equal(["o2", "o1", "o3"], Ids(Query(("_sort", "total")).Apply(Orders)));
    }

    [Fact]
    public void A_leading_minus_reverses_it()
    {
        Assert.Equal(["o3", "o1", "o2"], Ids(Query(("_sort", "-total")).Apply(Orders)));
    }

    [Fact]
    public void Text_sorts_as_text()
    {
        Assert.Equal(["o2", "o1", "o3"], Ids(Query(("_sort", "status")).Apply(Orders)));
    }

    [Fact]
    public void A_document_missing_the_sort_field_goes_last_in_either_direction()
    {
        // Absent is not "smallest". Burying it at the end is what a reader expects whichever way they
        // sorted, and putting it first in one direction would look like a bug in the data.
        Assert.Equal(["o1", "o3", "o2"], Ids(Query(("_sort", "note")).Apply(Orders)));
        Assert.Equal(["o3", "o1", "o2"], Ids(Query(("_sort", "-note")).Apply(Orders)));
    }

    [Fact]
    public void An_empty_sort_value_sorts_nothing_rather_than_by_a_field_called_nothing()
    {
        Assert.Equal(["o1", "o2", "o3"], Ids(Query(("_sort", "")).Apply(Orders)));
        Assert.Equal(["o1", "o2", "o3"], Ids(Query(("_sort", "-")).Apply(Orders)));
    }

    // ---- field selection ------------------------------------------------------------------------

    [Fact]
    public void Field_selection_returns_the_summary_shape_a_real_api_would()
    {
        Assert.Equal("""{"status":"settled","total":100}""", Query(("_fields", "status,total")).Project(Orders[0].Body));
    }

    [Fact]
    public void A_selected_field_the_document_lacks_is_absent_rather_than_null()
    {
        // Present-and-null is a claim the document does not make. Absent is the truth.
        Assert.Equal("""{"status":"pending"}""", Query(("_fields", "status,note")).Project(Orders[1].Body));
    }

    [Fact]
    public void Selecting_nothing_leaves_the_body_byte_for_byte()
    {
        // The compatibility promise: a caller who sends no query gets exactly what they got before.
        Assert.Equal(Orders[0].Body, ResourceQuery.All.Project(Orders[0].Body));
        Assert.Equal(Orders[0].Body, Query(("_fields", "")).Project(Orders[0].Body));
    }

    [Fact]
    public void A_body_that_is_not_an_object_is_returned_untouched()
    {
        Assert.Equal("[1,2,3]", Query(("_fields", "a")).Project("[1,2,3]"));
        Assert.Equal("not json", Query(("_fields", "a")).Project("not json"));
    }

    // ---- the control parameters -----------------------------------------------------------------

    [Fact]
    public void Paging_parameters_are_not_read_as_field_filters()
    {
        // limit/offset were already the listing's own, so they cannot also mean "a field called limit".
        Assert.True(Query(("limit", "10"), ("offset", "20")).IsEmpty);
    }

    [Fact]
    public void A_control_parameter_never_doubles_as_a_field_filter()
    {
        // If _fields also filtered, it would look for documents with a field called "_fields" and find
        // none — so asking for a summary shape would silently return nothing.
        var selection = Query(("_fields", "status"));

        Assert.Empty(selection.Filters);
        Assert.Equal(["o1", "o2", "o3"], Ids(selection.Apply(Orders)));
    }

    [Fact]
    public void A_field_selection_with_no_value_selects_nothing_rather_than_a_field_called_nothing()
    {
        Assert.Equal(Orders[0].Body, Query(("_fields", null)).Project(Orders[0].Body));
    }

    [Fact]
    public void A_key_that_is_only_an_operator_filters_a_field_by_that_literal_name()
    {
        // ":contains" has its colon at position 0, so there is no field in front of it. Splitting there
        // would leave an empty field name, which matches nothing and looks like a broken filter; it is
        // read as a field literally called ":contains" instead.
        var documents = (IReadOnlyList<ResourceDocument>)[Doc("a", """{":contains":"x"}""")];

        Assert.Equal(["a"], Ids(Query((":contains", "x")).Apply(documents)));
    }

    [Fact]
    public void The_control_parameters_are_the_ones_the_docs_name()
    {
        // This list is what the documentation promises cannot be filtered on. Drift between the two is
        // a field somebody cannot query and no error explaining why.
        Assert.Equal(["limit", "offset", "_sort", "_fields"], ResourceQuery.ControlParameters);
    }

    [Fact]
    public void An_empty_query_is_recognisably_empty()
    {
        // IsEmpty is what the callers use to skip the work entirely and keep today's behaviour exact.
        Assert.True(ResourceQuery.All.IsEmpty);
        Assert.True(Query().IsEmpty);
        Assert.False(Query(("status", "settled")).IsEmpty);
        Assert.False(Query(("_sort", "total")).IsEmpty);
        Assert.False(Query(("_fields", "id")).IsEmpty);
    }

    [Fact]
    public void A_parameter_with_no_name_is_ignored()
    {
        Assert.True(Query((" ", "x")).IsEmpty);
    }

    [Fact]
    public void A_filter_with_no_value_asks_for_the_empty_string_rather_than_for_anything()
    {
        // ?status= is a caller asking for documents whose status is empty. Reading it as "no filter"
        // would silently return everything to somebody who asked for something.
        var documents = (IReadOnlyList<ResourceDocument>)[Doc("a", """{"status":""}"""), Doc("b", """{"status":"x"}""")];

        Assert.Equal(["a"], Ids(Query(("status", null)).Apply(documents)));
    }
}
