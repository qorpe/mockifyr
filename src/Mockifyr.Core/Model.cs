namespace Mockifyr.Core;

/// <summary>
/// Logical tenant identifier. Every stub, scenario state, and journal entry belongs to a
/// tenant; matching always runs within a tenant scope. See docs/decisions/0003.
/// </summary>
public readonly record struct TenantId(string Value)
{
    /// <summary>The default tenant used by single-tenant/library scenarios.</summary>
    public static readonly TenantId Default = new("default");

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A single part of a multipart request body.</summary>
public sealed record MultipartPart
{
    /// <summary>The part name (the <c>name</c> attribute of the content disposition).</summary>
    public required string Name { get; init; }

    /// <summary>Headers attached to this part.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The raw part body.</summary>
    public required byte[] Body { get; init; }
}

/// <summary>
/// The transport-agnostic representation of an incoming request. Facades translate their
/// transport into this; the engine never sees a raw HTTP/gRPC/GraphQL concept.
/// </summary>
public sealed record CanonicalRequest
{
    /// <summary>HTTP method (or protocol-equivalent), e.g. <c>GET</c>.</summary>
    public required string Method { get; init; }

    /// <summary>Full URL: path plus query string.</summary>
    public required string Url { get; init; }

    /// <summary>URL path without the query string.</summary>
    public required string Path { get; init; }

    /// <summary>Zero-indexed path segments.</summary>
    public required IReadOnlyList<string> PathSegments { get; init; }

    /// <summary>Named path variables extracted from a matched urlPathTemplate.</summary>
    public required IReadOnlyDictionary<string, string> PathVariables { get; init; }

    /// <summary>Query parameters (multi-valued).</summary>
    public required ILookup<string, string> Query { get; init; }

    /// <summary>Request headers (multi-valued).</summary>
    public required ILookup<string, string> Headers { get; init; }

    /// <summary>Request cookies.</summary>
    public required IReadOnlyDictionary<string, string> Cookies { get; init; }

    /// <summary>The raw request body.</summary>
    public required byte[] Body { get; init; }

    /// <summary>Multipart parts, when the body is multipart.</summary>
    public required IReadOnlyList<MultipartPart> Parts { get; init; }

    /// <summary>The originating client IP, when known.</summary>
    public string? ClientIp { get; init; }

    /// <summary>
    /// The request scheme (<c>http</c>/<c>https</c>), when the transport supplied it. Null when
    /// unknown. Drives <c>request.scheme</c> matching (multi-domain, G15c; verified by the
    /// differential suite).
    /// </summary>
    public string? Scheme { get; init; }

    /// <summary>
    /// The request host — the hostname from the <c>Host</c> header — when known. Drives
    /// <c>request.host</c> matching (multi-domain, G15c; verified by the differential suite).
    /// </summary>
    public string? Host { get; init; }

    /// <summary>
    /// The request port — the port from the <c>Host</c> header — when known. Drives
    /// <c>request.port</c> matching (multi-domain, G15c; verified by the differential suite).
    /// </summary>
    public int? Port { get; init; }
}

/// <summary>Kinds of low-level fault the facade can inject on behalf of the engine.</summary>
public enum FaultKind
{
    /// <summary>Return an empty response.</summary>
    EmptyResponse,

    /// <summary>Send OK headers followed by garbage, then close.</summary>
    MalformedResponseChunk,

    /// <summary>Send random data, then close.</summary>
    RandomDataThenClose,

    /// <summary>Reset the connection (SO_LINGER = 0).</summary>
    ConnectionResetByPeer,
}

/// <summary>Directive asking the facade to delay before responding.</summary>
public sealed record DelayDirective(int Milliseconds);

/// <summary>A random response delay drawn uniformly from <c>[LowerMs, UpperMs]</c> (G12b uniform
/// <c>delayDistribution</c>). Lognormal distributions are deferred (no reliable lower bound to test).</summary>
public sealed record DelayDistribution(int LowerMs, int UpperMs);

/// <summary>Directive asking the facade to inject a low-level fault.</summary>
public sealed record FaultDirective(FaultKind Kind);

/// <summary>Directive asking the facade to proxy the request to a target.</summary>
public sealed record ProxyDirective(string BaseUrl)
{
    /// <summary>Extra headers added to the forwarded request (the <c>additionalProxyRequestHeaders</c> field).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> AdditionalHeaders { get; init; } = [];

    /// <summary>A leading URL-path prefix stripped before forwarding (the <c>proxyUrlPrefixToRemove</c> field).</summary>
    public string? UrlPrefixToRemove { get; init; }
}

/// <summary>
/// The transport-agnostic response the engine produces. Delay/fault/proxy are directives the
/// facade applies; the engine performs no I/O. See ARCHITECTURE.md section 7.
/// </summary>
public sealed record CanonicalResponse
{
    /// <summary>HTTP status code.</summary>
    public required int Status { get; init; }

    /// <summary>Optional status/reason phrase.</summary>
    public string? StatusMessage { get; init; }

    /// <summary>Response headers (multi-valued).</summary>
    public required ILookup<string, string> Headers { get; init; }

    /// <summary>The response body.</summary>
    public required byte[] Body { get; init; }

    /// <summary>Optional delay directive.</summary>
    public DelayDirective? Delay { get; init; }

    /// <summary>Optional random (uniform) delay directive.</summary>
    public DelayDistribution? DelayDistribution { get; init; }

    /// <summary>Optional fault directive.</summary>
    public FaultDirective? Fault { get; init; }

    /// <summary>Optional proxy directive.</summary>
    public ProxyDirective? Proxy { get; init; }
}

/// <summary>Arbitrary key/value metadata attached to a stub (used by management and record/playback).</summary>
public sealed record StubMetadata(IReadOnlyDictionary<string, object?> Values);

/// <summary>Binds a stub to a scenario state machine.</summary>
public sealed record ScenarioBinding
{
    /// <summary>The scenario name.</summary>
    public required string ScenarioName { get; init; }

    /// <summary>The state the scenario must be in for this stub to be eligible.</summary>
    public string? RequiredState { get; init; }

    /// <summary>The state to transition to after this stub is served.</summary>
    public string? NewState { get; init; }
}

/// <summary>
/// The request-side matching definition of a stub: URL, method, and header/query/cookie/body
/// matchers. Its concrete shape grows with the matching roadmap (G1a onward).
/// </summary>
public sealed record RequestPattern
{
    /// <summary>Matcher for the URL/path.</summary>
    public IMatcher? Url { get; init; }

    /// <summary>
    /// Opt-in declaration that named body fields arrive encrypted (G20a, ADR 0012). When set, body
    /// matchers and response templating see a decrypted VIEW of the request; the recorded request
    /// stays verbatim. Null on every stub that does not declare it — the default path.
    /// </summary>
    public PayloadDecryptDirective? Decrypt { get; init; }

    /// <summary>
    /// The raw <c>urlPathTemplate</c> string (e.g. <c>/users/{id}</c>) when the stub matched by URI
    /// template, else null. Carried so response templating can expose named path variables as
    /// <c>{{request.path.&lt;name&gt;}}</c> (verified by the differential suite). The engine records it; extraction happens
    /// at render time.
    /// </summary>
    public string? UrlPathTemplate { get; init; }

    /// <summary>Matcher for the HTTP method.</summary>
    public IMatcher? Method { get; init; }

    /// <summary>Header matchers.</summary>
    public required IReadOnlyList<IMatcher> Headers { get; init; }

    /// <summary>Query parameter matchers.</summary>
    public required IReadOnlyList<IMatcher> Query { get; init; }

    /// <summary>Form-parameter matchers (the <c>formParameters</c> field over a form-encoded body).</summary>
    public IReadOnlyList<IMatcher> FormParameters { get; init; } = [];

    /// <summary>Cookie matchers.</summary>
    public required IReadOnlyList<IMatcher> Cookies { get; init; }

    /// <summary>Body matchers.</summary>
    public required IReadOnlyList<IMatcher> Body { get; init; }

    /// <summary>Custom request-level matchers contributed by extensions (G10, <c>customMatcher</c>).</summary>
    public IReadOnlyList<IMatcher> Custom { get; init; } = [];

    /// <summary>Matcher for the request scheme (the <c>request.scheme</c> field; multi-domain, G15c; verified by the differential suite).</summary>
    public IMatcher? Scheme { get; init; }

    /// <summary>Matcher for the request host (the <c>request.host</c> field; multi-domain, G15c; verified by the differential suite).</summary>
    public IMatcher? Host { get; init; }

    /// <summary>Matcher for the request port (the <c>request.port</c> field; multi-domain, G15c; verified by the differential suite).</summary>
    public IMatcher? Port { get; init; }
}

/// <summary>
/// The response-side definition of a stub. Its concrete shape grows with the response roadmap
/// (G2a onward).
/// </summary>
public sealed record ResponseDefinition
{
    /// <summary>Status code.</summary>
    public required int Status { get; init; }

    /// <summary>Optional status message.</summary>
    public string? StatusMessage { get; init; }

    /// <summary>Response headers.</summary>
    public required ILookup<string, string> Headers { get; init; }

    /// <summary>Inline response body, when present.</summary>
    public byte[]? Body { get; init; }

    /// <summary>Names of the response transformers to apply (e.g. <c>response-template</c>).</summary>
    public required IReadOnlyList<string> Transformers { get; init; }

    /// <summary>Optional response delay directive (applied by the facade, not the engine).</summary>
    public DelayDirective? Delay { get; init; }

    /// <summary>Optional random (uniform) delay directive (applied by the facade).</summary>
    public DelayDistribution? DelayDistribution { get; init; }

    /// <summary>Optional low-level fault directive (applied by the transport facade — G12).</summary>
    public FaultDirective? Fault { get; init; }

    /// <summary>Optional proxy directive: forward the request to an upstream and return its response.</summary>
    public ProxyDirective? Proxy { get; init; }

    /// <summary>Optional sandbox state directive (G19b — applied by the templating renderer, never the engine).</summary>
    public StateDirective? State { get; init; }
}

/// <summary>A single stub: a request pattern paired with a response definition.</summary>
public sealed record StubMapping
{
    /// <summary>Unique stub id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Owning tenant.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The request-side pattern.</summary>
    public required RequestPattern Request { get; init; }

    /// <summary>The response-side definition.</summary>
    public required ResponseDefinition Response { get; init; }

    /// <summary>Priority; lower wins. The default is 5 (verified by the differential suite).</summary>
    public int Priority { get; init; } = 5;

    /// <summary>Optional scenario binding.</summary>
    public ScenarioBinding? Scenario { get; init; }

    /// <summary>Optional metadata.</summary>
    public StubMetadata? Metadata { get; init; }

    /// <summary>
    /// Outbound webhooks fired after this stub serves a request (the <c>postServeActions</c> field).
    /// The engine only records the intent; the actual I/O is performed by an
    /// <see cref="IServeEventListener"/> at the facade edge. See docs/decisions/0001.
    /// </summary>
    public IReadOnlyList<WebhookDefinition> Webhooks { get; init; } = [];

    /// <summary>
    /// The raw imported JSON this stub was parsed from, kept verbatim so the admin API can return the
    /// full mapping (not just an id) for faithful display + edit round-trips. Inert data — the engine
    /// never reads it for matching. Null for stubs constructed directly (not via the JSON adapter).
    /// </summary>
    public string? Source { get; init; }
}

/// <summary>
/// A single outbound webhook (a <c>webhook</c> post-serve action): the method, target URL,
/// headers, and body to send after a stub matches. Templating of these fields arrives with G3b.
/// </summary>
public sealed record WebhookDefinition
{
    /// <summary>HTTP method of the outbound call.</summary>
    public required string Method { get; init; }

    /// <summary>Target URL.</summary>
    public required string Url { get; init; }

    /// <summary>Outbound headers (multi-valued, insertion order preserved).</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }

    /// <summary>Outbound body, when present.</summary>
    public byte[]? Body { get; init; }

    /// <summary>Fixed delay (ms) applied before firing the webhook (the <c>delay</c> field). 0 = none.</summary>
    public int DelayMilliseconds { get; init; }
}

/// <summary>
/// The outcome of evaluating a matcher: an exact match, or a distance for near-miss ranking.
/// Distance is carried from day one so near-miss (G6) needs no new computation.
/// </summary>
public readonly record struct MatchResult(bool IsExactMatch, double Distance)
{
    /// <summary>An exact match (distance 0).</summary>
    public static MatchResult Exact => new(true, 0d);

    /// <summary>A non-match with the given distance.</summary>
    public static MatchResult NoMatch(double distance) => new(false, distance);
}

/// <summary>The input handed to a matcher.</summary>
public sealed record MatchInput
{
    /// <summary>The request being evaluated.</summary>
    public required CanonicalRequest Request { get; init; }
}

/// <summary>A correlated sub-event recorded during serving (e.g. a webhook request/response).</summary>
public sealed record SubEvent(string Type, long TimeOffsetNanos, object? Data)
{
    /// <summary>The upstream-compatible sub-event type for an outbound webhook request.</summary>
    public const string WebhookRequestType = "WEBHOOK_REQUEST";

    /// <summary>The upstream-compatible sub-event type for the response a webhook target returned.</summary>
    public const string WebhookResponseType = "WEBHOOK_RESPONSE";

    /// <summary>The sub-event type for a failed delivery (render error or unreachable target).</summary>
    public const string ErrorType = "ERROR";
}

/// <summary>The rendered outbound webhook request as actually sent (the data of a WEBHOOK_REQUEST sub-event).</summary>
public sealed record WebhookRequestData(
    string Method, string Url, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[]? Body);

/// <summary>The response a webhook target returned (the data of a WEBHOOK_RESPONSE sub-event).</summary>
public sealed record WebhookResponseData(
    int Status, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[]? Body);

/// <summary>Why a webhook delivery failed (the data of an ERROR sub-event).</summary>
public sealed record WebhookErrorData(string Message);

/// <summary>The record of a single served request: what came in, what stub matched, what went out.</summary>
public sealed record ServeEvent
{
    // Sub-events arrive after the event is journaled (webhook delivery is fire-and-forget), so this
    // is the one mutable, lock-guarded corner of the otherwise immutable record.
    private readonly List<SubEvent> _subEvents = [];

    /// <summary>Unique event id.</summary>
    public required Guid Id { get; init; }

    /// <summary>Owning tenant.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The incoming request.</summary>
    public required CanonicalRequest Request { get; init; }

    /// <summary>The stub that matched, if any.</summary>
    public StubMapping? MatchedStub { get; init; }

    /// <summary>The response produced, if any.</summary>
    public CanonicalResponse? Response { get; init; }

    /// <summary>A snapshot of the correlated sub-events (e.g. webhook request/response).</summary>
    public IReadOnlyList<SubEvent> SubEvents
    {
        get { lock (_subEvents) { return [.. _subEvents]; } }
    }

    /// <summary>
    /// Appends a correlated sub-event. Called from the serve-event listeners at the edge (e.g. the
    /// webhook listener recording what it actually sent and what came back), which run concurrently
    /// with journal reads — hence the lock.
    /// </summary>
    public void AppendSubEvent(SubEvent subEvent)
    {
        lock (_subEvents) { _subEvents.Add(subEvent); }
    }

    /// <summary>
    /// When the event was recorded. Stamped at dispatch by the (already non-deterministic) serve path,
    /// never read by matching/templating — it only backs the journal's ordering and "time ago" display.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>Query criteria for the request journal.</summary>
public sealed record ServeEventQuery
{
    /// <summary>Only include events at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only include events that did not match a stub.</summary>
    public bool UnmatchedOnly { get; init; }

    /// <summary>Only include events matched by this stub.</summary>
    public Guid? MatchingStubId { get; init; }

    /// <summary>Optional maximum number of events to return.</summary>
    public int? Limit { get; init; }

    /// <summary>When set, resolve exactly this event by id (index lookup, no scan).</summary>
    public Guid? Id { get; init; }
}

/// <summary>
/// The data context for a single render. For responses this carries <see cref="Request"/>;
/// for webhooks the same renderer is used with <see cref="OriginalRequest"/> populated.
/// One renderer, two contexts. See ARCHITECTURE.md section 5.
/// </summary>
public sealed record RenderContext
{
    /// <summary>The current request (the <c>request</c> template model).</summary>
    public required CanonicalRequest Request { get; init; }

    /// <summary>
    /// The tenant the request is being served for. Required, not defaulted: environment keys (G17)
    /// resolve per tenant, and silently falling back to the default tenant would leak one tenant's
    /// values into another's response. Forgetting the scope must be a compile error.
    /// </summary>
    public required TenantId Tenant { get; init; }

    /// <summary>The originating request (the <c>originalRequest</c> model), for webhooks.</summary>
    public CanonicalRequest? OriginalRequest { get; init; }

    /// <summary>
    /// The matched stub's <c>urlPathTemplate</c> (e.g. <c>/users/{id}</c>), when it matched by URI
    /// template — used to expose <c>{{request.path.&lt;name&gt;}}</c> named path variables.
    /// </summary>
    public string? UrlPathTemplate { get; init; }
}
