using System.Diagnostics;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;
using Mockifyr.Differential.Generator;
using Mockifyr.Facade.Library;
using Mockifyr.Outbound;

namespace Mockifyr.Differential.Harness;

/// <summary>The full outcome of a differential case: the diff plus both snapshots for reporting.</summary>
public sealed record DifferentialOutcome(
    DiffResult Diff,
    HttpResponseSnapshot Oracle,
    HttpResponseSnapshot Mockifyr);

/// <summary>The outcome of a single probe against a loaded scenario.</summary>
public sealed record ProbeOutcome(HttpResponseSnapshot Oracle, HttpResponseSnapshot Mockifyr, DiffResult Diff)
{
    /// <summary>Whether the oracle served a stub (any status other than 404 no-match).</summary>
    public bool OracleMatched => Oracle.Status != 404;

    /// <summary>Whether Mockifyr served a stub.</summary>
    public bool MockifyrMatched => Mockifyr.Status != 404;

    /// <summary>Whether both sides agree on the match/no-match decision.</summary>
    public bool DecisionAgrees => OracleMatched == MockifyrMatched;
}

/// <summary>The webhooks each side fired for a G3 case (null if a side fired none within the wait).</summary>
public sealed record WebhookOutcome(CapturedWebhook? Oracle, CapturedWebhook? Mockifyr);

/// <summary>Per-pattern match counts plus the unmatched-request counts, for a G6 verify case.</summary>
public sealed record VerifyOutcome(
    IReadOnlyList<(string Pattern, int Oracle, int Mockifyr)> Counts,
    int OracleUnmatched,
    int MockifyrUnmatched);

/// <summary>The proxied response each side returned for a G8 case.</summary>
public sealed record ProxyOutcome(HttpResponseSnapshot Oracle, HttpResponseSnapshot Mockifyr);

/// <summary>
/// The responses for a G9 record→playback case: what Mockifyr <em>captured</em> when it proxied,
/// then what the oracle and Mockifyr each <em>replay</em> from Mockifyr's generated stubs.
/// </summary>
public sealed record RecordPlaybackOutcome(
    IReadOnlyList<HttpResponseSnapshot> Captured,
    IReadOnlyList<HttpResponseSnapshot> OracleReplay,
    IReadOnlyList<HttpResponseSnapshot> MockifyrReplay);

/// <summary>
/// Orchestrates differential cases: load the same WireMock JSON into the oracle and Mockifyr,
/// replay the same request(s), and diff. Reuses a single oracle container across cases; for
/// fuzzing it loads a stub once and sends many probes.
/// </summary>
public sealed class DifferentialRunner : IAsyncDisposable
{
    private readonly WireMockOracle _oracle = new();
    private MockifyrUnderTest? _mockifyr;

    /// <summary>Starts the oracle container.</summary>
    public Task StartAsync() => _oracle.StartAsync();

    /// <summary>Runs a single case (reset + load + send) and returns the diff outcome.</summary>
    public async Task<DifferentialOutcome> RunAsync(string wireMockJson, RequestSpec request)
    {
        await LoadAsync(wireMockJson);
        var probe = await ProbeAsync(request);
        return new DifferentialOutcome(probe.Diff, probe.Oracle, probe.Mockifyr);
    }

    /// <summary>Resets the oracle and loads one stub mapping into both sides (for send-many fuzzing).</summary>
    public async Task LoadAsync(string wireMockJson)
    {
        await _oracle.ResetAsync();
        await _oracle.LoadMappingAsync(wireMockJson);
        _mockifyr = new MockifyrUnderTest();
        _mockifyr.ImportMappingJson(wireMockJson);
    }

    /// <summary>
    /// Drives a webhook (G3) case: loads the same mapping into each side with the receiver host
    /// rewritten to what that side can reach, triggers the stub, and captures the outbound webhook
    /// each side fires. The <c>__WEBHOOK_HOST__</c> token in the mapping is replaced per side.
    /// </summary>
    public async Task<WebhookOutcome> RunWebhookAsync(WebhookReceiver receiver, string mappingTemplate, RequestSpec trigger)
    {
        // Oracle: reaches the host-side receiver through host.docker.internal.
        receiver.Clear();
        await _oracle.ResetAsync();
        await _oracle.LoadMappingAsync(mappingTemplate.Replace("__WEBHOOK_HOST__", $"host.docker.internal:{receiver.Port}"));
        await _oracle.SendAsync(trigger);
        var oracle = await receiver.WaitForOneAsync(TimeSpan.FromSeconds(10));

        // Mockifyr: in-process, so it reaches the receiver over loopback.
        receiver.Clear();
        var mockifyr = new MockifyrUnderTest();
        mockifyr.ImportMappingJson(mappingTemplate.Replace("__WEBHOOK_HOST__", $"127.0.0.1:{receiver.Port}"));
        mockifyr.Send(trigger);
        var mockifyrWebhook = await receiver.WaitForOneAsync(TimeSpan.FromSeconds(10));

        return new WebhookOutcome(oracle, mockifyrWebhook);
    }

