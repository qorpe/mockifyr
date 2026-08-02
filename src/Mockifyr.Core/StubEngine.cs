namespace Mockifyr.Core;

/// <summary>A stub that did not match, with its distance, for near-miss diagnostics.</summary>
public sealed record NearMiss(StubMapping Stub, double Distance);

/// <summary>The outcome of handling a request: a matched response, or the ranked near-misses.</summary>
public sealed record StubResolution
{
    /// <summary>Whether a stub matched.</summary>
    public required bool Matched { get; init; }

    /// <summary>The response to serve, when matched.</summary>
    public CanonicalResponse? Response { get; init; }

    /// <summary>The stub that matched, when matched.</summary>
    public StubMapping? MatchedStub { get; init; }

    /// <summary>The closest non-matching stubs, when not matched.</summary>
    public required IReadOnlyList<NearMiss> NearMisses { get; init; }
}

/// <summary>
/// The transport-agnostic core coordinator. It owns no matching or templating logic itself;
/// it orchestrates the contracts. It performs no I/O and is fully deterministic, which is the
/// precondition for differential testing. See ARCHITECTURE.md sections 4-5.
/// </summary>
public sealed class StubEngine
{
    private readonly IStubStore _stubStore;
    private readonly IResponseRenderer _renderer;
    private readonly IScenarioStateStore _scenarioStore;
    private readonly IRequestJournal _journal;
    private readonly IReadOnlyList<IServeEventListener> _serveEventListeners;
    private readonly IReadOnlyList<IResponseTransformer> _responseTransformers;
    private readonly PayloadDecryptionView _decryption;
    private readonly PayloadProtectionApplier _protection;
    private readonly SignatureGate _signatures;
    private readonly ResponseSigningApplier _signing;

    /// <summary>Creates the engine with its collaborators.</summary>
    public StubEngine(
        IStubStore stubStore,
        IResponseRenderer renderer,
        IScenarioStateStore scenarioStore,
        IRequestJournal journal,
        IEnumerable<IServeEventListener> serveEventListeners,
        IEnumerable<IResponseTransformer>? responseTransformers = null,
        IEnumerable<IPayloadDecryptor>? payloadDecryptors = null,
        IEnumerable<IPayloadProtector>? payloadProtectors = null,
        IEnumerable<ISignatureVerifier>? signatureVerifiers = null,
        IEnumerable<IResponseSigner>? responseSigners = null)
    {
        _stubStore = stubStore;
        _renderer = renderer;
        _scenarioStore = scenarioStore;
        _journal = journal;
        _serveEventListeners = [.. serveEventListeners];
        _responseTransformers = responseTransformers is null ? [] : [.. responseTransformers];
        _decryption = new PayloadDecryptionView(payloadDecryptors ?? []);
        _protection = new PayloadProtectionApplier(payloadProtectors ?? []);
        _signatures = new SignatureGate(signatureVerifiers ?? []);
        _signing = new ResponseSigningApplier(responseSigners ?? []);
    }

