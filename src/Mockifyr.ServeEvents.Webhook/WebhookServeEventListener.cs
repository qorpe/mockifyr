using System.Text;
using Mockifyr.Core;
using Mockifyr.Outbound;

namespace Mockifyr.ServeEvents.Webhook;

/// <summary>
/// The <see cref="IServeEventListener"/> that performs the <c>webhook</c> post-serve action:
/// after a stub with webhook definitions matches, it fires each outbound HTTP call. This is the
/// engine's outbound-I/O edge — the pure core only records the <see cref="WebhookDefinition"/>; the
/// actual network call lives here (see docs/decisions/0001). Delivery is best-effort and never
/// throws back into serving. When a template renderer is supplied (G3b, verified by the differential
/// suite), the URL, header values, and body are rendered against the triggering request (exposed to
/// templates as <c>originalRequest</c>).
/// Each delivery is appended to the serve event as WEBHOOK_REQUEST / WEBHOOK_RESPONSE sub-events
/// (upstream 3.x journal shape), and a failed delivery — unreachable target or a template that does
/// not render — as an ERROR sub-event, so the journal shows what was actually sent and what came back.
/// </summary>
public sealed class WebhookServeEventListener : IServeEventListener
{
    // Content headers must be set on HttpContent, not HttpRequestMessage; this is the set that
    // webhook parameters commonly carry.
    private static readonly HashSet<string> ContentHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Content-Type", "Content-Length", "Content-Encoding", "Content-Language" };

    private readonly HttpClient _client;
    private readonly OutboundHostPolicy _outboundHosts;
    private readonly IServeEventTemplateRenderer? _renderer;
    private readonly bool _hostFallback;

    /// <summary>
    /// <paramref name="hostFallback"/> enables the container-localhost retry (#170): a callback aimed
    /// at loopback that is <em>refused</em> while running in a container is retried once against the
    /// host gateway. On by default because the failure it fixes is a hard, silent one; disable with
    /// <c>--webhook-host-fallback false</c> to keep delivery to exactly the address as written.
    /// </summary>
    public WebhookServeEventListener(
        HttpClient? client = null,
        IServeEventTemplateRenderer? renderer = null,
        bool hostFallback = true,
        OutboundHostPolicy? outboundHosts = null)
    {
        _client = client ?? new HttpClient();
        _renderer = renderer;
        _hostFallback = hostFallback;
        _outboundHosts = outboundHosts ?? OutboundHostPolicy.Unrestricted;
    }

