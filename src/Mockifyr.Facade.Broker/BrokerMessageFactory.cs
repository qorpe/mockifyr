using Mockifyr.Core;

namespace Mockifyr.Facade.Broker;

/// <summary>One consumed record, reduced to what the inbox needs — free of any client type.</summary>
/// <param name="Topic">The topic it arrived on.</param>
/// <param name="Key">The partition key, when the producer set one.</param>
/// <param name="Value">The payload as text.</param>
/// <param name="Partition">The partition it came from.</param>
/// <param name="Offset">Its offset within that partition.</param>
/// <param name="Headers">The message headers.</param>
public sealed record ConsumedRecord(
    string Topic,
    string? Key,
    string? Value,
    int Partition,
    long Offset,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// Turns a consumed broker record into a captured message (ADR 0013, slice 2).
/// </summary>
/// <remarks>
/// <para>
/// Pure, and deliberately separate from the consumer loop: everything interesting about capture — which
/// tenant a message belongs to, what ends up in the inbox, what an operator sees — is decided here and
/// can be asserted without a broker anywhere near the test.
/// </para>
/// <para>
/// A broker message is a <see cref="MessageEnvelope"/> with <see cref="MessageChannel.Broker"/>, per
/// ADR 0013: one inbox, one verify surface, one screen. Topic, key, partition and offset go in
/// <c>Meta</c>, the same place the SMS profile puts its provider fields.
/// </para>
/// </remarks>
public static class BrokerMessageFactory
{
    /// <summary>
    /// The header a producer sets to address a tenant, mirroring the HTTP facade's (ADR 0003/0009).
    /// </summary>
    public const string TenantHeader = "X-Mockifyr-Tenant";

    /// <summary>Which tenant this message belongs to: the header when set, the default otherwise.</summary>
    /// <remarks>
    /// A broker topic carries no tenancy of its own, so the honest options are "everything is the
    /// default tenant" or "a header says". The header keeps the same shape as every other channel, and
    /// its absence lands where a single-tenant host already expects to find things.
    /// </remarks>
    public static TenantId TenantOf(ConsumedRecord record)
    {
        foreach (var (name, value) in record.Headers)
        {
            if (string.Equals(name, TenantHeader, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(value))
            {
                return new TenantId(value);
            }
        }

        return TenantId.Default;
    }

    /// <summary>Builds the envelope the inbox stores.</summary>
    public static MessageEnvelope Build(ConsumedRecord record, DateTimeOffset receivedAt)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = record.Topic,
            ["partition"] = record.Partition.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["offset"] = record.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (record.Key is { Length: > 0 })
        {
            meta["key"] = record.Key;
        }

        // Headers ride along under a prefix so a producer cannot overwrite `topic` or `offset` with a
        // header of the same name — an operator reading the inbox must be able to trust those three.
        foreach (var (name, value) in record.Headers)
        {
            meta[$"header.{name}"] = value;
        }

        return new MessageEnvelope(
            Guid.NewGuid(),
            MessageChannel.Broker,

            // The topic is the closest thing a broker message has to a sender, and it is what somebody
            // scanning the inbox is looking for.
            From: record.Topic,

            // No recipients: a published message is addressed to a topic, not to anybody. Inventing a
            // consumer group here would suggest a delivery guarantee the inbox does not make.
            To: [],
            Subject: null,
            Body: record.Value ?? string.Empty,
            HtmlBody: null,
            meta,
            [],
            receivedAt);
    }
}
