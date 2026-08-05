using Mockifyr.Core;

namespace Mockifyr.Facade.Broker;

/// <summary>Sub-event payload recorded for a published message.</summary>
/// <param name="Topic">The topic it went to.</param>
/// <param name="Key">The partition key, when one was rendered.</param>
/// <param name="Body">The message body as published.</param>
public sealed record PublishData(string Topic, string? Key, string? Body);

/// <summary>Sub-event payload recorded when a publish could not be delivered.</summary>
/// <param name="Topic">The topic it was meant for.</param>
/// <param name="Key">The partition key that was rendered, or <c>null</c> if rendering is what failed.</param>
/// <param name="Body">The body that was rendered, or <c>null</c> if rendering is what failed.</param>
/// <param name="Error">What went wrong, in the words the client used.</param>
/// <remarks>
/// The message carries what it *would have* sent, not just that it did not send. "Delivery failed" and
/// "delivery failed, and here is the body whose template you got wrong" are the difference between
/// knowing something is broken and knowing what.
/// </remarks>
public sealed record PublishErrorData(string Topic, string? Key, string? Body, string Error);

/// <summary>
/// Publishes a stub's declared messages after it serves a request (ADR 0013, slice 1).
/// </summary>
/// <remarks>
/// <para>
/// The webhook listener's shape, pointed at a broker instead of at HTTP: the engine records the intent
/// on the stub and this performs the I/O at the edge, so <c>Mockifyr.Core</c> never learns what a
/// broker is. Every field is templated against the triggering request, which is what makes
/// <c>"key": "{{jsonPath request.body '$.orderId'}}"</c> the obvious thing to write.
/// </para>
/// <para>
/// Delivery is recorded either way. A stub that claims to emit an event and quietly fails to would be
/// worse than one that never claimed it, so both the message and any failure land on the serve event
/// where the journal can show them.
/// </para>
/// </remarks>
public sealed class PublishServeEventListener(
    IBrokerPublisher publisher, IServeEventTemplateRenderer renderer) : IServeEventListener
{
    /// <summary>The sub-event type recorded for a message that was published.</summary>
    public const string PublishedType = "BROKER_PUBLISH";

    /// <summary>The sub-event type recorded for a message that could not be published.</summary>
    public const string FailedType = "BROKER_PUBLISH_ERROR";

    /// <inheritdoc />
    public async Task OnServeEventAsync(ServeEvent serveEvent, CancellationToken cancellationToken)
    {
        if (serveEvent.MatchedStub is not { Publishes: { Count: > 0 } publishes })
        {
            return;
        }

        foreach (var publish in publishes)
        {
            await SendAsync(publish, serveEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(PublishDefinition publish, ServeEvent serveEvent, CancellationToken cancellationToken)
    {
        if (publish.DelayMilliseconds > 0)
        {
            try
            {
                await Task.Delay(publish.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }

        string topic;
        string? key;
        string? body;
        List<KeyValuePair<string, string>> headers;
        try
        {
            topic = Render(publish.Topic, serveEvent);
            key = publish.Key is null ? null : Render(publish.Key, serveEvent);
            body = publish.Body is null ? null : Render(publish.Body, serveEvent);
            headers = [.. publish.Headers.Select(h =>
                new KeyValuePair<string, string>(h.Key, Render(h.Value, serveEvent)))];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A template that fails to render dies before any message exists, so there is nothing to
            // report but the error itself — the same shape the webhook listener uses. The topic is the
            // raw template rather than a rendered one, because rendering is what failed.
            Append(serveEvent, FailedType, new PublishErrorData(publish.Topic, null, null, exception.Message));
            return;
        }

        try
        {
            await publisher.PublishAsync(topic, key, body, headers, cancellationToken).ConfigureAwait(false);
            Append(serveEvent, PublishedType, new PublishData(topic, key, body));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreachable broker must not take the served response down with it: the client already
            // has its answer by the time this runs, and the failure belongs in the journal — with the
            // message it was carrying, so a failure is diagnosable and not merely visible.
            Append(serveEvent, FailedType, new PublishErrorData(topic, key, body, exception.Message));
        }
    }

    private string Render(string template, ServeEvent serveEvent) =>
        renderer.Render(template, serveEvent.Request, serveEvent.TenantId);

    private static void Append(ServeEvent serveEvent, string type, object data) =>
        serveEvent.AppendSubEvent(new SubEvent(
            type,
            (DateTimeOffset.UtcNow - serveEvent.Timestamp).Ticks * 100,
            data));
}
