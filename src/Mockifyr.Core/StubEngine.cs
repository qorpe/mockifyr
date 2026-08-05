namespace Mockifyr.Core;

/// <summary>
/// One attribute of a stub's request pattern, and whether the request satisfied it (#288).
/// </summary>
/// <remarks>
/// <see cref="Attribute"/> names the slot in the pattern — <c>method</c>, <c>url</c>,
/// <c>headers[0]</c>, <c>bodyPatterns[1]</c> — so a reader can point at the line in the stub that
/// disagreed instead of re-reading the whole thing. <see cref="Actual"/> is what the request carried
/// there; what the stub *expected* lives in the stub's own JSON, which the admin layer serves beside
/// this rather than making every matcher describe itself.
/// </remarks>
/// <param name="Attribute">The pattern slot, in the mapping JSON's own vocabulary.</param>
/// <param name="Matched">Whether this attribute was satisfied.</param>
/// <param name="Actual">What the request carried, or null when it carried nothing at all.</param>
public sealed record MatchAttribute(string Attribute, bool Matched, string? Actual);

/// <summary>A stub that did not match, with its distance, for near-miss diagnostics.</summary>
/// <remarks>
/// <see cref="Attributes"/> is empty unless the caller asked for the detailed form (#288) — computing
/// it re-runs every matcher and extracts request values, which is the right cost for a debugging
/// question asked once and the wrong one for the serve path.
/// </remarks>
public sealed record NearMiss(StubMapping Stub, double Distance)
{
    /// <summary>Per-attribute verdicts, in pattern order; empty when not requested.</summary>
    public IReadOnlyList<MatchAttribute> Attributes { get; init; } = [];
}

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
        var winner = FindMatch(tenant, request);
        if (winner is not null)
        {
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

    /// <summary>
    /// The stub that would answer this request, or null when none would — <b>without serving it</b>:
    /// nothing is journaled, no scenario advances, no listener fires, no response is rendered.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="Handle"/> rather than reimplemented so that anything asking "what
    /// would this host answer" — drift detection against a recording (#287) among them — uses exactly
    /// the rules the host serves by: candidate narrowing, scenario eligibility, the signature gate,
    /// the decrypted view, priority, and recency. A diagnostic based on subtly different matching
    /// would be a confident report about a host that does not exist.
    /// </remarks>
    public StubMapping? FindMatch(TenantId tenant, CanonicalRequest request)
    {
        var input = new MatchInput { Request = request };
        // ISOLATION: only this tenant's stubs are visible. The store may narrow these to the ones that
        // could match (#265); it never decides the match, and a store without an index returns
        // everything, so behaviour is identical either way.
        var stubs = _stubStore.GetCandidates(tenant, request);

        var exact = new List<(StubMapping Stub, int Index)>();
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

            if (Evaluate(stub.Request, stubInput).IsExactMatch)
            {
                exact.Add((stub, i));
            }
        }

        // Lower priority wins; ties broken by recency (last added wins).
        return exact.Count == 0
            ? null
            : exact.OrderBy(x => x.Stub.Priority).ThenByDescending(x => x.Index).First().Stub;
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
    public IReadOnlyList<NearMiss> FindNearMisses(TenantId tenant, CanonicalRequest request) =>
        FindNearMisses(tenant, request, detailed: false);

    /// <summary>
    /// The same ranking, optionally with a per-attribute verdict for each candidate (#288) — which
    /// attribute of the pattern the request failed, and what it carried there.
    /// </summary>
    /// <remarks>
    /// The detail is computed only for the candidates that survive the ranking, so asking for it costs
    /// a second pass over a handful of stubs rather than over the whole store.
    /// </remarks>
    public IReadOnlyList<NearMiss> FindNearMisses(TenantId tenant, CanonicalRequest request, bool detailed)
    {
        var input = new MatchInput { Request = request };
        var ranked = _stubStore.GetStubs(tenant)
            .Select(stub => new NearMiss(stub, Evaluate(stub.Request, input).Distance))
            .OrderBy(nearMiss => nearMiss.Distance)
            .Take(3)
            .ToList();

        return detailed
            ? [.. ranked.Select(near => near with { Attributes = Explain(near.Stub.Request, input) })]
            : ranked;
    }

    /// <summary>
    /// Re-runs each matcher on its own so a failure can be attributed to the attribute that produced
    /// it. Matching itself only ever needs the sum, which is why this is a separate walk rather than
    /// something the serve path carries.
    /// </summary>
    private static IReadOnlyList<MatchAttribute> Explain(RequestPattern pattern, MatchInput input) =>
    [
        .. EnumerateNamedMatchers(pattern).Select(entry => new MatchAttribute(
            entry.Attribute,
            entry.Matcher.Match(input).IsExactMatch,
            ActualFor(entry.Attribute, entry.Matcher, input.Request))),
    ];

    /// <summary>
    /// What the request carried at the named attribute. Only the slots whose value is unambiguous are
    /// reported: a header matcher names its header, but a body pattern's subject is the whole body,
    /// which the caller already has in the journal entry.
    /// </summary>
    private static string? ActualFor(string attribute, IMatcher matcher, CanonicalRequest request)
    {
        if (matcher is INamedTargetMatcher named && !UrlSlots.Contains(attribute))
        {
            // The value the request actually carried under that name — the single most useful line in a
            // near-miss report, and the reason naming the target was worth an interface.
            // Headers and query parameters are multi-valued, cookies are not; a repeated header is
            // joined rather than truncated, because "which of the two did you mean" is exactly the
            // question a near miss is being asked.
            if (attribute.StartsWith("headers", StringComparison.Ordinal))
            {
                return request.Headers.Contains(named.TargetName)
                    ? string.Join(", ", request.Headers[named.TargetName]) : null;
            }

            if (attribute.StartsWith("queryParameters", StringComparison.Ordinal))
            {
                return request.Query.Contains(named.TargetName)
                    ? string.Join(", ", request.Query[named.TargetName]) : null;
            }

            return attribute.StartsWith("cookies", StringComparison.Ordinal)
                && request.Cookies.TryGetValue(named.TargetName, out var cookie) ? cookie : null;
        }

        return attribute switch
        {
            "method" => request.Method,
            // A urlPath* matcher is judging the path, so echoing the query string back would invite the
            // reader to look for a difference that is not being compared.
            "urlPath" or "urlPathPattern" or "urlPathTemplate" => request.Path,
            "url" or "urlPattern" => request.Url,
            "scheme" => request.Scheme,
            _ => null,
        };
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

    private static IEnumerable<IMatcher> EnumerateMatchers(RequestPattern pattern) =>
        EnumerateNamedMatchers(pattern).Select(entry => entry.Matcher);

    /// <summary>
    /// Every matcher in the pattern, paired with the mapping-JSON slot it came from. The names are the
    /// dialect's own (<c>bodyPatterns[0]</c>, not "body matcher 1") so a diagnostic points at something
    /// the reader can find in their stub.
    /// </summary>
    private static IEnumerable<(string Attribute, IMatcher Matcher)> EnumerateNamedMatchers(RequestPattern pattern)
    {
        if (pattern.Url is not null)
        {
            // The dialect has five spellings for the URL slot; report the one the stub actually used, so
            // a reader searching their mapping for "urlPath" finds the line rather than wondering why we
            // said "url".
            yield return (
                pattern.Url is INamedTargetMatcher named ? named.TargetName : "url",
                pattern.Url);
        }

        if (pattern.Method is not null)
        {
            yield return ("method", pattern.Method);
        }

        if (pattern.Scheme is not null)
        {
            yield return ("scheme", pattern.Scheme);
        }

        if (pattern.Host is not null)
        {
            yield return ("host", pattern.Host);
        }

        if (pattern.Port is not null)
        {
            yield return ("port", pattern.Port);
        }

        foreach (var (matcher, index) in pattern.Headers.Select((m, i) => (m, i)))
        {
            yield return (Slot("headers", matcher, index), matcher);
        }

        foreach (var (matcher, index) in pattern.Query.Select((m, i) => (m, i)))
        {
            yield return (Slot("queryParameters", matcher, index), matcher);
        }

        foreach (var (matcher, index) in pattern.FormParameters.Select((m, i) => (m, i)))
        {
            yield return (Slot("formParameters", matcher, index), matcher);
        }

        foreach (var (matcher, index) in pattern.Cookies.Select((m, i) => (m, i)))
        {
            yield return (Slot("cookies", matcher, index), matcher);
        }

        foreach (var (matcher, index) in pattern.Body.Select((m, i) => (m, i)))
        {
            yield return ($"bodyPatterns[{index}]", matcher);
        }

        foreach (var (matcher, index) in pattern.Custom.Select((m, i) => (m, i)))
        {
            yield return ($"customMatcher[{index}]", matcher);
        }
    }

    /// <summary>
    /// Names a slot by the part of the request it addresses when the matcher can say
    /// (<c>headers['X-Api-Key']</c>), and by position when it cannot — a custom matcher (G10) written
    /// before <see cref="INamedTargetMatcher"/> existed still reports somewhere findable.
    /// </summary>
    private static readonly string[] UrlSlots =
        ["url", "urlPath", "urlPattern", "urlPathPattern", "urlPathTemplate"];

    private static string Slot(string collection, IMatcher matcher, int index) =>
        matcher is INamedTargetMatcher named
            ? $"{collection}['{named.TargetName}']"
            : $"{collection}[{index}]";

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
