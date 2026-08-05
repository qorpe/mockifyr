namespace Mockifyr.Core;

/// <summary>The channel a captured message arrived through (G18, ADR 0009).</summary>
public enum MessageChannel
{
    Email,
    Sms,

    /// <summary>
    /// A message on a broker topic (ADR 0013). Topic and partition key live in <c>Meta</c>, the same
    /// place the SMS profile puts its provider fields — one inbox, one verify surface, one screen.
    /// <c>Subject</c> and <c>HtmlBody</c> stay null, as they do on SMS.
    /// </summary>
    Broker,
}

/// <summary>An attachment carried by a captured email message.</summary>
public sealed record MessageAttachment(string Name, string ContentType, byte[] Content)
{
    /// <summary>The attachment size in bytes.</summary>
    public int Size => Content.Length;
}

/// <summary>
/// A captured outbound message (G18, ADR 0009): the transport-neutral envelope a facade produces
/// after parsing its wire format (MIME for SMTP, a provider's form body for SMS). Core never sees
/// either wire format — only this value. <see cref="Meta"/> carries channel-specific fields
/// (mail headers, provider ids) as a flat string map so the envelope stays closed under new
/// providers without growing per-provider properties.
/// </summary>
public sealed record MessageEnvelope(
    Guid Id,
    MessageChannel Channel,
    string From,
    IReadOnlyList<string> To,
    string? Subject,
    string Body,
    string? HtmlBody,
    IReadOnlyDictionary<string, string> Meta,
    IReadOnlyList<MessageAttachment> Attachments,
    DateTimeOffset ReceivedAt,
    string? Raw = null);

/// <summary>
/// The tenant-scoped inbox of captured messages (G18). Bounded: the store holds at most its
/// configured capacity per tenant and evicts oldest-first, so an unattended host cannot grow without
/// limit. Every entry point takes an explicit <see cref="TenantId"/> — there is no tenant-less
/// overload (ADR 0003).
/// </summary>
public interface IMessageStore
{
    /// <summary>Appends a captured message to the tenant's inbox, evicting the oldest beyond capacity.</summary>
    void Append(TenantId tenant, MessageEnvelope message);

    /// <summary>The tenant's messages, newest first.</summary>
    IReadOnlyList<MessageEnvelope> GetMessages(TenantId tenant);

    /// <summary>The tenant's message with the given id, or null.</summary>
    MessageEnvelope? Get(TenantId tenant, Guid id);

    /// <summary>Removes one message; false when it does not exist.</summary>
    bool Remove(TenantId tenant, Guid id);

    /// <summary>Clears the tenant's inbox.</summary>
    void Reset(TenantId tenant);
}

/// <summary>
/// The write-side seam a message-producing facade calls (G18). The default sink appends to the
/// store; decorators layer behavior (serve events → webhooks) without the facade knowing.
/// </summary>
public interface IMessageSink
{
    /// <summary>Accepts a captured message for the tenant.</summary>
    void Accept(TenantId tenant, MessageEnvelope message);
}

/// <summary>How the SMTP facade misbehaves on purpose (G18e) — the message-channel analog of HTTP faults.</summary>
public enum SmtpFaultMode
{
    None,
    /// <summary>Refuse DATA with a 550 — the client sees a permanent failure (a bounce).</summary>
    Reject,
    /// <summary>Close the connection mid-transaction without a reply.</summary>
    Drop,
}

/// <summary>
/// Per-tenant message-channel behavior directives (G18e, ADR 0009): fault injection for SMTP,
/// simulated provider errors for SMS, and an optional webhook notified on every capture. Like HTTP
/// delay/fault, these are <b>facade directives</b> — Core records the configuration, the facades
/// apply it.
/// </summary>
public sealed record MessageBehaviors(
    SmtpFaultMode SmtpFault = SmtpFaultMode.None,
    int SmtpDelayMs = 0,
    int? SmsErrorCode = null,
    string? WebhookUrl = null)
{
    /// <summary>The no-directives default every tenant starts with.</summary>
    public static readonly MessageBehaviors None = new();
}

/// <summary>Tenant-scoped storage for <see cref="MessageBehaviors"/>. No tenant-less overloads (ADR 0003).</summary>
public interface IMessageBehaviorStore
{
    /// <summary>The tenant's behaviors; <see cref="MessageBehaviors.None"/> when never configured.</summary>
    MessageBehaviors Get(TenantId tenant);

    /// <summary>Replaces the tenant's behaviors.</summary>
    void Set(TenantId tenant, MessageBehaviors behaviors);

    /// <summary>Returns the tenant to the no-directives default.</summary>
    void Reset(TenantId tenant);
}
