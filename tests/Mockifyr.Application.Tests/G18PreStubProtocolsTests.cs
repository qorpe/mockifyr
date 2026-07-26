using System.Text.Json.Nodes;
using Mockifyr.Facade.Admin;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Unit coverage for the protocol classification (G18-pre, ADR 0010) — the pure decision table the
/// admin list applies. The wire behavior (field present in the list, absent from storage, descriptor
/// hot-reload) is covered by <c>G18PreProtocolUxTests</c> in the differential suite.
/// </summary>
public sealed class G18PreStubProtocolsTests
{
    private sealed class FixedProbe(params string[] grpcPaths) : IStubProtocolProbe
    {
        public bool IsGrpcPath(string path) => grpcPaths.Contains(path);
    }

    private static JsonObject Mapping(string requestJson) =>
        (JsonObject)JsonNode.Parse("""{"response":{"status":200},"request":""" + requestJson + "}")!;

    [Fact]
    public void Graphql_WinsByCustomMatcherName_CaseInsensitive()
    {
        var mapping = Mapping("""{"method":"POST","urlPath":"/graphql","customMatcher":{"name":"GraphQL-Body-Matcher"}}""");
        Assert.Equal("graphql", StubProtocols.Classify(mapping, probe: null));
    }

    [Fact]
    public void OtherCustomMatchers_AreNotGraphql()
    {
        var mapping = Mapping("""{"method":"POST","urlPath":"/x","customMatcher":{"name":"my-extension-matcher"}}""");
        Assert.Equal("http", StubProtocols.Classify(mapping, probe: null));
    }

    [Fact]
    public void Grpc_WhenTheProbeResolvesTheUrlPath()
    {
        var mapping = Mapping("""{"method":"POST","urlPath":"/pkg.Svc/Call"}""");
        Assert.Equal("grpc", StubProtocols.Classify(mapping, new FixedProbe("/pkg.Svc/Call")));
    }

    [Fact]
    public void Grpc_AlsoResolvesPlainUrl_WithQueryStringStripped()
    {
        var mapping = Mapping("""{"method":"POST","url":"/pkg.Svc/Call?x=1"}""");
        Assert.Equal("grpc", StubProtocols.Classify(mapping, new FixedProbe("/pkg.Svc/Call")));
    }

    [Fact]
    public void GraphqlBeatsGrpc_WhenBothWouldApply()
    {
        // A stub can't be both; the explicit matcher is the stronger signal than a path lookup.
        var mapping = Mapping(
            """{"method":"POST","urlPath":"/pkg.Svc/Call","customMatcher":{"name":"graphql-body-matcher"}}""");
        Assert.Equal("graphql", StubProtocols.Classify(mapping, new FixedProbe("/pkg.Svc/Call")));
    }

    [Fact]
    public void NoProbe_MeansNoGrpc()
    {
        var mapping = Mapping("""{"method":"POST","urlPath":"/pkg.Svc/Call"}""");
        Assert.Equal("http", StubProtocols.Classify(mapping, probe: null));
    }

    [Fact]
    public void UrlPatternForms_AreNeverProbed()
    {
        // The gRPC dialect writes plain paths; pattern forms stay http even if the regex text matches.
        var mapping = Mapping("""{"method":"POST","urlPathPattern":"/pkg.Svc/.*"}""");
        Assert.Equal("http", StubProtocols.Classify(mapping, new FixedProbe("/pkg.Svc/Call")));
    }

    [Fact]
    public void MissingRequest_IsHttp()
    {
        Assert.Equal("http", StubProtocols.Classify((JsonObject)JsonNode.Parse("""{"response":{"status":200}}""")!, null));
    }
}
