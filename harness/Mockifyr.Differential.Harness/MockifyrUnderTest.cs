using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;
using Mockifyr.Differential.Generator;
using Mockifyr.Facade.Library;
using Mockifyr.Outbound;

namespace Mockifyr.Differential.Harness;

/// <summary>
/// Drives Mockifyr in-process (via the library facade) so its output can be diffed against the
/// oracle. The engine is transport-agnostic, so an in-process drive is a faithful test of its
/// response semantics; wire-level facade behaviors are validated separately from G12.
/// </summary>
public sealed class MockifyrUnderTest
{
    private readonly MockifyrServer _server;
    private readonly ProxyResponder _proxy = new();

    /// <summary>
    /// Composes the Mockifyr side. <paramref name="bodyFiles"/> gives it the same <c>__files</c>
    /// content the oracle container was handed, so `bodyFileName` responses can be diffed.
    /// </summary>
    public MockifyrUnderTest(IResponseBodyFiles? bodyFiles = null) => _server = new MockifyrServer(bodyFiles: bodyFiles);

    /// <summary>Imports the same WireMock JSON the oracle receives.</summary>
    public void ImportMappingJson(string wireMockJson) => _server.ImportMappingJson(wireMockJson);

    /// <summary>
    /// Handles a request and snapshots the response. <paramref name="scheme"/> mirrors the transport
    /// the oracle was driven over (for WireMock's <c>request.scheme</c> matching, G15c); host/port
    /// derive from the request's <c>Host</c> header inside the builder.
    /// </summary>
    public HttpResponseSnapshot Send(RequestSpec spec, string? scheme = null)
    {
        var request = CanonicalRequestBuilder.Build(spec.Method, spec.Url, spec.Headers, spec.Body, scheme);
        var resolution = _server.Handle(request);
        return resolution.Response is { } response ? Snapshot(response) : NotFound();
    }

    /// <summary>
    /// Like <see cref="Send"/> but applies a proxy directive (G8): if the matched response is a
    /// proxy, the request is forwarded to the upstream and the upstream's response is snapshotted.
    /// </summary>
    public async Task<HttpResponseSnapshot> SendWithProxyAsync(RequestSpec spec)
    {
        var request = CanonicalRequestBuilder.Build(spec.Method, spec.Url, spec.Headers, spec.Body);
        var resolution = _server.Handle(request);

        if (resolution.Response is not { } response)
        {
            return NotFound();
        }

        return response.Proxy is { } proxy
            ? Snapshot(await _proxy.ProxyAsync(proxy, request))
            : Snapshot(response);
    }

    private static HttpResponseSnapshot Snapshot(CanonicalResponse response) => new()
    {
        Status = response.Status,
        Headers = response.Headers.ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase),
        Body = response.Body,
    };

    // No stub matched. WireMock serves a 404 here; the full no-match body is compared from the group
    // that implements it. For now report a bare 404.
    private static HttpResponseSnapshot NotFound() => new()
    {
        Status = 404,
        Headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
        Body = [],
    };

    /// <summary>Counts journaled requests matching a WireMock request-pattern JSON (verification — G6).</summary>
    public int CountRequestsMatching(string patternJson) =>
        _server.CountRequestsMatching(MappingJsonReader.ReadRequestPattern(patternJson));

    /// <summary>The number of journaled requests that matched no stub (verification — G6).</summary>
    public int UnmatchedCount() => _server.FindUnmatchedRequests().Count;
}
