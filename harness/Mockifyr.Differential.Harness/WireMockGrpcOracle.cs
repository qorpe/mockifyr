using System.Net.Http;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Mockifyr.Differential.Harness;

/// <summary>
/// The gRPC test oracle (G13): the same Java WireMock image with the official gRPC extension loaded,
/// serving gRPC over HTTP/2. The extension jar (mounted into <c>/var/wiremock/extensions</c>), the
/// compiled proto descriptor (<c>/home/wiremock/grpc</c>), and the stub mapping
/// (<c>/home/wiremock/mappings</c>) are copied into the container. The extension converts protobuf to
/// JSON and matches with the ordinary stub engine — so a gRPC stub is just a POST-to-
/// <c>/service/method</c> stub with an <c>equalToJson</c> body and a <c>jsonBody</c> response.
/// </summary>
public sealed class WireMockGrpcOracle : IAsyncDisposable
{
    private const ushort WireMockPort = 8080;
    private const ushort WireMockHttpsPort = 8443;

    private const string ExtensionUrl =
        "https://repo1.maven.org/maven2/org/wiremock/wiremock-grpc-extension-standalone/0.11.0/" +
        "wiremock-grpc-extension-standalone-0.11.0.jar";

    private readonly IContainer _container;

    /// <summary>
    /// Every path the mounted stubs serve, e.g. <c>/mockifyr.grpc.test.Greeter/Wrapped</c> — what
    /// readiness actually has to wait for (#367).
    /// </summary>
    /// <remarks>
    /// All of them, not the first: a mapping file carrying two methods is loaded as a unit, but waiting
    /// for one of them would leave the same race for the other and pass while looking thorough.
    /// </remarks>
    private readonly IReadOnlyList<string> _servedPaths;