    /// <summary>
    /// Drives a verify (G6) case: loads the stubs, replays traffic through both sides to fill the
    /// journals, then compares the match-count for each pattern and the unmatched-request count. The
    /// admin JSON is full of volatile fields, so verification is compared <em>semantically</em>
    /// (counts) rather than byte-for-byte.
    /// </summary>
    public async Task<VerifyOutcome> RunVerifyAsync(
        string mappingsJson, IReadOnlyList<RequestSpec> traffic, IReadOnlyList<string> countPatterns)
    {
        await _oracle.ResetAsync();
        await _oracle.LoadMappingAsync(mappingsJson);
        _mockifyr = new MockifyrUnderTest();
        _mockifyr.ImportMappingJson(mappingsJson);

        foreach (var request in traffic)
        {
            await _oracle.SendAsync(request);
            _mockifyr.Send(request);
        }

        var counts = new List<(string, int, int)>();
        foreach (var pattern in countPatterns)
        {
            counts.Add((pattern, await _oracle.CountRequestsMatchingAsync(pattern), _mockifyr.CountRequestsMatching(pattern)));
        }

        return new VerifyOutcome(counts, await _oracle.UnmatchedCountAsync(), _mockifyr.UnmatchedCount());
    }

    /// <summary>
    /// Drives a proxy (G8) case: loads a <c>proxyBaseUrl</c> stub into each side with the upstream
    /// host rewritten to what that side can reach, sends the request, and returns the proxied
    /// response each side produced (the oracle forwards over HTTP; Mockifyr via its
    /// <c>ProxyResponder</c>). The <c>__PROXY_HOST__</c> token is replaced per side.
    /// </summary>
    public async Task<ProxyOutcome> RunProxyAsync(UpstreamServer upstream, string stubTemplate, RequestSpec request)
    {
        await _oracle.ResetAsync();
        await _oracle.LoadMappingAsync(stubTemplate.Replace("__PROXY_HOST__", $"host.docker.internal:{upstream.Port}"));
        var oracle = await _oracle.SendAsync(request);

        var mockifyr = new MockifyrUnderTest();
        mockifyr.ImportMappingJson(stubTemplate.Replace("__PROXY_HOST__", $"127.0.0.1:{upstream.Port}"));
        var mockifyrResponse = await mockifyr.SendWithProxyAsync(request);

        return new ProxyOutcome(oracle, mockifyrResponse);
    }

    /// <summary>
    /// Drives a record→playback (G9) case: Mockifyr proxies each request to the upstream, capturing
    /// the response and generating a stub from the exchange. The generated stubs are then loaded into
    /// <em>both</em> the oracle and a fresh Mockifyr and the requests replayed — so a stub Mockifyr
    /// recorded is proven to be WireMock-valid and to replay the captured response on the real oracle.
    /// </summary>
    public async Task<RecordPlaybackOutcome> RunRecordPlaybackAsync(
        UpstreamServer upstream, IReadOnlyList<RequestSpec> requests)
    {
        var recorder = new StubRecorder();
        var stubs = new List<string>();
        var captured = new List<HttpResponseSnapshot>();
        foreach (var spec in requests)
        {
            var request = CanonicalRequestBuilder.Build(spec.Method, spec.Url, spec.Headers, spec.Body);
            var exchange = await recorder.RecordAsync($"http://127.0.0.1:{upstream.Port}", request);
            stubs.Add(RecordingJsonWriter.ToStubJson(request, exchange.StubResponse));
            captured.Add(ToSnapshot(exchange.CapturedResponse));
        }

        var bundle = "{\"mappings\":[" + string.Join(",", stubs) + "]}";

        // The oracle replays Mockifyr's generated stubs.
        await _oracle.ResetAsync();
        await _oracle.LoadMappingAsync(bundle);
        var oracleReplay = new List<HttpResponseSnapshot>();
        foreach (var spec in requests)
        {
            oracleReplay.Add(await _oracle.SendAsync(spec));
        }

        // A fresh Mockifyr replays them too.
        var mockifyr = new MockifyrUnderTest();
        mockifyr.ImportMappingJson(bundle);
        var mockifyrReplay = requests.Select(spec => mockifyr.Send(spec)).ToList();

        return new RecordPlaybackOutcome(captured, oracleReplay, mockifyrReplay);
    }

