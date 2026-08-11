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

    private static async Task ServeAsync(HttpContext context)
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
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // Fixed-window quota (race-free; ADR 0011 addendum) with honest rate headers; the
            // refusal is a realistic 429 with Retry-After.
            var decision = context.RequestServices.GetRequiredService<FixedWindowRateLimiter>()
                .Count(key.Id, key.QuotaPerHour);
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
                return;
            }

            keyTenant = key.Tenant;
        }

        var tenant = keyTenant
            ?? (context.Request.Headers.TryGetValue(TenantHeader, out var t) && !string.IsNullOrEmpty(t)
                ? new TenantId(t!)
                : TenantId.Default);

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
            return;
        }

        var resolution = engine.Handle(tenant, request);

        if (resolution.Response is not { } response)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

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
            return;
        }

        if (degradation.ErrorStatus is { } degradedStatus)
        {
            context.Response.StatusCode = degradedStatus;
            return;
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
            return;
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
            return;
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
}
