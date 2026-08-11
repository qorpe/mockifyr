using Mockifyr.Core;

namespace Mockifyr.Outbound;

/// <summary>
/// Raised when a proxy forward cannot be delivered. Carries the flattened cause so the transport can
/// surface it, and a flag marking the container-localhost diagnosis (#176) — the one case where the
/// facade turns the failure into an explanatory 502 rather than an opaque 500.
/// </summary>
public sealed class ProxyDeliveryException(string message, bool containerDiagnosis, bool refused = false)
    : Exception(message)
{
    /// <summary>
    /// Whether this host <em>declined</em> to make the call rather than failing to complete it (#349).
    /// </summary>
    /// <remarks>
    /// Kept apart from a connection failure because the two need different answers: a refusal has a
    /// message an operator can act on, and letting it propagate as an unhandled exception would turn
    /// the one outcome we can fully explain into an opaque 500.
    /// </remarks>
    public bool Refused { get; } = refused;

    /// <summary>True when the failure is the container-localhost trap and the message explains it.</summary>
    public bool ContainerDiagnosis { get; } = containerDiagnosis;
}

/// <summary>
/// Applies a <see cref="ProxyDirective"/> (G8): forwards the matched request to the upstream at
/// <c>proxyBaseUrl</c> (preserving the path + query, method, headers, and body) and returns the
/// upstream's response. This is outbound I/O, so it lives at the facade edge — the pure engine only
/// records the directive. The transport HTTP facade (G12) reuses this same responder over the wire.
/// <para>
/// <paramref name="hostFallback"/> extends the callback fix (#170) to proxying (#176): a loopback
/// target whose connection is <em>refused</em> while containerised is retried once via the host
/// gateway, because inside a container <c>localhost</c> is the container itself. On by default; a plain
/// <see cref="HttpClient"/> and the retry are both skipped when a service really does answer.
/// </para>
/// </summary>
public sealed class ProxyResponder(
    HttpClient? client = null,
    bool hostFallback = true,
    OutboundHostPolicy? outboundHosts = null)
{
    // The Host header must track the upstream URL (set by HttpClient), not the original mock host.
    private static readonly HashSet<string> DropForwardHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "Host", "Content-Length" };

    private readonly HttpClient _client = client ?? new HttpClient();

    public async Task<CanonicalResponse> ProxyAsync(
        ProxyDirective proxy, CanonicalRequest request, CancellationToken cancellationToken = default)
    {
        // proxyUrlPrefixToRemove strips a leading path prefix from the request before forwarding (G8, verified by the differential suite).
        var forwardPath = proxy.UrlPrefixToRemove is { Length: > 0 } prefix && request.Url.StartsWith(prefix, StringComparison.Ordinal)
            ? request.Url[prefix.Length..]
            : request.Url;

        var target = proxy.BaseUrl.TrimEnd('/') + forwardPath;

        // The outbound allowlist (#349). A proxy stub names its own target, so this is the other half
        // of the same control the webhook edge applies — and the refusal is an exception rather than a
        // silent pass-through, because a proxy that quietly returned nothing would read as the upstream
        // being down.
        var policy = outboundHosts ?? OutboundHostPolicy.Unrestricted;
        if (!policy.Allows(target))
        {
            throw new ProxyDeliveryException(policy.Refusal(target), containerDiagnosis: false, refused: true);
        }
        try
        {
            return await SendAsync(proxy, request, target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not ProxyDeliveryException)
        {
            // The container-localhost trap (#176), mirroring the callback fix: a refused loopback target
            // inside a container means nothing is listening on the container itself, so the operator
            // almost certainly meant a service on their machine. Retry once via the host gateway.
            var retry = hostFallback ? ContainerHostFallback.RetryTargetFor(target) : null;
            if (retry is null || !ContainerHostFallback.IsConnectionRefused(exception))
            {
                throw new ProxyDeliveryException(
                    ContainerHostFallback.Explain(target, exception, fallbackAttempted: false),
                    containerDiagnosis: false);
            }

            try
            {
                return await SendAsync(proxy, request, retry, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryException) when (retryException is not ProxyDeliveryException)
            {
                // The retry's own error kind is irrelevant; what earns the diagnosis is that the
                // original loopback attempt was refused. See ContainerHostFallback.Explain.
                throw new ProxyDeliveryException(
                    ContainerHostFallback.Explain(target, retryException, fallbackAttempted: true),
                    containerDiagnosis: true);
            }
        }
    }

    // A fresh HttpRequestMessage per attempt: a message cannot be reused once sent, and the retry
    // targets a different URL. The request body and headers are reusable, so this is cheap.
    private async Task<CanonicalResponse> SendAsync(
        ProxyDirective proxy, CanonicalRequest request, string target, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), target);

        if (request.Body is { Length: > 0 } body)
        {
            message.Content = new ByteArrayContent(body);
        }

        foreach (var group in request.Headers)
        {
            if (DropForwardHeaders.Contains(group.Key))
            {
                continue;
            }

            foreach (var value in group)
            {
                if (!message.Headers.TryAddWithoutValidation(group.Key, value))
                {
                    message.Content?.Headers.TryAddWithoutValidation(group.Key, value);
                }
            }
        }

        // additionalProxyRequestHeaders are added to the forwarded request (G8, verified by the differential suite).
        foreach (var (name, value) in proxy.AdditionalHeaders)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await _client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        var headers = response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string>(header.Key, value)))
            .ToLookup(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        return new CanonicalResponse
        {
            Status = (int)response.StatusCode,
            Headers = headers,
            Body = responseBody,
        };
    }
}
