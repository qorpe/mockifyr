using Mockifyr.Adapters.MappingJson;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Coverage for the import warnings (1.0). Two things matter equally: a deferred field must produce a
/// warning, and a stub that uses none of them must produce none — a warning that cries wolf on ordinary
/// mappings gets filtered out by the first CI pipeline that sees it, and then the real one is missed too.
/// </summary>
public sealed class UnsupportedFieldWarningTests
{
    [Fact]
    public void A_bodyFileName_response_is_reported()
    {
        var warnings = UnsupportedFieldWarnings.For(
            """{"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"bodyFileName":"body.json"}}""");

        var warning = Assert.Single(warnings);
        Assert.Contains("bodyFileName", warning);
        // The message has to say what will actually happen, or it is just a label. An empty body reads
        // as a matching problem, which is the wrong thing to go debugging.
        Assert.Contains("EMPTY body", warning);
        // One stub gets no count: "(1 stubs)" is noise, and ungrammatical noise at that.
        Assert.DoesNotContain("stubs)", warning);
    }

    [Fact]
    public void A_non_uniform_delay_distribution_is_reported()
    {
        var warnings = UnsupportedFieldWarnings.For(
            """
            {"request":{"method":"GET","urlPath":"/a"},
             "response":{"status":200,"delayDistribution":{"type":"lognormal","median":90,"sigma":0.1}}}
            """);

        var warning = Assert.Single(warnings);
        Assert.Contains("lognormal", warning);
        Assert.Contains("NO delay", warning);
    }

    [Fact]
    public void A_uniform_delay_distribution_is_not_reported() =>
        // Uniform is implemented. Warning about it would train people to ignore warnings.
        Assert.Empty(UnsupportedFieldWarnings.For(
            """
            {"request":{"method":"GET","urlPath":"/a"},
             "response":{"status":200,"delayDistribution":{"type":"uniform","lower":10,"upper":20}}}
            """));

    [Theory]
    [InlineData("""{"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"body":"ok"}}""")]
    [InlineData("""{"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"jsonBody":{"a":1}}}""")]
    [InlineData("""{"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"fixedDelayMilliseconds":50}}""")]
    [InlineData("""{"request":{"method":"GET","urlPath":"/a"},"response":{"status":404}}""")]
    public void An_ordinary_stub_produces_no_warnings(string mapping) =>
        Assert.Empty(UnsupportedFieldWarnings.For(mapping));

    [Fact]
    public void A_bundle_is_inspected_stub_by_stub()
    {
        var warnings = UnsupportedFieldWarnings.For(
            """
            {"mappings":[
              {"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"body":"fine"}},
              {"request":{"method":"GET","urlPath":"/b"},"response":{"status":200,"bodyFileName":"b.json"}}
            ]}
            """);

        Assert.Contains("bodyFileName", Assert.Single(warnings));
    }

    [Fact]
    public void A_bare_array_of_mappings_is_inspected_too() =>
        Assert.Single(UnsupportedFieldWarnings.For(
            """[{"request":{"method":"GET","urlPath":"/b"},"response":{"status":200,"bodyFileName":"b.json"}}]"""));

    [Fact]
    public void The_same_gap_across_a_bundle_is_reported_once_with_a_count()
    {
        // Deliberately a DIFFERENT file name per stub: grouping by the whole message would let these
        // through as fifty near-identical lines, which is the wall this is meant to prevent. The gap
        // is one fact whatever the file is called, and the fix is the same for all of them.
        var stubs = string.Join(",", Enumerable.Range(0, 50).Select(i =>
            $$$"""{"request":{"method":"GET","urlPath":"/s{{{i}}}"},"response":{"status":200,"bodyFileName":"file-{{{i}}}.json"}}"""));

        var warning = Assert.Single(UnsupportedFieldWarnings.For($$$"""{"mappings":[{{{stubs}}}]}"""));
        Assert.Contains("50 stubs", warning);
    }

    [Fact]
    public void Two_different_gaps_are_both_reported()
    {
        var warnings = UnsupportedFieldWarnings.For(
            """
            {"mappings":[
              {"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"bodyFileName":"a.json"}},
              {"request":{"method":"GET","urlPath":"/b"},"response":{"status":200,"delayDistribution":{"type":"lognormal"}}}
            ]}
            """);

        Assert.Equal(2, warnings.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("""{"request":{"method":"GET"}}""")]
    [InlineData("""{"response":"not an object"}""")]
    [InlineData("""{"response":{"status":200,"bodyFileName":42}}""")]
    public void Anything_malformed_produces_no_warnings_rather_than_throwing(string mapping) =>
        // The warning pass runs on input a client controls, and it must never be the thing that fails
        // an import. Malformed JSON is reported by the importer itself, with a better message.
        Assert.Empty(UnsupportedFieldWarnings.For(mapping));
}