    public WireMockGrpcOracle(byte[] descriptorSet, string mappingJson)
    {
        _servedPaths = ServedPathsOf(mappingJson);

        // gRPC needs HTTP/2; the plaintext h2c path is nondeterministic on WireMock (HTTP_1_1_REQUIRED),
        // so gRPC is driven over TLS (ALPN-negotiated h2), which is deterministic. See g11-tls-http2.md.
        _container = new ContainerBuilder(WireMockOracle.Image)
            .WithPortBinding(WireMockPort, assignRandomHostPort: true)
            .WithPortBinding(WireMockHttpsPort, assignRandomHostPort: true)
            .WithCommand("--https-port", "8443")
            .WithResourceMapping(GetExtensionJar(), "/var/wiremock/extensions/grpc.jar")
            .WithResourceMapping(descriptorSet, "/home/wiremock/grpc/service.dsc")
            .WithResourceMapping(Encoding.UTF8.GetBytes(mappingJson), "/home/wiremock/mappings/stub.json")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(WireMockPort).ForPath("/__admin/mappings")))
            .Build();
    }

    /// <summary>
    /// Starts the oracle and waits until it is <b>serving the mounted stub</b> over the HTTPS listener
    /// gRPC uses (#367).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two preconditions, and only one of them used to be established. The container wait strategy gates
    /// on the plaintext admin port; the previous poll added the HTTPS listener. Neither says the stub is
    /// loaded — it is mounted as a file and read by the gRPC extension after the admin surface answers —
    /// so a call could arrive between "the port accepts" and "the method is registered" and get a plain
    /// <c>404</c>, which the gRPC client reports as <c>Unimplemented: Bad gRPC response</c>. That reads
    /// exactly like the two engines disagreeing, which is the one thing this suite must never say by
    /// mistake.
    /// </para>
    /// <para>
    /// So readiness asks the question the test is about to ask: does the admin surface list a mapping for
    /// the path this oracle was built to serve. Not a retry around the assertion and not a longer sleep —
    /// a retry could hide a genuine flapping divergence, and a sleep hides the signal either way.
    /// </para>
    /// </remarks>
    public async Task StartAsync()
    {
        await _container.StartAsync();

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var httpsAdmin = new Uri($"{GrpcAddress}__admin/mappings");
        var lastSeen = "no response";

        for (var attempt = 0; attempt < ReadinessAttempts; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(httpsAdmin);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    if (_servedPaths.All(path => body.Contains(path, StringComparison.Ordinal)))
                    {
                        return;
                    }

                    var missing = _servedPaths.Where(path => !body.Contains(path, StringComparison.Ordinal));
                    lastSeen = $"the HTTPS admin surface answered, but these paths were not listed: "
                        + string.Join(", ", missing);
                }
                else
                {
                    lastSeen = $"the HTTPS admin surface answered {(int)response.StatusCode}";
                }
            }
            catch (HttpRequestException failure)
            {
                lastSeen = $"the HTTPS listener refused the connection ({failure.Message})";
            }

            await Task.Delay(ReadinessInterval);
        }

        // Named, not surfaced as a protocol error: the next person should read "the oracle never became
        // ready" rather than work backwards from an Unimplemented status in an unrelated assertion.
        throw new InvalidOperationException(
            $"The gRPC oracle never became ready for '{string.Join("', '", _servedPaths)}' within "
            + $"{ReadinessAttempts * ReadinessInterval.TotalSeconds:0.#}s — {lastSeen}.");
    }

    /// <summary>How many times readiness is checked before the wait is declared a failure.</summary>
    private const int ReadinessAttempts = 60;

    /// <summary>How long between readiness checks.</summary>
    private static readonly TimeSpan ReadinessInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The path the mounted mapping serves — <c>urlPath</c> when it has one, else <c>url</c>.
    /// </summary>
    /// <remarks>
    /// Read from the mapping rather than passed in beside it: two sources for one fact is how a
    /// readiness probe ends up waiting for a path nobody serves and passing instantly.
    /// </remarks>
    private static IReadOnlyList<string> ServedPathsOf(string mappingJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(mappingJson);
        var root = document.RootElement;

        // Both shapes the tests use: one mapping, or a { "mappings": [ … ] } bundle.
        var mappings = root.TryGetProperty("mappings", out var bundle)
            && bundle.ValueKind == System.Text.Json.JsonValueKind.Array
                ? bundle.EnumerateArray().ToList()
                : [root];

        var paths = new List<string>();
        foreach (var mapping in mappings)
        {
            if (!mapping.TryGetProperty("request", out var request))
            {
                continue;
            }

            foreach (var name in new[] { "urlPath", "url" })
            {
                if (request.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } path)
                {
                    paths.Add(path);
                    break;
                }
            }
        }

        if (paths.Count == 0)
        {
            // A readiness probe with nothing to wait for passes instantly and proves nothing, so this is
            // an error at construction rather than a silently useless wait.
            throw new ArgumentException(
                "A gRPC oracle mapping must name at least one served path as url or urlPath.",
                nameof(mappingJson));
        }

        return paths;
    }

    /// <summary>
    /// Calls <paramref name="probe"/> until it stops failing, then returns (#367).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The readiness in <see cref="StartAsync"/> proves the mapping is loaded, and that turned out to be
    /// the wrong half. WireMock also answers <c>404</c> when a stub <em>is</em> loaded and nothing
    /// matched, and until the gRPC extension has read the descriptor it cannot decode a protobuf body —
    /// so <c>equalToJson</c> cannot match and the call 404s with the mapping present and listed. A gRPC
    /// client reports that as <c>Unimplemented: Bad gRPC response</c>, which reads exactly like the two
    /// engines disagreeing.
    /// </para>
    /// <para>
    /// No admin surface reports descriptor readiness, so the only honest probe is the call itself. This
    /// is a warm-up <b>before</b> any comparison begins — deliberately not a retry around a differential
    /// assertion, which could hide a genuine flapping divergence and stays refused.
    /// </para>
    /// <para>
    /// Only the oracle is warmed. Mockifyr's own host is in-process and serving before its start task
    /// completes, so warming it too would hide a real startup fault behind a wait.
    /// </para>
    /// </remarks>
    public async Task WarmUpAsync(Func<Task> probe)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < ReadinessAttempts; attempt++)
        {
            try
            {
                await probe();
                return;
            }
            catch (Exception failure)
            {
                last = failure;
                await Task.Delay(ReadinessInterval);
            }
        }

        throw new InvalidOperationException(
            $"The gRPC oracle never served '{string.Join("', '", _servedPaths)}' within "
            + $"{ReadinessAttempts * ReadinessInterval.TotalSeconds:0.#}s — the descriptor or the mapping "
            + "never became usable. The last attempt failed with: " + last?.Message,
            last);
    }

    /// <summary>The base address for gRPC calls (HTTPS, ALPN-negotiated h2) against the oracle.</summary>
    public Uri GrpcAddress =>
        new($"https://{_container.Hostname}:{_container.GetMappedPublicPort(WireMockHttpsPort)}");

    /// <summary>A fresh HTTP client bound to the oracle's plaintext admin port (for reset/mappings — G13d).</summary>
    public HttpClient CreateAdminClient() => new()
    {
        BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(WireMockPort)}"),
    };

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _container.DisposeAsync();

    // The extension jar is large (~30 MB); download once and cache in the temp dir so repeated test
    // runs (and multiple oracle instances) don't refetch it.
    private static byte[] GetExtensionJar()
    {
        var cache = Path.Combine(Path.GetTempPath(), "wiremock-grpc-extension-standalone-0.11.0.jar");
        if (!File.Exists(cache))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            File.WriteAllBytes(cache, http.GetByteArrayAsync(ExtensionUrl).GetAwaiter().GetResult());
        }

        return File.ReadAllBytes(cache);
    }
}
