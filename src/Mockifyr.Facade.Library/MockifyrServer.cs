using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;
using Mockifyr.ServeEvents.Webhook;
using Mockifyr.Stores.InMemory;
using Mockifyr.Templating;

namespace Mockifyr.Facade.Library;

/// <summary>
/// In-process composition of the engine for direct-call use (tests and the differential
/// harness) — a "no HTTP server" mode that runs the engine entirely in-process. It wires the default
/// in-memory stores, the static renderer, and the engine behind a small facade.
/// </summary>
/// <remarks>G0 composition: static responses and exact matching. It grows with the roadmap.</remarks>
public sealed class MockifyrServer
{
    private readonly InMemoryStubStore _stubStore = new();
    private readonly StubEngine _engine;

    /// <summary>Creates a server with the default in-memory composition.</summary>
    /// <param name="webhookClient">
    /// Optional <see cref="HttpClient"/> for the webhook listener's outbound calls (the differential
    /// harness injects one; production uses the default).
    /// </param>
    /// <param name="bodyFiles">
    /// Optional store for <c>bodyFileName</c> response bodies. Absent, such a stub serves an empty
    /// body — the same outcome the import warning describes.
    /// </param>
    public MockifyrServer(HttpClient? webhookClient = null, IResponseBodyFiles? bodyFiles = null)
    {
        _engine = new StubEngine(
            _stubStore,
            new TemplatingResponseRenderer(bodyFiles: bodyFiles),
            new InMemoryScenarioStateStore(),
            new InMemoryRequestJournal(),
            serveEventListeners: [new WebhookServeEventListener(webhookClient, new WebhookTemplateRenderer())]);
    }

    /// <summary>Imports stub mapping JSON in the supported import dialect into the given tenant (defaults to the default tenant).</summary>
    public void ImportMappingJson(string json, TenantId? tenant = null)
    {
        var scope = tenant ?? TenantId.Default;
        foreach (var stub in MappingJsonReader.Read(json, scope))
        {
            _stubStore.Put(stub);
        }
    }

    /// <summary>Handles a request within the given tenant (defaults to the default tenant).</summary>
    /// <remarks>
    /// The pure engine only records directives; this facade applies the response <c>delay</c> (G4).
    /// The <c>fault</c> directive is a socket-level behavior emitted by the HTTP facade (G12), so it
    /// is carried on the response but not applied in-process here.
    /// </remarks>
    public StubResolution Handle(CanonicalRequest request, TenantId? tenant = null)
    {
        var resolution = _engine.Handle(tenant ?? TenantId.Default, request);

        if (resolution.Response?.Delay is { Milliseconds: > 0 } delay)
        {
            Thread.Sleep(delay.Milliseconds);
        }

        return resolution;
    }

    /// <summary>Counts journaled requests matching the pattern (verification — G6).</summary>
    public int CountRequestsMatching(RequestPattern pattern, TenantId? tenant = null) =>
        _engine.CountRequestsMatching(tenant ?? TenantId.Default, pattern);

    /// <summary>The journaled requests matching the pattern (verification — G6).</summary>
    public IReadOnlyList<CanonicalRequest> FindRequestsMatching(RequestPattern pattern, TenantId? tenant = null) =>
        _engine.FindRequestsMatching(tenant ?? TenantId.Default, pattern);

    /// <summary>The journaled requests that matched no stub (verification — G6).</summary>
    public IReadOnlyList<CanonicalRequest> FindUnmatchedRequests(TenantId? tenant = null) =>
        _engine.FindUnmatchedRequests(tenant ?? TenantId.Default);

    /// <summary>The stubs closest to an unmatched request, ranked by distance (near-miss — G6).</summary>
    public IReadOnlyList<NearMiss> FindNearMisses(CanonicalRequest request, TenantId? tenant = null) =>
        _engine.FindNearMisses(tenant ?? TenantId.Default, request);
}
