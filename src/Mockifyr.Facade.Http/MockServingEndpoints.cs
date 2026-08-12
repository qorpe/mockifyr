using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Core;
using Mockifyr.Outbound;

namespace Mockifyr.Facade.Http;

/// <summary>
/// The mock-serving HTTP facade (G12): a fallback endpoint that turns every non-admin request into a
/// <see cref="CanonicalRequest"/>, resolves it through the (pure) <see cref="StubEngine"/>, and writes
/// the response over the wire — status, the custom reason phrase (<c>statusMessage</c>), declared
/// headers, and body — applying the response <c>delay</c> directive. Fault emission is G12b. Tenant
/// resolution reads an optional <c>X-Mockifyr-Tenant</c> header, else the default tenant.
/// </summary>
public static class MockServingEndpoints
{
    private const string TenantHeader = "X-Mockifyr-Tenant";

    // Recomputed by Kestrel; setting them explicitly would conflict with the framed response.
    private static readonly HashSet<string> SkipHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Content-Length", "Transfer-Encoding", "Connection" };

    public static IEndpointRouteBuilder MapMockServing(this IEndpointRouteBuilder endpoints)
    {
        // An explicit {*path} pattern, NOT the MapFallback default: the default is {*path:nonfile},
        // whose :nonfile constraint silently refuses any path whose last segment looks like a file —
        // so a stub on /report.json (or the Twilio profile's /Messages.json) 404'd without ever
        // reaching the engine. WireMock serves dotted paths; discovered by G18d, covered by test.
        // Static dashboard assets are unaffected: the static-files middleware runs before routing.
        endpoints.MapFallback("{*path}", ServeAsync);
        return endpoints;
    }

