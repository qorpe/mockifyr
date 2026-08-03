using System.Net.Http;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Mockifyr.Differential.Generator;

namespace Mockifyr.Differential.Harness;

/// <summary>
/// The test oracle: a real Java WireMock running in a container. Its responses are the ground
/// truth Mockifyr is diffed against. See docs/decisions/0002.
/// </summary>
public sealed class WireMockOracle : IAsyncDisposable
{
    /// <summary>The pinned oracle image.</summary>
    public const string Image = "wiremock/wiremock:3.10.0";

    private const ushort WireMockPort = 8080;
    private const ushort WireMockHttpsPort = 8443;

    private readonly IContainer _container;

    /// <summary>
    /// Builds the oracle. <paramref name="extraCommandArgs"/> append to the standalone command line
    /// (e.g. <c>--global-response-templating</c>), so host-flag behaviors can be diffed too.
    /// </summary>
    public WireMockOracle(params string[] extraCommandArgs)
        : this(bodyFiles: null, extraCommandArgs)
    {
    }

    /// <summary>
    /// Builds the oracle with files placed in its <c>__files</c> directory, so <c>bodyFileName</c>
    /// responses can be diffed: both engines are handed the same stub AND the same file, and the
    /// answers are compared like any other behaviour.
    /// </summary>
    public WireMockOracle(IReadOnlyDictionary<string, string>? bodyFiles, params string[] extraCommandArgs) =>
        _container = bodyFiles is null or { Count: 0 }
            ? BuildContainer(extraCommandArgs)
            : bodyFiles.Aggregate(
                BuilderFor(extraCommandArgs),
                (builder, file) => builder.WithResourceMapping(
                    Encoding.UTF8.GetBytes(file.Value), $"/home/wiremock/__files/{file.Key}"))
                .Build();

    private static IContainer BuildContainer(string[] extraCommandArgs) => BuilderFor(extraCommandArgs).Build();

    private static ContainerBuilder BuilderFor(string[] extraCommandArgs) =>
        new ContainerBuilder(Image)
            .WithPortBinding(WireMockPort, assignRandomHostPort: true)
            .WithPortBinding(WireMockHttpsPort, assignRandomHostPort: true)
            // Enable the HTTPS listener too (G11a); WireMock serves it with its default self-signed cert.
            .WithCommand(["--https-port", "8443", .. extraCommandArgs])
            // Lets the container reach a host-side webhook receiver (G3) via host.docker.internal on
            // Linux CI, where — unlike Docker Desktop — it is not resolvable by default.
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(WireMockPort).ForPath("/__admin/mappings")));

    private HttpClient? _client;
    private HttpClient? _httpsClient;

    // UseCookies is disabled so an explicit Cookie request header passes through unmodified
    // (otherwise the handler's cookie container would manage it).
    private HttpClient Client => _client ??= new HttpClient(new SocketsHttpHandler { UseCookies = false })
    {
        BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(WireMockPort)}"),
    };

    // Bound to the oracle's HTTPS listener; the self-signed cert is accepted (parity is about the HTTP
    // response served over TLS, not the certificate). Fresh connection per request, like Client.
    private HttpClient HttpsClient => _httpsClient ??= new HttpClient(new SocketsHttpHandler
    {
        UseCookies = false,
        SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
    })
    {
        BaseAddress = new Uri($"https://{_container.Hostname}:{_container.GetMappedPublicPort(WireMockHttpsPort)}"),
    };

    /// <summary>Starts the oracle container.</summary>
    public Task StartAsync() => _container.StartAsync();

    /// <summary>The oracle's plaintext base address, for tests that drive it with a custom client (G11b).</summary>
    public Uri HttpBaseAddress =>
        new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(WireMockPort)}");

    /// <summary>The oracle's HTTPS base address, for tests that drive it with a custom client (G11b).</summary>
    public Uri HttpsBaseAddress =>
        new($"https://{_container.Hostname}:{_container.GetMappedPublicPort(WireMockHttpsPort)}");

    /// <summary>A fresh HTTP client bound to the oracle, for driving its admin API directly (G7b).</summary>
    public HttpClient CreateAdminClient() => new()
    {
        BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(WireMockPort)}"),
    };

    /// <summary>Clears all stubs and the request journal.</summary>
    public async Task ResetAsync()
    {
        using var response = await Client.PostAsync("/__admin/reset", content: null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Loads WireMock stub mapping JSON. A single mapping goes to <c>/__admin/mappings</c>; a
    /// <c>{"mappings":[...]}</c> wrapper is loaded via <c>/__admin/mappings/import</c>, which
    /// preserves array order (array-first wins on equal priority).
    /// </summary>
    public async Task LoadMappingAsync(string wireMockJson)
    {
        var path = IsMappingsWrapper(wireMockJson) ? "/__admin/mappings/import" : "/__admin/mappings";
        using var content = new StringContent(wireMockJson, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync(path, content);
        response.EnsureSuccessStatusCode();
    }

    private static bool IsMappingsWrapper(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty("mappings", out var mappings) &&
               mappings.ValueKind == JsonValueKind.Array;
    }

    /// <summary>Counts journaled requests matching a pattern via <c>/__admin/requests/count</c> (G6).</summary>
    public async Task<int> CountRequestsMatchingAsync(string patternJson)
    {
        using var content = new StringContent(patternJson, Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/__admin/requests/count", content);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("count").GetInt32();
    }

    /// <summary>The count of unmatched journaled requests via <c>/__admin/requests/unmatched</c> (G6).</summary>
    public async Task<int> UnmatchedCountAsync()
    {
        using var response = await Client.GetAsync("/__admin/requests/unmatched");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("requests").GetArrayLength();
    }

    /// <summary>Replays a request against the oracle over HTTP and snapshots the response.</summary>
    public Task<HttpResponseSnapshot> SendAsync(RequestSpec spec) => SendAsync(spec, Client);

    /// <summary>Replays a request against the oracle's HTTPS listener and snapshots the response (G11a).</summary>
    public Task<HttpResponseSnapshot> SendHttpsAsync(RequestSpec spec) => SendAsync(spec, HttpsClient);

    private static async Task<HttpResponseSnapshot> SendAsync(RequestSpec spec, HttpClient client)
    {
        using var request = new HttpRequestMessage(new HttpMethod(spec.Method), spec.Url);
        if (spec.Body is { } body)
        {
            request.Content = new ByteArrayContent(body);
        }

        foreach (var header in spec.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Force a fresh connection per request: on a reused keep-alive connection the oracle
        // receives a mangled (lowercased) Cookie header, which made cookie value matching diverge
        // spuriously. A fresh connection transmits the header verbatim. See docs/parity/g1-matching.md.
        request.Headers.ConnectionClose = true;
        using var response = await client.SendAsync(request);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = [.. header.Value];
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = [.. header.Value];
        }

        return new HttpResponseSnapshot
        {
            Status = (int)response.StatusCode,
            Headers = headers,
            Body = await response.Content.ReadAsByteArrayAsync(),
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _httpsClient?.Dispose();
        await _container.DisposeAsync();
    }
}
