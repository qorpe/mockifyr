using System.Text;
using Microsoft.Extensions.Hosting;
using Mockifyr.Core;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Mockifyr.Facade.Broker;

/// <summary>How this host consumes from AMQP (ADR 0013, slice 4).</summary>
/// <param name="Uri">The AMQP URI to connect to.</param>
/// <param name="Queues">The queues to consume. Empty means capture is off.</param>
public sealed record AmqpCaptureOptions(string Uri, IReadOnlyList<string> Queues);

/// <summary>
/// Consumes AMQP queues, captures what arrives, and serves the tenant's broker mappings (ADR 0013,
/// slice 4) — the same three steps in the same order as the Kafka consumer.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is the guarantee, not an implementation detail: <b>capture, serve, then acknowledge</b>.
/// A host that died in between redelivers rather than losing the message, and a message that produced
/// a reply is still in the inbox afterwards — which is how anybody debugs a mapping that did not fire.
/// </para>
/// <para>
/// Queues are declared as durable on connect, so pointing a host at a queue nobody has created yet
/// works rather than failing. A mock that required the system under test to have started first would
/// make test ordering a deployment concern.
/// </para>
/// </remarks>
public sealed class AmqpCaptureService(
    AmqpCaptureOptions options,
    IMessageSink sink,
    TimeProvider? clock = null,
    BrokerMappingPlanner? planner = null,
    IBrokerPublisher? publisher = null,
    // Last, and optional: callers construct this positionally, so a parameter inserted mid-list
    // silently rebinds their arguments rather than failing to compile (#396).
    TenantHeaderOptions? tenantHeader = null) : BackgroundService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private IConnection? _connection;
    private IChannel? _channel;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Queues.Count == 0)
        {
            return;
        }

        // Retried rather than fatal: a broker that is still starting beside this host is the normal
        // case in a compose file, and a capture loop that gave up once would need a restart to recover.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                planner?.Failures.Add(new PlanFailure("amqp-capture", exception.Message));
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory { Uri = new Uri(options.Uri) };
        _connection = await factory.CreateConnectionAsync(stoppingToken).ConfigureAwait(false);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken).ConfigureAwait(false);

        // One unacknowledged message at a time. Ordering within a queue is the only ordering AMQP
        // offers, and a prefetch window would let a later message be served before an earlier one.
        await _channel.BasicQosAsync(0, prefetchCount: 1, global: false, stoppingToken).ConfigureAwait(false);

        foreach (var queue in options.Queues)
        {
            await _channel.QueueDeclareAsync(
                queue, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: stoppingToken).ConfigureAwait(false);

            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += (_, delivered) => HandleAsync(queue, delivered, stoppingToken);

            await _channel.BasicConsumeAsync(
                queue, autoAck: false, consumer, stoppingToken).ConfigureAwait(false);
        }

        // The consumer callbacks do the work; this only keeps the connection alive until shutdown.
        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    private async Task HandleAsync(string queue, BasicDeliverEventArgs delivered, CancellationToken cancellationToken)
    {
        var record = Read(queue, delivered);
        var tenant = BrokerMessageFactory.TenantOf(record, (tenantHeader ?? TenantHeaderOptions.Default).Name);

        try
        {
            sink.Accept(tenant, BrokerMessageFactory.Build(record, _clock.GetUtcNow()));
            Serve(tenant, record, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            planner?.Failures.Add(new PlanFailure(queue, exception.Message));
        }

        if (_channel is { IsOpen: true } channel)
        {
            // Only now. Acknowledging first would let a host that crashed mid-serve drop a message
            // nobody can see afterwards — at-least-once, as ADR 0013 states for both transports.
            await channel.BasicAckAsync(delivered.DeliveryTag, multiple: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Reduces an AMQP delivery to the transport-free record the rest of the channel speaks.</summary>
    /// <remarks>
    /// The <b>queue</b> stands in for the topic, so one <c>whenTopic</c> matcher works on both
    /// transports; the delivery tag stands in for the offset, being the same kind of fact — where this
    /// message sits in what the consumer has been handed.
    /// </remarks>
    public static ConsumedRecord Read(string queue, BasicDeliverEventArgs delivered)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (delivered.BasicProperties.Headers is { } amqpHeaders)
        {
            foreach (var (name, value) in amqpHeaders)
            {
                headers.Add(new KeyValuePair<string, string>(name, Stringify(value)));
            }
        }

        return new ConsumedRecord(
            queue,
            delivered.BasicProperties.MessageId,
            Encoding.UTF8.GetString(delivered.Body.Span),
            Partition: 0,
            Offset: (long)delivered.DeliveryTag,
            headers);
    }

    /// <summary>
    /// AMQP header values are typed. The client hands byte arrays for strings, so those are decoded
    /// rather than printed as <c>System.Byte[]</c>, which is what a matcher would otherwise be asked
    /// to match against.
    /// </summary>
    private static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
        _ => value.ToString() ?? string.Empty,
    };

    private void Serve(TenantId tenant, ConsumedRecord record, CancellationToken cancellationToken)
    {
        if (planner is null || publisher is null)
        {
            return;
        }

        foreach (var message in planner.Plan(tenant, record))
        {
            try
            {
                publisher
                    .PublishAsync(message.Topic, message.Key, message.Body, message.Headers, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A broker that will not take the reply must not stop this host from consuming the
                // next message, for the same reason an unmatched message is not parked.
                planner.Failures.Add(new PlanFailure(message.Topic, exception.Message));
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        if (_channel is not null)
        {
            await _channel.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
