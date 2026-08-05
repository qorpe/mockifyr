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
    [InlineData("""{"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"bodyFileName":"b.json"}}""")]
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
              {"request":{"method":"GET","urlPath":"/b"},"response":{"status":200,"delayDistribution":{"type":"lognormal"}}}
            ]}
            """);

        Assert.Contains("lognormal", Assert.Single(warnings));
    }

    [Fact]
    public void A_bare_array_of_mappings_is_inspected_too() =>
        Assert.Single(UnsupportedFieldWarnings.For(
            """[{"request":{"method":"GET","urlPath":"/b"},"response":{"status":200,"delayDistribution":{"type":"lognormal"}}}]"""));

    [Fact]
    public void The_same_gap_across_a_bundle_is_reported_once_with_a_count()
    {
        // Built with escaped quotes rather than a raw literal: the JSON ends in a run of closing
        // braces that an interpolated raw string reads as its own.
        var stubs = string.Join(",", Enumerable.Range(0, 50).Select(i =>
            "{\"request\":{\"method\":\"GET\",\"urlPath\":\"/s" + i +
            "\"},\"response\":{\"status\":200,\"delayDistribution\":{\"type\":\"lognormal\"}}}"));

        // One gap is one fact, however many stubs share it.
        var warning = Assert.Single(UnsupportedFieldWarnings.For("{\"mappings\":[" + stubs + "]}"));
        Assert.Contains("50 stubs", warning);
    }

    [Fact]
    public void Two_different_distribution_types_are_both_reported()
    {
        var warnings = UnsupportedFieldWarnings.For(
            """
            {"mappings":[
              {"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"delayDistribution":{"type":"lognormal"}}},
              {"request":{"method":"GET","urlPath":"/b"},"response":{"status":200,"delayDistribution":{"type":"chunkedDribble"}}}
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
    [InlineData("""{"response":{"status":200,"delayDistribution":42}}""")]
    public void Anything_malformed_produces_no_warnings_rather_than_throwing(string mapping) =>
        // The warning pass runs on input a client controls, and it must never be the thing that fails
        // an import. Malformed JSON is reported by the importer itself, with a better message.
        Assert.Empty(UnsupportedFieldWarnings.For(mapping));

    // A `publish` action on a host with no broker (ADR 0013). Unlike every other gap here the mapping
    // is not at fault, which is exactly why it is worth saying: the stub serves its response perfectly
    // and emits nothing, and that reads as a broker outage rather than as a missing flag.
    private const string PublishingStub =
        """
        {"request":{"method":"POST","urlPath":"/payments"},"response":{"status":201},
         "postServeActions":[{"name":"publish","parameters":{"topic":"payments.events"}}]}
        """;

    [Fact]
    public void A_publish_action_on_a_host_with_no_broker_is_reported()
    {
        var warning = Assert.Single(UnsupportedFieldWarnings.For(PublishingStub, brokerConfigured: false));

        // The message has to name the fix, not just the fact — the operator's next question is "so what
        // do I do", and the answer is one flag.
        Assert.Contains("publish", warning, StringComparison.Ordinal);
        Assert.Contains("--kafka-bootstrap", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_publish_action_on_a_host_with_a_broker_is_not_a_gap() =>
        Assert.Empty(UnsupportedFieldWarnings.For(PublishingStub, brokerConfigured: true));

    [Fact]
    public void A_host_with_no_broker_says_nothing_about_a_stub_that_does_not_publish() =>
        // The warning must be about the action, not about the flag. A host that mocks no events would
        // otherwise be told off for every stub it loads.
        Assert.Empty(UnsupportedFieldWarnings.For(
            """{"request":{"method":"GET","urlPath":"/a"},"response":{"status":200}}""",
            brokerConfigured: false));

    [Fact]
    public void A_webhook_is_not_mistaken_for_a_publish() =>
        // Both are post-serve actions and only one needs a broker.
        Assert.Empty(UnsupportedFieldWarnings.For(
            """
            {"request":{"method":"GET","urlPath":"/a"},"response":{"status":200},
             "postServeActions":[{"name":"webhook","parameters":{"url":"http://x/y"}}]}
            """,
            brokerConfigured: false));

    [Fact]
    public void The_action_name_is_matched_without_regard_to_case() =>
        Assert.Single(UnsupportedFieldWarnings.For(
            """
            {"request":{"method":"GET","urlPath":"/a"},"response":{"status":200},
             "postServeActions":[{"name":"Publish","parameters":{"topic":"t"}}]}
            """,
            brokerConfigured: false));

    [Fact]
    public void A_bundle_of_publishing_stubs_reports_the_gap_once_with_a_count()
    {
        var warning = Assert.Single(UnsupportedFieldWarnings.For(
            $$"""{"mappings":[{{PublishingStub}},{{PublishingStub}},{{PublishingStub}}]}""",
            brokerConfigured: false));

        // One line per kind, as with every other gap: three stubs with the same missing flag is one
        // fact, and burying it under two duplicates is how a warning list stops being read.
        Assert.Contains("(3 stubs)", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void One_stub_is_counted_by_saying_nothing()
    {
        // "(1 stubs)" is noise and ungrammatical, and the count only earns its place when it tells the
        // reader something they could not see. Mutation testing caught this going unasserted.
        Assert.DoesNotContain(
            "stubs)",
            Assert.Single(UnsupportedFieldWarnings.For(PublishingStub, brokerConfigured: false)),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "stubs)",
            Assert.Single(UnsupportedFieldWarnings.For(
                """{"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"delayDistribution":{"type":"lognormal"}}}""")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_stub_that_publishes_and_has_a_deferred_field_reports_both()
    {
        var warnings = UnsupportedFieldWarnings.For(
            """
            {"request":{"method":"GET","urlPath":"/a"},
             "response":{"status":200,"delayDistribution":{"type":"lognormal"}},
             "postServeActions":[{"name":"publish","parameters":{"topic":"t"}}]}
            """,
            brokerConfigured: false);

        // The publish check runs before the response is even looked at, so this pins that an early
        // return cannot swallow the other warning — or the reverse.
        Assert.Equal(2, warnings.Count);
    }

    [Theory]
    [InlineData("""{"postServeActions":"not an array"}""")]
    [InlineData("""{"postServeActions":[42]}""")]
    [InlineData("""{"postServeActions":[{"parameters":{}}]}""")]
    [InlineData("""{"postServeActions":[{"name":42}]}""")]
    public void A_malformed_post_serve_action_produces_no_warning_rather_than_throwing(string mapping) =>
        Assert.Empty(UnsupportedFieldWarnings.For(mapping, brokerConfigured: false));

    [Fact]
    public void Nothing_is_assumed_about_a_broker_when_the_caller_does_not_say() =>
        // The default is "it works", because every caller that knows better passes the answer. A default
        // that warned would make an in-process library user hear about a flag they cannot pass.
        Assert.Empty(UnsupportedFieldWarnings.For(PublishingStub));
}
