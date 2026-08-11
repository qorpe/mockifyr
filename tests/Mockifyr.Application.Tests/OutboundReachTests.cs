using Mockifyr.Adapters.MappingJson;

namespace Mockifyr.Application.Tests;

/// <summary>
/// The detector behind the partner principal (#346): which fields of a stub definition make the host
/// act on the network. Pure and dialect-shaped, so unit-tested here and enforced at the host edge.
/// </summary>
public sealed class OutboundReachTests
{
    [Fact]
    public void A_proxy_stub_reaches_outward()
    {
        Assert.Equal(
            ["proxyBaseUrl"],
            OutboundReach.DeclaredBy("""{"request":{"url":"/a"},"response":{"proxyBaseUrl":"http://elsewhere"}}"""));
    }

    [Theory]
    [InlineData("postServeActions")]
    [InlineData("serveEventListeners")]
    public void Both_spellings_of_a_post_serve_action_reach_outward(string key)
    {
        // The adapter accepts either, so a check that knew only one would be a check with a documented
        // way around it.
        var mapping = $$"""{"request":{"url":"/a"},"response":{"status":200},"{{key}}":[{"name":"webhook"}]}""";

        Assert.Equal([key], OutboundReach.DeclaredBy(mapping));
    }

    [Fact]
    public void An_ordinary_stub_reaches_nowhere()
    {
        Assert.Empty(OutboundReach.DeclaredBy(
            """{"request":{"method":"GET","url":"/a"},"response":{"status":200,"body":"hi"}}"""));
    }

    [Fact]
    public void A_bundle_is_read_the_same_way_as_a_single_stub()
    {
        var bundle = """
        {"mappings":[
          {"request":{"url":"/a"},"response":{"status":200}},
          {"request":{"url":"/b"},"response":{"proxyBaseUrl":"http://elsewhere"}}
        ]}
        """;

        Assert.Equal(["proxyBaseUrl"], OutboundReach.DeclaredBy(bundle));
    }

    [Fact]
    public void Every_declared_field_is_named_once_and_in_a_stable_order()
    {
        // The refusal quotes this list back to the caller, so it has to read the same way twice.
        var bundle = """
        {"mappings":[
          {"request":{"url":"/a"},"response":{"proxyBaseUrl":"http://one"}},
          {"request":{"url":"/b"},"response":{"proxyBaseUrl":"http://two"}},
          {"request":{"url":"/c"},"response":{"status":200},"postServeActions":[{"name":"webhook"}]}
        ]}
        """;

        Assert.Equal(["postServeActions", "proxyBaseUrl"], OutboundReach.DeclaredBy(bundle));
    }

    [Theory]
    [InlineData("""{"request":{"url":"/a"},"response":{"status":200},"postServeActions":[]}""")]      // declared, empty
    [InlineData("""{"request":{"url":"/a"},"response":{"status":200},"postServeActions":"nope"}""")]  // wrong type
    [InlineData("""{"request":{"url":"/a"},"response":{"proxyBaseUrl":""}}""")]                       // present, blank
    [InlineData("""{"request":{"url":"/a"},"response":{"proxyBaseUrl":"   "}}""")]                    // whitespace only
    [InlineData("""{"request":{"url":"/a"},"response":{"proxyBaseUrl":123}}""")]                      // wrong type
    public void A_field_that_names_no_target_is_not_outward_reach(string mapping)
    {
        // Refusing these would refuse stubs that cannot call anything, and a control that fires on
        // harmless input is one an operator learns to work around.
        Assert.Empty(OutboundReach.DeclaredBy(mapping));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    public void A_payload_that_is_not_a_mapping_declares_nothing(string payload)
    {
        // It is refused moments later by the reader that owns the dialect, with a better message than
        // this could produce. Answering "denied" here would tell a caller their permissions are wrong
        // when their syntax is.
        Assert.Empty(OutboundReach.DeclaredBy(payload));
    }

    [Fact]
    public void A_bundle_whose_entries_are_not_objects_is_survived()
    {
        Assert.Empty(OutboundReach.DeclaredBy("""{"mappings":[1,"two",null]}"""));
    }

    [Fact]
    public void A_mappings_property_that_is_not_an_array_is_read_as_a_single_mapping()
    {
        // A stub could legitimately be named "mappings" nowhere, but the guard matters: treating a
        // non-array as a bundle would skip the root object and miss what it declares.
        Assert.Equal(
            ["proxyBaseUrl"],
            OutboundReach.DeclaredBy("""{"mappings":"oops","response":{"proxyBaseUrl":"http://elsewhere"}}"""));
    }
}
