using Mockifyr.Core;

namespace Mockifyr.Core.Tests;

/// <summary>
/// Pure-logic coverage for drift against reality (#287): does what a stub would answer still look like
/// what the upstream actually answered? Self-tested — no oracle has this concept.
/// </summary>
public class ResponseDriftTests
{
    private static IReadOnlyList<ResponseDrift> Compare(
        string? stubBody, string? upstreamBody, int stubStatus = 200, int upstreamStatus = 200) =>
        ResponseDriftCheck.Compare("GET", "/orders", stubStatus, stubBody, upstreamStatus, upstreamBody);

    [Fact]
    public void An_identical_shape_agrees()
    {
        Assert.Empty(Compare("""{"id":"a","total":1}""", """{"id":"b","total":99}"""));
    }

    [Fact]
    public void Values_are_never_compared()
    {
        // An id, a timestamp, a total: these differ between environments and between minutes.
        // Reporting them would bury the findings that matter under noise nobody can act on.
        Assert.Empty(Compare(
            """{"id":"stub-1","createdAt":"2020-01-01T00:00:00Z","total":0}""",
            """{"id":"real-9","createdAt":"2026-08-05T11:00:00Z","total":4213}"""));
    }

    [Fact]
    public void A_field_the_upstream_added_is_reported()
    {
        // The drift that matters most: the upstream grew a field, the stub never followed, and a client
        // reading it works against production and not against the mock.
        var findings = Compare("""{"id":"a"}""", """{"id":"a","currency":"EUR"}""");

        var finding = Assert.Single(findings);
        Assert.Equal(ResponseDriftKind.FieldMissing, finding.Kind);
        Assert.Equal("/currency", finding.Pointer);
        Assert.Contains("the upstream returns this field and the stub does not", finding.Detail);
    }

    [Fact]
    public void A_field_only_the_stub_has_is_reported()
    {
        // The upstream dropped it, or it never existed and somebody invented it while writing the stub.
        // Either way a client may now depend on something reality does not send.
        var finding = Assert.Single(Compare("""{"id":"a","legacyFlag":true}""", """{"id":"a"}"""));

        Assert.Equal(ResponseDriftKind.FieldUnexpected, finding.Kind);
        Assert.Equal("/legacyFlag", finding.Pointer);
    }

    [Fact]
    public void A_changed_type_is_reported_with_both_sides_named()
    {
        var finding = Assert.Single(Compare("""{"total":"12.00"}""", """{"total":12.0}"""));

        Assert.Equal(ResponseDriftKind.TypeDiffers, finding.Kind);
        Assert.Contains("the stub returns a string", finding.Detail);
        Assert.Contains("the upstream returns a number", finding.Detail);
    }

    [Fact]
    public void Nesting_is_followed_and_the_pointer_says_where()
    {
        var finding = Assert.Single(Compare(
            """{"order":{"customer":{"id":"a"}}}""",
            """{"order":{"customer":{"id":"a","vip":true}}}"""));

        Assert.Equal("/order/customer/vip", finding.Pointer);
    }

    [Fact]
    public void An_array_is_compared_by_its_first_element()
    {
        // Comparing every element would report the same difference once per row, which turns one
        // finding into a hundred and a report into a wall.
        var finding = Assert.Single(Compare(
            """[{"id":"a"},{"id":"b"},{"id":"c"}]""",
            """[{"id":"a","currency":"EUR"},{"id":"b","currency":"EUR"}]"""));

        Assert.Equal("/0/currency", finding.Pointer);
    }

    [Fact]
    public void An_empty_array_says_nothing_about_shape()
    {
        Assert.Empty(Compare("""{"items":[]}""", """{"items":[{"id":"a"}]}"""));
        Assert.Empty(Compare("""{"items":[{"id":"a"}]}""", """{"items":[]}"""));
    }

    [Fact]
    public void A_differing_status_is_reported()
    {
        var finding = Assert.Single(Compare("""{"id":"a"}""", """{"id":"a"}""", stubStatus: 200, upstreamStatus: 201));

        Assert.Equal(ResponseDriftKind.StatusDiffers, finding.Kind);
        Assert.Contains("The stub answers 200", finding.Detail);
        Assert.Contains("the upstream answered 201", finding.Detail);
        Assert.Null(finding.Pointer);
    }