    private static HttpResponseSnapshot ToSnapshot(CanonicalResponse response) => new()
    {
        Status = response.Status,
        Headers = response.Headers.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase),
        Body = response.Body,
    };

    /// <summary>
    /// Drives a remote-<c>$ref</c> schema case (G1h): the stub's <c>$ref</c> host (<c>__SCHEMA_HOST__</c>)
    /// is rewritten to what each side can reach (the oracle via <c>host.docker.internal</c>, Mockifyr via
    /// <c>127.0.0.1</c>), so both resolve the same host-side schema. Returns the match-decision probe.
    /// </summary>
    public async Task<ProbeOutcome> RunSchemaRefAsync(int schemaPort, string stubTemplate, RequestSpec request)
    {
        await _oracle.ResetAsync();
        await _oracle.LoadMappingAsync(stubTemplate.Replace("__SCHEMA_HOST__", $"host.docker.internal:{schemaPort}"));
        var oracle = await _oracle.SendAsync(request);

        _mockifyr = new MockifyrUnderTest();
        _mockifyr.ImportMappingJson(stubTemplate.Replace("__SCHEMA_HOST__", $"127.0.0.1:{schemaPort}"));
        var mockifyr = _mockifyr.Send(request, "http");

        var diff = ResponseDiffer.Compare(oracle, mockifyr, mockifyr.Headers.Keys);
        return new ProbeOutcome(oracle, mockifyr, diff);
    }

    /// <summary>Replays one request against the currently loaded stub on both sides and diffs.</summary>
    public async Task<ProbeOutcome> ProbeAsync(RequestSpec request)
    {
        if (_mockifyr is null)
        {
            throw new InvalidOperationException("Call LoadAsync before ProbeAsync.");
        }

        var oracle = await _oracle.SendAsync(request);
        // The oracle is driven over plaintext HTTP, so mirror scheme=http into Mockifyr's request
        // (for request.scheme/baseUrl templating). Host/port derive from the Host header in the builder.
        var mockifyr = _mockifyr.Send(request, "http");
        var diff = ResponseDiffer.Compare(oracle, mockifyr, mockifyr.Headers.Keys);
        return new ProbeOutcome(oracle, mockifyr, diff);
    }

    /// <summary>
    /// Like <see cref="ProbeAsync"/> but drives the request over the oracle's HTTP or HTTPS listener
    /// and mirrors that scheme into Mockifyr's request, so WireMock's <c>request.scheme</c> matching
    /// (multi-domain, G15c) can be diffed. Host/port derive from the request's <c>Host</c> header.
    /// </summary>
    public async Task<ProbeOutcome> ProbeSchemeAsync(RequestSpec request, bool https)
    {
        if (_mockifyr is null)
        {
            throw new InvalidOperationException("Call LoadAsync before ProbeSchemeAsync.");
        }

        var oracle = https ? await _oracle.SendHttpsAsync(request) : await _oracle.SendAsync(request);
        var mockifyr = _mockifyr.Send(request, https ? "https" : "http");
        var diff = ResponseDiffer.Compare(oracle, mockifyr, mockifyr.Headers.Keys);
        return new ProbeOutcome(oracle, mockifyr, diff);
    }

    /// <summary>
    /// Like <see cref="ProbeAsync"/> but also measures each side's wall-clock handling time, for
    /// validating response delays (G4). Only a generous <em>lower</em> bound is asserted by callers —
    /// a fixed delay can't make a response faster, so it is robust against CI timing variance.
    /// </summary>
    public async Task<(ProbeOutcome Outcome, long OracleMs, long MockifyrMs)> ProbeTimedAsync(RequestSpec request)
    {
        if (_mockifyr is null)
        {
            throw new InvalidOperationException("Call LoadAsync before ProbeTimedAsync.");
        }

        var oracleTimer = Stopwatch.StartNew();
        var oracle = await _oracle.SendAsync(request);
        oracleTimer.Stop();

        var mockifyrTimer = Stopwatch.StartNew();
        // The oracle is driven over plaintext HTTP, so mirror scheme=http into Mockifyr's request
        // (for request.scheme/baseUrl templating). Host/port derive from the Host header in the builder.
        var mockifyr = _mockifyr.Send(request, "http");
        mockifyrTimer.Stop();

        var diff = ResponseDiffer.Compare(oracle, mockifyr, mockifyr.Headers.Keys);
        return (new ProbeOutcome(oracle, mockifyr, diff), oracleTimer.ElapsedMilliseconds, mockifyrTimer.ElapsedMilliseconds);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _oracle.DisposeAsync();
}
