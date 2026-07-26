using System.Text.Json.Nodes;

namespace Mockifyr.Facade.Admin;

/// <summary>
/// Answers "does this URL path serve gRPC?" for protocol classification (G18-pre, ADR 0010). The
/// admin facade must not reference the gRPC facade (facades never depend on each other), so the
/// composition root implements this over the loaded descriptor index and registers it; a host without
/// gRPC registers nothing and every stub classifies as HTTP/GraphQL.
/// </summary>
public interface IStubProtocolProbe
{
    /// <summary>Whether the path resolves to a loaded gRPC service/method.</summary>
    bool IsGrpcPath(string path);
}

/// <summary>
/// Computes the read-only <c>protocol</c> field the admin mappings list exposes (G18-pre, ADR 0010):
/// <c>graphql</c> when the stub carries the <c>graphql-body-matcher</c> custom matcher (G14),
/// <c>grpc</c> when its URL path resolves against a loaded descriptor (G13), else <c>http</c>.
/// Purely computed at query time — the stored mapping JSON is never touched.
/// </summary>
public static class StubProtocols
{
    /// <summary>The custom matcher name the GraphQL extension dialect uses (learned in G14a).</summary>
    public const string GraphqlMatcherName = "graphql-body-matcher";

    /// <summary>Classifies a mapping's source document. <paramref name="probe"/> may be null (no gRPC host).</summary>
    public static string Classify(JsonObject mapping, IStubProtocolProbe? probe)
    {
        if (mapping["request"] is not JsonObject request)
        {
            return "http";
        }

        if (request["customMatcher"] is JsonObject custom &&
            string.Equals((string?)custom["name"], GraphqlMatcherName, StringComparison.OrdinalIgnoreCase))
        {
            return "graphql";
        }

        return probe is not null && UrlOf(request) is { } url && probe.IsGrpcPath(PathOf(url))
            ? "grpc"
            : "http";
    }

    // A gRPC stub addresses the method by plain path (urlPath / url — G13 writes urlPath); pattern
    // forms are never emitted by the gRPC dialect, so they are not probed.
    private static string? UrlOf(JsonObject request) =>
        (string?)request["urlPath"] ?? (string?)request["url"];

    // `url` may carry a query string; the probe wants the bare path.
    private static string PathOf(string url)
    {
        var query = url.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? url : url[..query];
    }
}