    /// <summary>
    /// Resolves a request within a tenant scope to a response (or near-misses). Matching always
    /// runs inside the given <paramref name="tenant"/>; a tenant can never see another's stubs.
    /// </summary>
    public StubResolution Handle(TenantId tenant, CanonicalRequest request)
    {
        var input = new MatchInput { Request = request };
        // ISOLATION: only this tenant's stubs are visible. The store may narrow these to the ones that
        // could match (#265); it never decides the match, and a store without an index returns
        // everything, so behaviour is identical either way.
        var stubs = _stubStore.GetCandidates(tenant, request);

        var scored = new List<(StubMapping Stub, MatchResult Result, int Index)>(stubs.Count);
        for (var i = 0; i < stubs.Count; i++)
        {
            var stub = stubs[i];
            if (!IsEligible(tenant, stub))
            {
                continue;
            }

            // Encrypted-payload stubs (G20a) match against a decrypted view; every other stub keeps
            // the very same MatchInput instance, so the default path is untouched.
            // Signature requirement (G20c): an unsigned or badly signed request cannot match a stub
            // that demands a signature. It fails closed — including when no verifier is registered.
            if (!_signatures.Satisfied(request, stub.Request.Signature))
            {
                continue;
            }

            var stubInput = stub.Request.Decrypt is null || _decryption.IsEmpty
                ? input
                : new MatchInput { Request = _decryption.For(request, stub.Request.Decrypt) };
            scored.Add((stub, Evaluate(stub.Request, stubInput), i));
        }

        var exact = scored.Where(x => x.Result.IsExactMatch).ToList();
        if (exact.Count > 0)
        {
            // Lower priority wins; ties broken by recency (last added wins).
            var winner = exact.OrderBy(x => x.Stub.Priority).ThenByDescending(x => x.Index).First().Stub;
            // Templating sees the same decrypted view the winner matched against (G20a), so
            // {{jsonPath request.body …}} can correlate with what the client actually sent.
            var renderRequest = _decryption.For(request, winner.Request.Decrypt);
            var response = _renderer.Render(
                winner.Response,
                new RenderContext
                {
                    Request = renderRequest,
                    Tenant = tenant,
                    UrlPathTemplate = winner.Request.UrlPathTemplate,
                });
            response = ApplyResponseTransformers(response, tenant, request, winner);

            // Payload protection (G20b) runs LAST — after templating and after every transformer, so
            // what gets encrypted is exactly what would otherwise have gone on the wire. The serve
            // event records the protected response, because that IS what the client received.
            response = _protection.For(response, winner.Response.Protect);

            // Signing comes after protection (G20c), so the digest covers the bytes the client will
            // actually receive and verify — signing the plaintext would be a signature over
            // something that never went on the wire.
            response = _signing.For(response, winner.Response.Sign);

            ApplyTransition(tenant, winner);
            DispatchServeEvent(tenant, request, winner, response);

            return new StubResolution { Matched = true, Response = response, MatchedStub = winner, NearMisses = [] };
        }

        // Nothing matched, so this is the diagnostic path — and the near-miss answer must be the
        // closest stubs in the WHOLE tenant, not merely the closest among the candidates the index
        // offered. A request that hit no bucket would otherwise report no near misses at all, which
        // is precisely when an operator most needs them. The full scan costs what matching always
        // cost; it just no longer happens on the path that succeeds.
        var nearMisses = FindNearMisses(tenant, request);

        DispatchServeEvent(tenant, request, matchedStub: null, response: null);

        return new StubResolution { Matched = false, NearMisses = nearMisses };
    }

    private bool IsEligible(TenantId tenant, StubMapping stub)
    {
        if (stub.Scenario is not { } scenario)
        {
            return true;
        }

        if (scenario.RequiredState is null)
        {
            return true;
        }

        return string.Equals(
            _scenarioStore.GetState(tenant, scenario.ScenarioName),
            scenario.RequiredState,
            StringComparison.Ordinal);
    }

    private void ApplyTransition(TenantId tenant, StubMapping stub)
    {
        if (stub.Scenario is { NewState: { } newState } scenario)
        {
            _scenarioStore.SetState(tenant, scenario.ScenarioName, newState);
        }
    }

    private void DispatchServeEvent(TenantId tenant, CanonicalRequest request, StubMapping? matchedStub, CanonicalResponse? response)
    {
        var serveEvent = new ServeEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Request = request,
            MatchedStub = matchedStub,
            Response = response,
            Timestamp = DateTimeOffset.UtcNow,
        };

        _journal.Record(serveEvent);