    /// <summary>The presented sandbox credential: <c>X-Api-Key</c> (any casing) or a Bearer token.</summary>
    private static string? PresentedApiKey(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var header) &&
            header.FirstOrDefault() is { Length: > 0 } apiKey)
        {
            return apiKey;
        }

        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        return authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>Where the key id waits so the usage record made at the end knows whose request this was.</summary>
    private const string UsageKeyItem = "mockifyr.usage.key";

    /// <summary>Where a match is noted, since the final status alone cannot tell 404-by-stub from no-match.</summary>
    private const string UsageMatchedItem = "mockifyr.usage.matched";

    /// <summary>
    /// Serves one request and records what it did for the presenting key (#356).
    /// </summary>
    /// <remarks>
    /// Wrapped rather than instrumented at each return: this method has a dozen exits — refusals,
    /// faults, proxies, degradation — and one of them would eventually be added without its counter.
    /// A <c>finally</c> cannot be forgotten by the next person to add an exit.
    /// </remarks>
    private static async Task ServeAsync(HttpContext context)
    {
        var tenantForUsage = TenantId.Default;
        try
        {
            tenantForUsage = await ServeCoreAsync(context);
        }
        finally
        {
            if (context.Items.TryGetValue(UsageKeyItem, out var keyId) && keyId is string presented)
            {
                context.RequestServices.GetRequiredService<IUsageRecorder>().Record(
                    tenantForUsage,
                    presented,
                    context.Request.Path.Value ?? "/",
                    OutcomeOf(context),
                    DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary>Classifies the finished request. Status first, since every refusal has its own code.</summary>
    private static UsageOutcome OutcomeOf(HttpContext context) => context.Response.StatusCode switch
    {
        StatusCodes.Status401Unauthorized => UsageOutcome.Unauthorized,
        StatusCodes.Status403Forbidden => UsageOutcome.Forbidden,
        StatusCodes.Status429TooManyRequests => UsageOutcome.RateLimited,
        _ => context.Items.TryGetValue(UsageMatchedItem, out var matched) && matched is true
            ? UsageOutcome.Matched
            // A stub is free to answer 404, so "did anything match" is recorded rather than inferred:
            // a modelled 404 and a call the sandbox does not model at all are opposite findings.
            : UsageOutcome.Unmatched,
    };

    private static async Task<TenantId> ServeCoreAsync(HttpContext context)
    {
        var request = await BuildRequestAsync(context);

        var engine = context.RequestServices.GetRequiredService<StubEngine>();

        // Sandbox access (G19d, ADR 0011): with --sandbox-auth, a presented API key resolves the
        // tenant AHEAD of the host/header chain — an extension of the ADR 0003 chain, not a parallel
        // mechanism. No credentials presented → the legacy chain below; a presented-but-invalid key
        // is an honest 401, never a silent fall-through to another tenant. gRPC/GraphQL/WS inherit
        // this for free (same facade); SMTP keeps AUTH-as-tenant (ADR 0009).
        TenantId? keyTenant = null;
        var sandbox = context.RequestServices.GetRequiredService<SandboxAuthOptions>();
        if (sandbox.Enabled && PresentedApiKey(context) is { } presented)
        {
            var keys = context.RequestServices.GetRequiredService<IApiKeyStore>();
            var key = keys.GetAll().FirstOrDefault(k => ApiKeyMaterial.Verify(presented, k.Salt, k.Hash));
            if (key is null)
            {
                // An unknown token is told nothing: anything more would answer whether a guess was a
                // real key. Nor is it recorded — a stranger must not be able to grow this host's
                // memory by presenting tokens.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return TenantId.Default;
            }

            // From here the request belongs to a known consumer, so it is counted whatever happens to
            // it next — including the refusals below, which are the most useful thing usage reports.
            context.Items[UsageKeyItem] = key.Id;

            // Expiry and revocation say which they are (#355). A partner who has proved possession of a
            // real credential and one who mistyped a token both got the same bare 401, and the two need
            // to do completely different things about it.
            var status = key.StatusAt(DateTimeOffset.UtcNow);
            if (status is not ApiKeyStatus.Active)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(status == ApiKeyStatus.Expired
                    ? """{"error":"ApiKey.Expired","message":"This sandbox key has expired — ask for a new one."}"""
                    : """{"error":"ApiKey.Revoked","message":"This sandbox key has been revoked."}""");
                return key.Tenant;
            }

            // A read-only key may use safe methods only. The rule is the method, not the effect: a stub
            // whose GET mutates state through the `state` directive is not stopped by this, and saying
            // so is better than a rule that needs a response template read to predict.
            if (key.Scope == ApiKeyScope.ReadOnly
                && !(HttpMethods.IsGet(context.Request.Method)
                    || HttpMethods.IsHead(context.Request.Method)
                    || HttpMethods.IsOptions(context.Request.Method)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"ApiKey.ReadOnly","message":"This sandbox key may only read."}""");
                return key.Tenant;
            }

            // Fixed-window quota (race-free; ADR 0011 addendum) with honest rate headers; the
            // refusal is a realistic 429 with Retry-After.
            // Every window the key is subject to (#354): its own hourly quota and any host-wide burst
            // ceiling, counted through a shared counter so two replicas enforce the sum rather than
            // twice the number the key says.
            var decision = RateLimits.Count(
                key.Id,
                RateLimits.For(key.QuotaPerHour, context.RequestServices.GetService<RateWindow>()),
                context.RequestServices.GetRequiredService<IRateCounter>(),
                DateTimeOffset.UtcNow);
            if (decision.Limit > 0)
            {
                context.Response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString();
                context.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString();
                context.Response.Headers["X-RateLimit-Reset"] = decision.ResetAt.ToUnixTimeSeconds().ToString();
            }

            if (!decision.Allowed)
            {
                context.Response.Headers.RetryAfter =
                    Math.Max(1, (int)(decision.ResetAt - DateTimeOffset.UtcNow).TotalSeconds).ToString();
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return key.Tenant;
            }

            keyTenant = key.Tenant;
        }

        var tenant = keyTenant
            ?? (context.Request.Headers.TryGetValue(TenantHeader, out var t) && !string.IsNullOrEmpty(t)
                ? new TenantId(t!)
                : TenantId.Default);

        // Idempotent replay (#358): a retried write carrying the same Idempotency-Key gets the first
        // response back instead of running the directive again. Every payment API this sandbox stands
        // in for behaves this way, and a partner's client library retries automatically — so without
        // it a timeout creates a second payment, and the bug looks like theirs.
        var idempotency = context.RequestServices.GetService<IdempotencyOptions>();
        var presentedKey = context.Request.Headers[Idempotency.HeaderName].FirstOrDefault();
        if (idempotency is not null
            && Idempotency.IsWellFormed(presentedKey)
            && Idempotency.AppliesTo(context.Request.Method)
            && TenantIdempotency.EnabledFor(
                context.RequestServices.GetService<ITenantStore>()?.Get(tenant), idempotency.Enabled))
        {
            var store = context.RequestServices.GetRequiredService<IIdempotencyStore>();
            var fingerprint = Idempotency.Fingerprint(
                context.Request.Method, context.Request.Path.Value ?? "/", context.Request.QueryString.Value ?? string.Empty,
                request.Body);
            var stored = store.Get(tenant, presentedKey!, DateTimeOffset.UtcNow);

            switch (Idempotency.Decide(stored, fingerprint))
            {
                case IdempotencyOutcome.Replay:
                    await ReplayAsync(context, tenant, request, stored!);
                    return tenant;

                case IdempotencyOutcome.Conflict:
                    // Refused rather than answered: reusing a key for a different request would hand a
                    // caller somebody else's result, which is worse than any error.
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"error":"Idempotency.KeyReused","message":"This Idempotency-Key was already used with a different request."}""");
                    return tenant;

                default:
                    await CaptureAsync(context, tenant, request, presentedKey!, fingerprint, store);
                    return tenant;
            }
        }

        return await ServeRestAsync(context, tenant, request);
    }

    /// <summary>
    /// Everything after the tenant is known: suspension, recording, matching and writing the response.
    /// </summary>
    /// <remarks>
    /// Split out for idempotent capture (#358), which needs to run exactly this and keep the bytes.
    /// </remarks>
    private static async Task<TenantId> ServeRestAsync(HttpContext context, TenantId tenant, CanonicalRequest request)
    {
        var engine = context.RequestServices.GetRequiredService<StubEngine>();

        // A suspended tenant is refused at the door (#357): the sandbox is still there and nothing has
        // been deleted, which is the entire reason suspension exists. 403 rather than 401 — the
        // credential is fine and the account is not — and the body says suspended, because a partner
        // told "unauthorised" will spend the afternoon re-checking a key with nothing wrong with it.
        if (context.RequestServices.GetService<ITenantStore>()?.Get(tenant) is { Status: TenantStatus.Suspended })
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":"Tenant.Suspended","message":"This sandbox tenant is suspended. Contact the operator."}""");
            return tenant;
        }

        // Record mode (G12d): while THIS TENANT has a session live, its requests are proxied to that
        // tenant's target, a stub is generated from the exchange and captured, and the upstream's
        // response is returned to the caller — Mockifyr's record-through-proxy behavior (verified by
        // the differential suite).
        //
        // Deliberately after tenant resolution: recording follows the same chain everything else does
        // (API key, then header, then default). Checking it earlier — as this did while the session
        // was global — meant one team's recording proxied every other tenant's traffic to their
        // upstream.
        var recording = context.RequestServices.GetRequiredService<RecordingSession>();
        if (recording.TargetBaseUrl(tenant) is { } target)
        {
            var recorder = context.RequestServices.GetRequiredService<StubRecorder>();
            var exchange = await recorder.RecordAsync(target, request, context.RequestAborted);
            recording.Record(tenant, request, exchange.StubResponse);
            await WriteUpstreamAsync(context, exchange.CapturedResponse);
            return tenant;
        }

        var resolution = engine.Handle(tenant, request);

        if (resolution.Response is not { } response)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return tenant;
        }

        // Noted here rather than inferred from the status: a stub is free to answer 404, and a
        // modelled 404 and a call the sandbox does not model at all are opposite findings (#356).
        context.Items[UsageMatchedItem] = true;

        // Degradation profile (#289): what the whole dependency is doing today, on top of whatever this
        // stub declares. Asked for once per request so the ordinal advances exactly once — the seeded
        // sequence is only reproducible if every served request takes exactly one number.
        var degradation = context.RequestServices.GetService<IDegradationResolver>()?.Next(tenant)
            ?? DegradationDecision.None;

        if (degradation.DelayMs > 0)
        {
            await Task.Delay(degradation.DelayMs);
        }

        if (degradation.Fault is { } degradedFault)
        {
            // A dependency that resets the connection does not first politely explain itself, so this
            // outranks both the profile's error status and the stub's own response.
            await EmitFaultAsync(context, new FaultDirective(degradedFault));
            return tenant;
        }

        if (degradation.ErrorStatus is { } degradedStatus)
        {
            context.Response.StatusCode = degradedStatus;
            return tenant;
        }

        if (response.Delay is { Milliseconds: > 0 } delay)
        {
            await Task.Delay(delay.Milliseconds);
        }

        if (response.DelayDistribution is { } distribution && distribution.UpperMs > distribution.LowerMs)
        {
            await Task.Delay(Random.Shared.Next(distribution.LowerMs, distribution.UpperMs + 1));
        }

        // Fault injection (G12b): a low-level fault breaks the connection, so the client sees a failed
        // request rather than a valid response — all four fault kinds surface to an HTTP client as a
        // connection error (verified by the differential suite).
        if (response.Fault is { } fault)
        {
            await EmitFaultAsync(context, fault);
            return tenant;
        }

        // Proxy directive (G12d): forward the matched request to the upstream over HTTP and stream its
        // response back verbatim — closing the wire gap left by G8 (proxying was validated in-process).
        if (response.Proxy is { } proxy)
        {
            var responder = context.RequestServices.GetRequiredService<ProxyResponder>();
            try
            {
                var upstream = await responder.ProxyAsync(proxy, request, context.RequestAborted);
                await WriteUpstreamAsync(context, upstream);
            }
            catch (ProxyDeliveryException failure) when (failure.ContainerDiagnosis || failure.Refused)
            {
                // The container-localhost trap (#176): unlike a callback, a proxy has no journal, so its
                // failure is a live response. Rather than the opaque 500 an unhandled exception would
                // produce, answer 502 Bad Gateway with the cause — a proxy that cannot reach upstream is
                // exactly what 502 means. An allowlist refusal (#349) takes the same answer for the same
                // reason: it is the one proxy outcome we can explain completely, and propagating it as
                // an unhandled exception would hand back an opaque 500 instead. Other failures are left
                // to propagate unchanged.
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(failure.Message, context.RequestAborted);
            }
            return tenant;
        }

        context.Response.StatusCode = response.Status;
        if (!string.IsNullOrEmpty(response.StatusMessage))
        {
            // The custom reason phrase (statusMessage) goes on the status line.
            context.Features.Get<IHttpResponseFeature>()!.ReasonPhrase = response.StatusMessage;
        }

        foreach (var group in response.Headers)
        {
            if (!SkipHeaders.Contains(group.Key))
            {
                context.Response.Headers.Append(group.Key, group.ToArray());
            }
        }

        // gzip the body when the client accepts it, for any content type.
        var body = response.Body;
        if (body.Length > 0 && AcceptsGzip(context.Request))
        {
            body = Gzip(body);
            context.Response.Headers.ContentEncoding = "gzip";
        }

        await context.Response.Body.WriteAsync(body);
        return tenant;
    }

    // Writes a proxied/recorded upstream response back to the caller verbatim: status, the upstream's
    // headers (minus the transport headers Kestrel reframes), and the body exactly as received — no
    // re-encoding, since the upstream already set its own Content-Encoding. This is pass-through
    // relay of a proxied response.
    private static async Task WriteUpstreamAsync(HttpContext context, CanonicalResponse response)
    {
        context.Response.StatusCode = response.Status;
        foreach (var group in response.Headers)
        {
            if (!SkipHeaders.Contains(group.Key))
            {
                context.Response.Headers.Append(group.Key, group.ToArray());
            }
        }

        await context.Response.Body.WriteAsync(response.Body);
    }

    private static bool AcceptsGzip(HttpRequest request) =>
        request.Headers.AcceptEncoding.Any(value => value is not null && value.Contains("gzip", StringComparison.OrdinalIgnoreCase));

    private static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return buffer.ToArray();
    }

    // Emits a fault the way it manifests to an HTTP client: a broken connection. Empty-response and
    // reset abort with nothing written; the malformed/random kinds write some bytes first, then abort
    // mid-response. HttpClient surfaces all of them as a request failure (verified against the oracle).
    private static async Task EmitFaultAsync(HttpContext context, FaultDirective fault)
    {
        switch (fault.Kind)
        {
            case FaultKind.MalformedResponseChunk:
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.Body.WriteAsync(new byte[] { 0x00, 0xFF, 0x00, 0xFF });
                break;

            case FaultKind.RandomDataThenClose:
                await context.Response.Body.WriteAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
                break;
        }

        context.Abort();
    }

    private static async Task<CanonicalRequest> BuildRequestAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer);

        var headers = context.Request.Headers
            .SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value ?? string.Empty)))
            .ToList();

        var url = context.Request.Path + context.Request.QueryString;

        // Scheme is supplied here (not header-borne); host/port derive from the Host header inside the
        // builder, so multi-domain matching (G15c, verified by the differential suite) sees the same
        // values the transport did.
        return CanonicalRequestBuilder.Build(
            context.Request.Method, url, headers, buffer.ToArray(), context.Request.Scheme);
    }

    /// <summary>
    /// Writes a stored response back and journals it as a replay (#358).
    /// </summary>
    /// <remarks>
    /// Journaled rather than silent: the request really did arrive, and a journal that omitted it
    /// would disagree with the client about what happened. The entry says plainly that nothing ran.
    /// </remarks>
    private static async Task ReplayAsync(
        HttpContext context, TenantId tenant, CanonicalRequest request, IdempotentResponse stored)
    {
        context.Response.StatusCode = stored.Status;
        foreach (var header in stored.Headers)
        {
            if (!SkipHeaders.Contains(header.Key))
            {
                context.Response.Headers.Append(header.Key, header.Value);
            }
        }

        // Named so a client can tell a replay from a fresh serve without diffing bodies.
        context.Response.Headers["Idempotency-Replayed"] = "true";
        await context.Response.Body.WriteAsync(stored.Body);

        context.RequestServices.GetRequiredService<IRequestJournal>().Record(new ServeEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Request = request,
            Response = new CanonicalResponse
            {
                Status = stored.Status,
                Headers = stored.Headers.ToLookup(header => header.Key, header => header.Value),
                Body = stored.Body,
            },
            Timestamp = DateTimeOffset.UtcNow,
            Replayed = true,
        });
    }

    /// <summary>
    /// Serves normally while buffering the response, then remembers it against the key (#358).
    /// </summary>
    /// <remarks>
    /// Buffered only for a request that actually carries a key on an unsafe method — every other
    /// request writes straight to the wire, so nothing changes for the traffic this has nothing to do
    /// with.
    /// </remarks>
    private static async Task CaptureAsync(
        HttpContext context, TenantId tenant, CanonicalRequest request, string key, string fingerprint,
        IIdempotencyStore store)
    {
        var original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await ServeRestAsync(context, tenant, request);
        }
        finally
        {
            context.Response.Body = original;
        }

        var body = buffer.ToArray();

        // A server failure is not remembered: replaying a 500 for a day would make a transient failure
        // permanent for the one key the client is retrying with.
        if (context.Response.StatusCode < 500)
        {
            store.Put(tenant, key, new IdempotentResponse(
                fingerprint,
                context.Response.StatusCode,
                [.. context.Response.Headers
                    .Where(header => !SkipHeaders.Contains(header.Key))
                    .Select(header => new KeyValuePair<string, string>(header.Key, header.Value.ToString()))],
                body,
                DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
        }

        await original.WriteAsync(body);
    }
}