    [Fact]
    public void A_status_difference_does_not_stop_the_body_comparison()
    {
        // A stub that is wrong about both should say so about both; reporting only the first would send
        // somebody back for a second run to find the rest.
        var findings = Compare("""{"id":"a"}""", """{"id":"a","currency":"EUR"}""", 200, 500);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Kind == ResponseDriftKind.StatusDiffers);
        Assert.Contains(findings, f => f.Kind == ResponseDriftKind.FieldMissing);
    }

    [Fact]
    public void No_matching_stub_is_the_first_thing_reported()
    {
        var finding = Assert.Single(ResponseDriftCheck.Compare(
            "POST", "/orders", stubStatus: null, stubBody: null, upstreamStatus: 201, upstreamBody: """{"id":"a"}"""));

        Assert.Equal(ResponseDriftKind.NoStub, finding.Kind);
        Assert.Contains("no stub matches this request", finding.Detail);
    }

    [Fact]
    public void A_templated_body_is_left_alone()
    {
        // Not JSON until a request renders it. Reporting drift on every templated stub is the fastest
        // way to make the report something people stop reading.
        Assert.Empty(Compare("""{"id":"{{request.pathSegments.[1]}}"}""", """{"id":"a","extra":1}"""));
    }

    [Fact]
    public void A_body_neither_side_sends_as_json_is_left_alone()
    {
        Assert.Empty(Compare("plain text", "also plain"));
        Assert.Empty(Compare(null, """{"id":"a"}"""));
        Assert.Empty(Compare("""{"id":"a"}""", null));
    }

    [Fact]
    public void Null_and_a_value_are_different_shapes()
    {
        // A field that arrives null in one environment and populated in another is worth seeing; "true
        // versus false" is a value and is not.
        Assert.Single(Compare("""{"cancelledAt":null}""", """{"cancelledAt":"2026-01-01"}"""));
        Assert.Empty(Compare("""{"active":true}""", """{"active":false}"""));
    }

    [Fact]
    public void The_body_findings_are_capped()
    {
        var stub = """{"id":"a"}""";
        var upstream = """{"id":"a","a":1,"b":2,"c":3,"d":4,"e":5,"f":6,"g":7,"h":8}""";

        // A handful of pointers is a report somebody reads; forty is a wall somebody closes.
        Assert.Equal(ResponseDriftCheck.MaxBodyFindings, Compare(stub, upstream).Count);
    }

    [Theory]
    [InlineData("""{"v":"x"}""", """{"v":1}""", "a string", "a number")]
    [InlineData("""{"v":[1]}""", """{"v":{"a":1}}""", "an array", "an object")]
    [InlineData("""{"v":true}""", """{"v":"x"}""", "a boolean", "a string")]
    [InlineData("""{"v":null}""", """{"v":[1]}""", "null", "an array")]
    [InlineData("""{"v":{"a":1}}""", """{"v":null}""", "an object", "null")]
    public void Each_json_type_is_named_the_way_a_reader_would_name_it(
        string stub, string upstream, string stubKind, string upstreamKind)
    {
        // The sentence is the whole finding for a type change; naming both sides in words a reader
        // recognises is what makes it actionable without opening a schema.
        var finding = Assert.Single(Compare(stub, upstream));

        Assert.Contains($"the stub returns {stubKind}", finding.Detail);
        Assert.Contains($"the upstream returns {upstreamKind}", finding.Detail);
    }

    [Fact]
    public void A_nested_type_change_points_at_the_field_not_at_the_body()
    {
        var finding = Assert.Single(Compare("""{"order":{"total":"12"}}""", """{"order":{"total":12}}"""));

        Assert.Equal("/order/total", finding.Pointer);
        Assert.StartsWith("/order/total:", finding.Detail);
    }

    [Fact]
    public void An_unexpected_field_says_which_side_has_it()
    {
        var finding = Assert.Single(Compare("""{"id":"a","legacy":true}""", """{"id":"a"}"""));

        // "Extra field" alone leaves the reader working out which side is extra; the sentence says it.
        Assert.Contains("the stub returns this field and the upstream does not", finding.Detail);
        Assert.StartsWith("/legacy:", finding.Detail);
    }

    [Fact]
    public void A_stub_body_that_is_not_json_is_left_alone()
    {
        // Only the upstream parses. Comparing a shape against something with no shape would be
        // guesswork, and a guess in a drift report is a false finding.
        Assert.Empty(Compare("not json at all", """{"id":"a"}"""));
    }

    [Fact]
    public void An_upstream_body_that_is_not_json_is_left_alone()
    {
        Assert.Empty(Compare("""{"id":"a"}""", "<html>an error page</html>"));
    }

    [Fact]
    public void A_whole_body_type_change_is_reported_against_the_body_itself()
    {
        var finding = Assert.Single(Compare("""{"id":"a"}""", """[{"id":"a"}]"""));

        Assert.Equal(ResponseDriftKind.TypeDiffers, finding.Kind);
        Assert.Equal("the body", finding.Pointer);
    }
}
