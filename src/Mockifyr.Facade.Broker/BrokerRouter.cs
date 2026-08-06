namespace Mockifyr.Facade.Broker;

/// <summary>
/// Sends each message to the transport its topic names, when a host is configured with more than one
/// (ADR 0013, slice 4).
/// </summary>
/// <remarks>
/// <para>
/// A host with a single broker never meets this: every publish goes where the only publisher goes, and
/// nothing has to be spelled differently than it was before AMQP existed. The prefix is only needed
/// when a host genuinely has two places a message could go, and then not naming one would be
/// ambiguous rather than convenient.
/// </para>
/// <para>
/// <b>Why a prefix and not a new field.</b> A <c>broker</c> field would have to be added to the
/// mapping model in Core, to the mapping-JSON reader, to the post-serve publish action and to the
/// broker-mapping publish action — four places, to express a choice that is part of the destination.
/// A prefixed topic works identically in all four with no model change, and Core still does not know
/// what a broker is. Kafka topic names cannot contain a colon, so nothing legal is shadowed.
/// </para>
/// </remarks>
public sealed class BrokerRouter : IBrokerPublisher
{
    /// <summary>How a topic names Kafka explicitly.</summary>
    public const string KafkaPrefix = "kafka:";

    /// <summary>How a topic names AMQP explicitly.</summary>
    public const string AmqpPrefix = "amqp:";

    private readonly IBrokerPublisher? _kafka;
    private readonly IBrokerPublisher? _amqp;
    private readonly IBrokerPublisher _fallback;

    /// <summary>Creates a router over the transports this host configured. At least one is required.</summary>
    public BrokerRouter(IBrokerPublisher? kafka, IBrokerPublisher? amqp)
    {
        _kafka = kafka;
        _amqp = amqp;
        _fallback = kafka ?? amqp
            ?? throw new ArgumentException("a router needs at least one transport.", nameof(kafka));
    }

    /// <inheritdoc />
    public Task PublishAsync(
        string topic,
        string? key,
        string? body,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var (publisher, destination) = Route(topic);
        return publisher.PublishAsync(destination, key, body, headers, cancellationToken);
    }

    /// <summary>Which transport a topic names, and the topic with any prefix removed.</summary>
    /// <remarks>
    /// A prefix naming a transport this host does not have falls back rather than failing. The
    /// alternative — refusing the message — would turn a mapping that is portable between a
    /// Kafka-only and an AMQP-only host into one that only runs on the host it was written for.
    /// </remarks>
    public (IBrokerPublisher Publisher, string Topic) Route(string topic)
    {
        if (topic.StartsWith(KafkaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (_kafka ?? _fallback, topic[KafkaPrefix.Length..]);
        }

        if (topic.StartsWith(AmqpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (_amqp ?? _fallback, topic[AmqpPrefix.Length..]);
        }

        return (_fallback, topic);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_kafka is not null)
        {
            await _kafka.DisposeAsync().ConfigureAwait(false);
        }

        if (_amqp is not null)
        {
            await _amqp.DisposeAsync().ConfigureAwait(false);
        }
    }
}