    /// <inheritdoc />
    public async Task OnServeEventAsync(ServeEvent serveEvent, CancellationToken cancellationToken)
    {
        if (serveEvent.MatchedStub is not { Webhooks: { Count: > 0 } webhooks })
        {
            return;
        }

        foreach (var webhook in webhooks)
        {
            await SendAsync(webhook, serveEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(WebhookDefinition webhook, ServeEvent serveEvent, CancellationToken cancellationToken)
    {
        if (webhook.DelayMilliseconds > 0)
        {
            try { await Task.Delay(webhook.DelayMilliseconds, cancellationToken).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
        }

        var originalRequest = serveEvent.Request;
        string url;
        byte[]? renderedBody = null;
        List<KeyValuePair<string, string>> renderedHeaders;
        try
        {
            url = Render(webhook.Url, originalRequest, serveEvent.TenantId);
            if (webhook.Body is { } body)
            {
                renderedBody = RenderBody(body, originalRequest, serveEvent.TenantId);
            }

            renderedHeaders = [.. webhook.Headers.Select(h =>
                new KeyValuePair<string, string>(h.Key, Render(h.Value, originalRequest, serveEvent.TenantId)))];
        }
        catch (Exception exception)
        {
            // A template that fails to render dies before any request exists, so there is nothing to
            // report but the error itself.
            Append(serveEvent, SubEvent.ErrorType, new WebhookErrorData(ContainerHostFallback.Describe(exception)));
            return;
        }

        // Checked against the RENDERED url, not the template (#349): the template may be
        // "{{request.headers.X-Callback}}", and a policy that inspected the text rather than the target
        // would allow exactly the case it exists to stop.
        if (!_outboundHosts.Allows(url))
        {
            // Recorded on the serve event like any failed delivery, so a refusal is visible rather
            // than a webhook that quietly never arrived.
            Append(serveEvent, SubEvent.ErrorType, new WebhookErrorData(_outboundHosts.Refusal(url)));
            return;
        }

        var failure = await AttemptAsync(webhook, url, renderedHeaders, renderedBody, serveEvent, cancellationToken)
            .ConfigureAwait(false);
        if (failure is null)
        {
            return;
        }

        // The container-localhost trap (#170): the target refused the connection and we are in a
        // container aimed at loopback, which means "nothing is listening on the container itself" —
        // the operator almost certainly meant a service on their machine. Retry once via the host
        // gateway. Anything else (timeout, DNS, TLS) is reported as-is; see ContainerHostFallback.
        var retryUrl = _hostFallback ? ContainerHostFallback.RetryTargetFor(url) : null;
        if (retryUrl is null || !ContainerHostFallback.IsConnectionRefused(failure))
        {
            Append(serveEvent, SubEvent.ErrorType,
                new WebhookErrorData(ContainerHostFallback.Explain(url, failure, fallbackAttempted: false)));
            return;
        }

        // Both attempts are journaled, so the retry is visible rather than magic.
        Append(serveEvent, SubEvent.ErrorType, new WebhookErrorData(
            $"{ContainerHostFallback.Describe(failure)} Retrying via {ContainerHostFallback.HostGateway} (#170)."));

        var retryFailure = await AttemptAsync(webhook, retryUrl, renderedHeaders, renderedBody, serveEvent, cancellationToken)
            .ConfigureAwait(false);
        if (retryFailure is not null)
        {
            Append(serveEvent, SubEvent.ErrorType,
                new WebhookErrorData(ContainerHostFallback.Explain(url, retryFailure, fallbackAttempted: true)));
        }
    }

    /// <summary>
    /// Journals the outbound request, sends it, and journals the response. Returns the exception on
    /// failure instead of throwing, so the caller can decide whether it is worth another address.
    /// </summary>
    private async Task<Exception?> AttemptAsync(
        WebhookDefinition webhook,
        string url,
        List<KeyValuePair<string, string>> renderedHeaders,
        byte[]? renderedBody,
        ServeEvent serveEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(webhook.Method), url);
            if (renderedBody is not null)
            {
                request.Content = new ByteArrayContent(renderedBody);
            }

            foreach (var (name, value) in renderedHeaders)
            {
                if (ContentHeaders.Contains(name))
                {
                    // A content header needs an HttpContent to hang off of.
                    request.Content ??= new ByteArrayContent([]);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            Append(serveEvent, SubEvent.WebhookRequestType,
                new WebhookRequestData(webhook.Method, url, renderedHeaders, renderedBody));

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var responseHeaders = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
                .ToList();
            Append(serveEvent, SubEvent.WebhookResponseType,
                new WebhookResponseData((int)response.StatusCode, responseHeaders, responseBody.Length > 0 ? responseBody : null));
            return null;
        }
        catch (Exception exception)
        {
            // Best-effort delivery: an unreachable target may never surface into request serving.
            return exception;
        }
    }

    private static void Append(ServeEvent serveEvent, string type, object data) =>
        serveEvent.AppendSubEvent(new SubEvent(
            type,
            (DateTimeOffset.UtcNow - serveEvent.Timestamp).Ticks * 100,
            data));

    // Templates a webhook field against originalRequest; a null renderer leaves it literal (G3a). The
    // tenant is threaded through so environment keys resolve against the owning tenant (G17).
    private string Render(string value, CanonicalRequest originalRequest, TenantId tenant) =>
        _renderer is null ? value : _renderer.Render(value, originalRequest, tenant);

    private byte[] RenderBody(byte[] body, CanonicalRequest originalRequest, TenantId tenant) =>
        _renderer is null ? body : Encoding.UTF8.GetBytes(Render(Encoding.UTF8.GetString(body), originalRequest, tenant));
}
