using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Mockifyr.Differential.Generator;
using Mockifyr.Differential.Harness;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential validation of the two outbound-at-the-edge facade behaviors over <em>real HTTP</em>
/// (G12d):
/// <list type="bullet">
/// <item>a <c>proxyBaseUrl</c> stub driven over the wire against both the oracle and a hosted Mockifyr
/// must relay the same upstream response — closing the wire gap left when G8 validated proxying only
/// in-process;</item>
/// <item>record-through-proxy driven over the wire: start a recording on the hosted Mockifyr, drive
/// requests through its mock-serving fallback (each proxied to the upstream and captured), stop, then
/// replay the generated stubs on the <em>real</em> oracle — proving the wire-recorded stubs are
/// WireMock-valid and replay the captured response.</item>
/// </list>
/// Both sides share one upstream (the oracle reaches it via <c>host.docker.internal</c>, Mockifyr via
/// <c>127.0.0.1</c>). Requires Docker.
/// </summary>
public sealed class G12dProxyRecordTests : IAsyncLifetime
{
    private static readonly string[] StableHeaders = ["X-Upstream", "Content-Type"];

    private readonly WireMockOracle _oracle = new();
    private readonly WebApplicationFactory<Program> _mockifyr = new();
    private readonly UpstreamServer _upstream = new();

    public Task InitializeAsync() => _oracle.StartAsync();

    public async Task DisposeAsync()
    {
        _upstream.Dispose();
        await _mockifyr.DisposeAsync();
        await _oracle.DisposeAsync();
    }

    private sealed record WireResult(int Status, byte[] Body, IReadOnlyDictionary<string, string> Headers);