        foreach (var listener in _serveEventListeners)
        {
            // Fire-and-forget: outbound I/O (e.g. webhooks) must not block serving. Full
            // correlation and error handling arrive at G3.
            _ = listener.OnServeEventAsync(serveEvent, CancellationToken.None);
        }
    }

    // --- Verification / diagnostics (G6): read-only queries over the journal, reusing matching. ---

    /// <summary>Counts journaled requests matching the pattern; backs the <c>requests/count</c> admin endpoint (G6, verified by the differential suite).</summary>
    public int CountRequestsMatching(TenantId tenant, RequestPattern pattern) =>
        RequestsMatching(tenant, pattern).Count;

    /// <summary>The journaled requests matching the pattern; backs the <c>requests/find</c> admin endpoint (G6, verified by the differential suite).</summary>
    public IReadOnlyList<CanonicalRequest> FindRequestsMatching(TenantId tenant, RequestPattern pattern) =>
        [.. RequestsMatching(tenant, pattern)];

    /// <summary>The journaled requests that matched no stub; backs the <c>requests/unmatched</c> admin endpoint (G6, verified by the differential suite).</summary>
    public IReadOnlyList<CanonicalRequest> FindUnmatchedRequests(TenantId tenant) =>
        [.. _journal.Query(tenant, new ServeEventQuery { UnmatchedOnly = true }).Select(e => e.Request)];

    /// <summary>The journaled serve events for a tenant (the <c>requests</c> log), newest last (G6, verified by the differential suite).</summary>
    public IReadOnlyList<ServeEvent> GetServeEvents(TenantId tenant, ServeEventQuery query) =>
        _journal.Query(tenant, query);

    /// <summary>
    /// The stubs closest to an unmatched request, ranked by ascending match distance — the near-miss
    /// diagnostic. The distance is the same one matching computes, so no extra machinery is needed.
    /// </summary>
    public IReadOnlyList<NearMiss> FindNearMisses(TenantId tenant, CanonicalRequest request)
    {
        var input = new MatchInput { Request = request };
        return
        [
            .. _stubStore.GetStubs(tenant)
                .Select(stub => new NearMiss(stub, Evaluate(stub.Request, input).Distance))
                .OrderBy(nearMiss => nearMiss.Distance)
                .Take(3),
        ];
    }

    private List<CanonicalRequest> RequestsMatching(TenantId tenant, RequestPattern pattern) =>
    [
        .. _journal.Query(tenant, new ServeEventQuery())
            .Where(e => Evaluate(pattern, new MatchInput { Request = e.Request }).IsExactMatch)
            .Select(e => e.Request),
    ];

    private static MatchResult Evaluate(RequestPattern pattern, MatchInput input)
    {
        var exact = true;
        var distance = 0d;

        foreach (var matcher in EnumerateMatchers(pattern))
        {
            var result = matcher.Match(input);
            if (!result.IsExactMatch)
            {
                exact = false;
            }

            distance += result.Distance;
        }

        return exact ? MatchResult.Exact : MatchResult.NoMatch(distance);
    }

    private static IEnumerable<IMatcher> EnumerateMatchers(RequestPattern pattern)
    {
        if (pattern.Url is not null)
        {
            yield return pattern.Url;
        }

        if (pattern.Method is not null)
        {
            yield return pattern.Method;
        }

        if (pattern.Scheme is not null)
        {
            yield return pattern.Scheme;
        }

        if (pattern.Host is not null)
        {
            yield return pattern.Host;
        }

        if (pattern.Port is not null)
        {
            yield return pattern.Port;
        }

        foreach (var matcher in pattern.Headers)
        {
            yield return matcher;
        }

        foreach (var matcher in pattern.Query)
        {
            yield return matcher;
        }

        foreach (var matcher in pattern.FormParameters)
        {
            yield return matcher;
        }

        foreach (var matcher in pattern.Cookies)
        {
            yield return matcher;
        }

        foreach (var matcher in pattern.Body)
        {
            yield return matcher;
        }

        foreach (var matcher in pattern.Custom)
        {
            yield return matcher;
        }
    }

    // Applies the registered response transformers (G10): a transformer runs when it applies globally
    // or the stub named it in its `transformers`. The built-in response-template runs in the renderer.
    private CanonicalResponse ApplyResponseTransformers(
        CanonicalResponse response, TenantId tenant, CanonicalRequest request, StubMapping stub)
    {
        if (_responseTransformers.Count == 0)
        {
            return response;
        }

        var serveEvent = new ServeEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Request = request,
            MatchedStub = stub,
            Response = response,
        };

        foreach (var transformer in _responseTransformers)
        {
            if (transformer.ApplyGlobally || stub.Response.Transformers.Contains(transformer.Name))
            {
                response = transformer.Transform(response, serveEvent);
            }
        }

        return response;
    }
}
