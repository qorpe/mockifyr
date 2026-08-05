using Confluent.Kafka;
using Mockifyr.Core;

namespace Mockifyr.Facade.Broker;

/// <summary>
/// Where messages go. The seam exists so the serve-event listener can be tested without a broker, and
/// so a second transport (AMQP, ADR 0013 slice 4) slots in without the listener changing.
/// </summary>
public interface IBrokerPublisher : IAsyncDisposable
{
    /// <summary>Publishes one message and returns when the broker has accepted it.</summary>
    Task PublishAsync(
        string topic,
        string? key,
        string? body,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken);
}

/// <summary>
/// Publishes to Kafka (ADR 0013, slice 1).
/// </summary>
/// <remarks>
/// <para>
/// One producer for the host's lifetime: a Kafka producer is designed to be shared and batches
/// internally, and creating one per message would turn every publish into a connection handshake.
/// </para>
/// <para>
/// The publish is awaited rather than fired blind. A stub that claims to emit an event and quietly
/// fails to would be worse than one that never claimed it — the caller records the failure as a
/// sub-event, exactly as a failed webhook delivery is recorded.
/// </para>
/// </remarks>
public sealed class KafkaPublisher : IBrokerPublisher
{
    private readonly IProducer<string?, string?> _producer;

    public KafkaPublisher(string bootstrapServers)
    {
        _producer = new ProducerBuilder<string?, string?>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,

            // A mock is not a system of record: waiting for every replica would add latency to a test
            // suite for a durability guarantee nobody is relying on here. The leader's acknowledgement
            // is what "the broker accepted it" means for this purpose, and it is stated rather than
            // defaulted into.
            Acks = Acks.Leader,

            // Fail fast rather than blocking a served request for a minute: an unreachable broker
            // should surface as a recorded delivery failure while the response still goes out.
            MessageTimeoutMs = 5_000,
        }).Build();
    }

    /// <inheritdoc />
    public async Task PublishAsync(
        string topic,
        string? key,
        string? body,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        var message = new Message<string?, string?> { Key = key, Value = body };
        if (headers.Count > 0)
        {
            message.Headers = [];
            foreach (var (name, value) in headers)
            {
                message.Headers.Add(name, System.Text.Encoding.UTF8.GetBytes(value));
            }
        }

        await _producer.ProduceAsync(topic, message, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Flush before the process exits, or the last events a test suite asserted on are still sitting
        // in the producer's buffer when the host goes away.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
        return ValueTask.CompletedTask;
    }
}