    [Fact]
    public async Task Proxy_OverTheWire_MatchesOracle()
    {
        using var oracleClient = _oracle.CreateAdminClient();
        using var mockifyrClient = _mockifyr.CreateClient();
        var failures = new List<string>();

        foreach (var scenario in ProxyScenarios.All())
        {
            // Each side reaches the shared upstream by the host it can address.
            var oracleStub = scenario.StubTemplate.Replace("__PROXY_HOST__", $"host.docker.internal:{_upstream.Port}");
            var mockifyrStub = scenario.StubTemplate.Replace("__PROXY_HOST__", $"127.0.0.1:{_upstream.Port}");

            await LoadStubAsync(oracleClient, oracleStub);
            await LoadStubAsync(mockifyrClient, mockifyrStub);

            var oracle = await DriveAsync(oracleClient, scenario.Request);
            var mockifyr = await DriveAsync(mockifyrClient, scenario.Request);

            if (oracle.Status != mockifyr.Status)
            {
                failures.Add($"{scenario.Description}: status oracle={oracle.Status} mockifyr={mockifyr.Status}");
            }

            if (!oracle.Body.AsSpan().SequenceEqual(mockifyr.Body))
            {
                failures.Add($"{scenario.Description}: body oracle=\"{Text(oracle.Body)}\" mockifyr=\"{Text(mockifyr.Body)}\"");
            }

            foreach (var header in StableHeaders)
            {
                var o = oracle.Headers.GetValueOrDefault(header);
                var m = mockifyr.Headers.GetValueOrDefault(header);
                if (!string.Equals(o, m, StringComparison.Ordinal))
                {
                    failures.Add($"{scenario.Description}: header[{header}] oracle={o ?? "<absent>"} mockifyr={m ?? "<absent>"}");
                }
            }

            // Sanity: the response really came from the upstream, not a stub body.
            if (!Text(mockifyr.Body).Contains("upstream"))
            {
                failures.Add($"{scenario.Description}: mockifyr did not proxy (body=\"{Text(mockifyr.Body)}\")");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} proxy-over-wire divergence(s):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public async Task Record_OverTheWire_GeneratesStubsThatReplayOnOracle()
    {
        using var mockifyrClient = _mockifyr.CreateClient();
        using var oracleClient = _oracle.CreateAdminClient();

        var requests = new List<RequestSpec>
        {
            new() { Method = "GET", Url = "/rec/users/1?full=true" },
            new() { Method = "POST", Url = "/rec/orders", Body = Encoding.UTF8.GetBytes("{\"item\":\"book\"}") },
            new() { Method = "GET", Url = "/rec/health" },
        };

        // Start recording on the hosted Mockifyr, pointed at the upstream it can reach.
        await StartRecordingAsync(mockifyrClient, $"http://127.0.0.1:{_upstream.Port}");

        // Drive each request through the mock-serving fallback: proxied to the upstream and captured.
        var captured = new List<WireResult>();
        foreach (var request in requests)
        {
            captured.Add(await DriveAsync(mockifyrClient, request));
        }

        // Stop recording — the generated stubs come back in a {"mappings":[…]} envelope.
        var bundle = await StopRecordingAsync(mockifyrClient);

        var failures = new List<string>();

        // Sanity: recording actually captured (one stub per request) and each response came from upstream.
        var stubCount = bundle.Split("\"request\"").Length - 1;
        if (stubCount != requests.Count)
        {
            failures.Add($"recorded {stubCount} stub(s), expected {requests.Count}");
        }

        // Load the wire-recorded stubs into the real oracle and replay — proving they are WireMock-valid.
        await LoadStubAsync(oracleClient, bundle);
        for (var i = 0; i < requests.Count; i++)
        {
            var replay = await DriveAsync(oracleClient, requests[i]);
            var captureResult = captured[i];

            if (!Text(captureResult.Body).Contains("upstream"))
            {
                failures.Add($"{requests[i].Method} {requests[i].Url}: mockifyr did not proxy while recording");
            }

            if (replay.Status != captureResult.Status)
            {
                failures.Add($"{requests[i].Method} {requests[i].Url}: replay status={replay.Status} captured={captureResult.Status}");
            }

            if (!replay.Body.AsSpan().SequenceEqual(captureResult.Body))
            {
                failures.Add($"{requests[i].Method} {requests[i].Url}: oracle replay body != captured — \"{Text(replay.Body)}\" vs \"{Text(captureResult.Body)}\"");
            }

            var replayUpstream = replay.Headers.GetValueOrDefault("X-Upstream");
            var capturedUpstream = captureResult.Headers.GetValueOrDefault("X-Upstream");
            if (!string.Equals(replayUpstream, capturedUpstream, StringComparison.Ordinal))
            {
                failures.Add($"{requests[i].Method} {requests[i].Url}: X-Upstream replay={replayUpstream} captured={capturedUpstream}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} record-over-wire divergence(s):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public async Task Recording_ProxiesEveryRequest_EvenOnesAnExistingStubWouldMatch()
    {
        using var oracleClient = _oracle.CreateAdminClient();
        using var mockifyrClient = _mockifyr.CreateClient();
        var failures = new List<string>();

        // A stub that exists BEFORE recording starts, with a body that deliberately lacks "upstream"
        // so a proxied answer is unmistakable. Learned from this diff (recorded in docs/parity):
        // while a recording is live the oracle proxies EVERY request to the target — a request an
        // existing stub would match is proxied and captured too, it does NOT serve the stub. Mockifyr
        // must diverge in neither direction: same upstream answer, same capture count.
        const string keptStub =
            """{"request":{"method":"GET","urlPath":"/kept"},"response":{"status":200,"body":"stub-wins"}}""";
        await LoadStubAsync(oracleClient, keptStub);
        await LoadStubAsync(mockifyrClient, keptStub);

        await StartRecordingAsync(oracleClient, $"http://host.docker.internal:{_upstream.Port}", reset: false);
        await StartRecordingAsync(mockifyrClient, $"http://127.0.0.1:{_upstream.Port}", reset: false);

        var requests = new[]
        {
            new RequestSpec { Method = "GET", Url = "/kept" },      // an existing stub matches this
            new RequestSpec { Method = "GET", Url = "/rec/extra" }, // nothing matches this
        };
        foreach (var request in requests)
        {
            var oracle = await DriveAsync(oracleClient, request);
            var mockifyr = await DriveAsync(mockifyrClient, request);
            if (oracle.Status != mockifyr.Status || Text(oracle.Body) != Text(mockifyr.Body))
            {
                failures.Add($"{request.Url} while recording diverges: oracle={oracle.Status}/\"{Text(oracle.Body)}\" " +
                    $"mockifyr={mockifyr.Status}/\"{Text(mockifyr.Body)}\"");
            }

            if (!Text(mockifyr.Body).Contains("upstream"))
            {
                failures.Add($"{request.Url}: expected the upstream's answer while recording, got \"{Text(mockifyr.Body)}\"");
            }
        }

        // Both exchanges were captured on both sides — the matched one included.
        var oracleCount = (await StopRecordingAsync(oracleClient)).Split("\"request\"").Length - 1;
        var mockifyrCount = (await StopRecordingAsync(mockifyrClient)).Split("\"request\"").Length - 1;
        if (oracleCount != mockifyrCount)
        {
            failures.Add($"captured-stub count diverges: oracle={oracleCount} mockifyr={mockifyrCount}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} record-matched divergence(s):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public async Task Recording_AGzippedUpstreamResponse_GeneratesAReplayableStub()
    {
        using var oracleClient = _oracle.CreateAdminClient();
        using var mockifyrClient = _mockifyr.CreateClient();
        var failures = new List<string>();

        // The /gzip/* upstream path answers gzip-compressed with a Content-Encoding header — like a
        // real API compressing large payloads (how this surfaced: recording jsonplaceholder from a
        // browser baked raw gzip bytes into the generated stub body as mojibake).
        await StartRecordingAsync(oracleClient, $"http://host.docker.internal:{_upstream.Port}");
        await StartRecordingAsync(mockifyrClient, $"http://127.0.0.1:{_upstream.Port}");

        var request = new RequestSpec { Method = "GET", Url = "/gzip/data" };
        var oracleLive = await DriveAsync(oracleClient, request);
        var mockifyrLive = await DriveAsync(mockifyrClient, request);

        // While recording, the compressed exchange passes through to the caller on both sides.
        if (Payload(oracleLive) != Payload(mockifyrLive))
        {
            failures.Add($"live payload diverges: oracle=\"{Payload(oracleLive)}\" mockifyr=\"{Payload(mockifyrLive)}\"");
        }

        var oracleBundle = await StopRecordingAsync(oracleClient);
        var mockifyrBundle = await StopRecordingAsync(mockifyrClient);

        // Each side replays its own generated stub; the client-visible payload must match the
        // upstream's original text on both sides — a stub that baked in raw gzip bytes cannot.
        await LoadStubAsync(oracleClient, oracleBundle);
        await LoadStubAsync(mockifyrClient, mockifyrBundle);
        var oracleReplay = await DriveAsync(oracleClient, request);
        var mockifyrReplay = await DriveAsync(mockifyrClient, request);

        foreach (var (name, replay) in new[] { ("oracle", oracleReplay), ("mockifyr", mockifyrReplay) })
        {
            if (!Payload(replay).Contains("\"from\":\"upstream\""))
            {
                failures.Add($"{name} replay payload is not the upstream text: \"{Payload(replay)}\"");
            }
        }

        if (oracleReplay.Status != mockifyrReplay.Status || Payload(oracleReplay) != Payload(mockifyrReplay))
        {
            failures.Add($"replay diverges: oracle={oracleReplay.Status}/\"{Payload(oracleReplay)}\" " +
                $"mockifyr={mockifyrReplay.Status}/\"{Payload(mockifyrReplay)}\"");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} gzip-record divergence(s):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public async Task Recording_RepeatedIdenticalRequests_CaptureLikeTheOracle()
    {
        using var oracleClient = _oracle.CreateAdminClient();
        using var mockifyrClient = _mockifyr.CreateClient();
        var failures = new List<string>();

        await StartRecordingAsync(oracleClient, $"http://host.docker.internal:{_upstream.Port}");
        await StartRecordingAsync(mockifyrClient, $"http://127.0.0.1:{_upstream.Port}");

        // The same request twice — spaced across a Date-header second boundary, so the diff also
        // answers whether volatile headers break the oracle's identity — plus one distinct request.
        var dup = new RequestSpec { Method = "GET", Url = "/rec/dup" };
        var other = new RequestSpec { Method = "GET", Url = "/rec/other" };
        await DriveAsync(oracleClient, dup);
        await DriveAsync(mockifyrClient, dup);
        await Task.Delay(1100);
        await DriveAsync(oracleClient, dup);
        await DriveAsync(mockifyrClient, dup);
        await DriveAsync(oracleClient, other);
        await DriveAsync(mockifyrClient, other);

        var oracleBundle = await StopRecordingAsync(oracleClient);
        var mockifyrBundle = await StopRecordingAsync(mockifyrClient);

        // Learned from this diff: the oracle does NOT deduplicate — a repeated request becomes a
        // SCENARIO CHAIN (first capture serves at Started and advances, the next serves from that
        // state), so a replay yields the recorded responses in recorded order. Distinct requests
        // stay scenario-free. Names/ids are generated values, so the comparison is structural.
        foreach (var (name, bundle) in new[] { ("oracle", oracleBundle), ("mockifyr", mockifyrBundle) })
        {
            using var doc = JsonDocument.Parse(bundle);
            var mappings = doc.RootElement.GetProperty("mappings").EnumerateArray().ToList();
            if (mappings.Count != 3)
            {
                failures.Add($"{name}: captured {mappings.Count} stub(s), expected 3 (two chained + one plain)");
                continue;
            }

            var dups = mappings.Where(m => m.GetProperty("request").GetProperty("url").GetString() == "/rec/dup").ToList();
            var plain = mappings.Single(m => m.GetProperty("request").GetProperty("url").GetString() == "/rec/other");

            if (plain.TryGetProperty("scenarioName", out _))
            {
                failures.Add($"{name}: the distinct request must stay scenario-free");
            }

            string? Field(JsonElement m, string field) => m.TryGetProperty(field, out var v) ? v.GetString() : null;
            var first = dups.SingleOrDefault(m => Field(m, "requiredScenarioState") == "Started");
            var second = dups.SingleOrDefault(m => Field(m, "requiredScenarioState") != "Started");
            if (first.ValueKind == JsonValueKind.Undefined || second.ValueKind == JsonValueKind.Undefined)
            {
                failures.Add($"{name}: repeated request did not form a Started→next scenario chain: {bundle}");
                continue;
            }

            var scenario = Field(first, "scenarioName");
            if (scenario is null || Field(second, "scenarioName") != scenario)
            {
                failures.Add($"{name}: chained stubs must share one scenarioName");
            }

            if (Field(first, "newScenarioState") is not { } next || Field(second, "requiredScenarioState") != next)
            {
                failures.Add($"{name}: first capture must advance to the state the second serves from");
            }

            if (Field(second, "newScenarioState") is not null)
            {
                failures.Add($"{name}: the last capture in a chain must not advance further");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} repeat-capture divergence(s):\n{string.Join("\n", failures)}");
    }

    /// <summary>The client-visible payload text: gunzips when the response declares gzip encoding.</summary>
    private static string Payload(WireResult result)
    {
        if (!result.Headers.TryGetValue("Content-Encoding", out var encoding) ||
            !encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            return Text(result.Body);
        }

        try
        {
            using var source = new MemoryStream(result.Body);
            using var gzip = new System.IO.Compression.GZipStream(source, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (InvalidDataException)
        {
            // Declared gzip but the bytes no longer decompress — the corruption this test exists for.
            return $"<invalid-gzip:{result.Body.Length} bytes>";
        }
    }

    private static async Task LoadStubAsync(HttpClient client, string stubOrBundleJson)
    {
        await client.PostAsync("/__admin/mappings/reset", content: null);
        using var load = new StringContent(stubOrBundleJson, Encoding.UTF8, "application/json");
        // A single mapping goes to /mappings; a {"mappings":[…]} bundle to /mappings/import.
        var path = stubOrBundleJson.Contains("\"mappings\"") ? "/__admin/mappings/import" : "/__admin/mappings";
        await client.PostAsync(path, load);
    }

    private static async Task StartRecordingAsync(HttpClient client, string targetBaseUrl, bool reset = true)
    {
        if (reset)
        {
            await client.PostAsync("/__admin/mappings/reset", content: null);
        }
        using var body = new StringContent(
            "{\"targetBaseUrl\":\"" + targetBaseUrl + "\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/__admin/recordings/start", body);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> StopRecordingAsync(HttpClient client)
    {
        var response = await client.PostAsync("/__admin/recordings/stop", content: null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<WireResult> DriveAsync(HttpClient client, RequestSpec request)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body is { } body)
        {
            message.Content = new ByteArrayContent(body);
        }

        using var response = await client.SendAsync(message);
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        return new WireResult((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(), headers);
    }

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
