using System.Text;
using System.Text.Json;
using Mockifyr.Core;

namespace Mockifyr.Server;

/// <summary>
/// Decorates the message sink with the capture webhook (G18e): when the tenant's behaviors carry a
/// <see cref="MessageBehaviors.WebhookUrl"/>, every captured message is POSTed there as JSON.
/// Delivery is best-effort and fire-and-forget — a webhook must never slow down or fail a capture,
/// the same rule the G3 stub webhooks follow. Lives in the composition root: this is the outbound
/// I/O edge, never Core.
/// </summary>
internal sealed class NotifyingMessageSink(IMessageSink inner, IMessageBehaviorStore behaviors, HttpClient client) : IMessageSink
{
    public void Accept(TenantId tenant, MessageEnvelope message)
    {
        inner.Accept(tenant, message);

        if (behaviors.Get(tenant).WebhookUrl is not { Length: > 0 } url)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            id = message.Id,
            channel = message.Channel == MessageChannel.Email ? "email" : "sms",
            tenant = tenant.Value,
            from = message.From,
            to = message.To,
            subject = message.Subject,
            body = message.Body,
            meta = message.Meta,
            receivedAt = message.ReceivedAt,
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await client.PostAsync(url, content);
            }
            catch (Exception)
            {
                // Unreachable target — best-effort by design.
            }
        });
    }
}
