using System.Text;
using RabbitMQ.Client;

namespace Mockifyr.Facade.Broker;

/// <summary>
/// Publishes to AMQP / RabbitMQ (ADR 0013, slice 4), behind the same contract Kafka uses.
/// </summary>
/// <remarks>
/// <para>
/// The ADR said to design for Kafka first because it is the harder shape, so that AMQP would fit
/// inside rather than the reverse. It did: this implements <see cref="IBrokerPublisher"/> unchanged,
/// and every mapping, template and admin route above it is transport-agnostic already.
/// </para>
/// <para>
/// <b>A topic is not an AMQP concept</b>, so the mock states the translation rather than inventing a
/// second vocabulary: <c>"topic": "exchange/routing.key"</c> addresses an exchange, and a topic with
/// no slash publishes to the <b>default exchange</b> with the topic as the routing key — which
/// delivers straight to a queue of that name. That makes <c>{"topic":"orders.events"}</c> mean the
/// obvious thing on both transports, which is the whole point of one dialect.
/// </para>
/// <para>
/// <b>A partition key has no AMQP counterpart.</b> Rather than drop it silently, <c>key</c> becomes the
/// message's <c>MessageId</c> — the closest standard property, and one a consumer can actually read.
/// </para>
/// </remarks>
public sealed class AmqpPublisher : IBrokerPublisher
{
    private readonly string _uri;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>Creates a publisher for the given AMQP URI (e.g. <c>amqp://guest:guest@localhost:5672/</c>).</summary>
    /// <remarks>
    /// Nothing connects here. A constructor that dialled a broker would make an unreachable one a
    /// startup crash rather than a recorded delivery failure, which is the opposite of what a mock
    /// should do to the host it runs beside.
    /// </remarks>
    public AmqpPublisher(string uri) => _uri = uri;

    /// <inheritdoc />
    public async Task PublishAsync(
        string topic,
        string? key,
        string? body,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var channel = await ChannelAsync(cancellationToken).ConfigureAwait(false);
        var (exchange, routingKey) = Split(topic);

        var properties = new BasicProperties();
        if (key is { Length: > 0 })
        {
            properties.MessageId = key;
        }

        if (headers.Count > 0)
        {
            properties.Headers = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in headers)
            {
                properties.Headers[name] = Encoding.UTF8.GetBytes(value);
            }
        }

        await channel.BasicPublishAsync(
            exchange,
            routingKey,

            // Mandatory stays off: an unroutable message would come back as a separate asynchronous
            // return, and a mock that failed a publish because nobody had declared a queue yet would
            // be reporting the test's setup order as its own error.
            mandatory: false,
            properties,
            body is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(body),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Splits <c>exchange/routing.key</c>; a topic with no slash is the default exchange.</summary>
    public static (string Exchange, string RoutingKey) Split(string topic)
    {
        var separator = topic.IndexOf('/', StringComparison.Ordinal);

        // The default exchange is named "", and publishing to it with a routing key delivers to the
        // queue of that name — so a single-segment topic behaves the way a Kafka topic does.
        return separator < 0
            ? (string.Empty, topic)
            : (topic[..separator], topic[(separator + 1)..]);
    }

    /// <summary>
    /// The shared channel, opened on first use. One connection and one channel for the host's
    /// lifetime, as with the Kafka producer: a connection per message would turn every publish into a
    /// handshake.
    /// </summary>
    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is { IsOpen: true } opened)
            {
                return opened;
            }

            // A dropped connection must heal rather than poison the publisher for the host's lifetime:
            // the old objects are discarded and the next publish reconnects.
            if (_channel is not null)
            {
                await _channel.DisposeAsync().ConfigureAwait(false);
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            var factory = new ConnectionFactory { Uri = new Uri(_uri) };
            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
