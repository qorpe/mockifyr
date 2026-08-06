using Mockifyr.Facade.Broker;

namespace Mockifyr.Application.Tests;

/// <summary>
/// The pure decisions AMQP support rests on (ADR 0013, slice 4): how a topic becomes an AMQP
/// destination, and which transport a message goes to on a host that has two.
/// Self-tested; no oracle has a broker concept at all.
/// </summary>
public sealed class AmqpRoutingTests
{
    [Fact]
    public void A_topic_with_no_slash_publishes_to_the_default_exchange()
    {
        // Which delivers straight to a queue of that name — so {"topic":"orders.events"} means the
        // obvious thing on both transports, which is the whole point of having one dialect.
        var (exchange, routingKey) = AmqpPublisher.Split("orders.events");

        Assert.Equal(string.Empty, exchange);
        Assert.Equal("orders.events", routingKey);
    }

    [Fact]
    public void A_slash_names_an_exchange_and_a_routing_key()
    {
        var (exchange, routingKey) = AmqpPublisher.Split("amq.topic/order.settled");

        Assert.Equal("amq.topic", exchange);
        Assert.Equal("order.settled", routingKey);
    }

    [Fact]
    public void Only_the_first_slash_splits()
    {
        // AMQP routing keys are dotted by convention but a slash is legal in one, and losing part of
        // the key would route the message somewhere quietly wrong.
        var (exchange, routingKey) = AmqpPublisher.Split("events/a/b/c");

        Assert.Equal("events", exchange);
        Assert.Equal("a/b/c", routingKey);
    }

    [Fact]
    public void A_leading_slash_is_the_default_exchange_with_the_rest_as_the_key()
    {
        var (exchange, routingKey) = AmqpPublisher.Split("/orders.events");

        Assert.Equal(string.Empty, exchange);
        Assert.Equal("orders.events", routingKey);
    }

    [Fact]
    public void A_trailing_slash_is_an_exchange_with_an_empty_key()
    {
        // A fanout exchange ignores the routing key, so this is a real thing to write rather than a
        // mistake to reject.
        var (exchange, routingKey) = AmqpPublisher.Split("logs/");

        Assert.Equal("logs", exchange);
        Assert.Equal(string.Empty, routingKey);
    }

    private sealed class Recorder : IBrokerPublisher
    {
        public List<string> Topics { get; } = [];

        public Task PublishAsync(
            string topic,
            string? key,
            string? body,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            CancellationToken cancellationToken)
        {
            Topics.Add(topic);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void With_one_transport_every_message_goes_to_it_prefix_or_not()
    {
        // The case that must not regress: a host with only Kafka behaves exactly as it did before AMQP
        // existed, and a mapping written for an AMQP host still runs on it.
        var kafka = new Recorder();
        var router = new BrokerRouter(kafka, amqp: null);

        Assert.Same(kafka, router.Route("orders.events").Publisher);
        Assert.Same(kafka, router.Route("kafka:orders.events").Publisher);
        Assert.Same(kafka, router.Route("amqp:orders.events").Publisher);
    }

    [Fact]
    public void A_prefix_is_stripped_so_the_transport_never_sees_it()
    {
        var router = new BrokerRouter(new Recorder(), new Recorder());

        Assert.Equal("orders.events", router.Route("kafka:orders.events").Topic);
        Assert.Equal("amq.topic/order.settled", router.Route("amqp:amq.topic/order.settled").Topic);
        Assert.Equal("orders.events", router.Route("orders.events").Topic);
    }

    [Fact]
    public void With_two_transports_the_prefix_decides_and_kafka_is_the_default()
    {
        var kafka = new Recorder();
        var amqp = new Recorder();
        var router = new BrokerRouter(kafka, amqp);

        Assert.Same(amqp, router.Route("amqp:orders.events").Publisher);
        Assert.Same(kafka, router.Route("kafka:orders.events").Publisher);

        // An unprefixed topic on a two-broker host has to go somewhere; the first configured transport
        // is the answer, and the guide says so rather than leaving it to be discovered.
        Assert.Same(kafka, router.Route("orders.events").Publisher);
    }

    [Fact]
    public void A_prefix_is_recognised_without_regard_to_case()
    {
        var amqp = new Recorder();

        Assert.Same(amqp, new BrokerRouter(new Recorder(), amqp).Route("AMQP:orders.events").Publisher);
    }

    [Fact]
    public void A_topic_that_only_looks_like_a_prefix_is_left_alone()
    {
        // "kafkaesque.events" starts with "kafka" and is not a prefix. Matching the colon is what keeps
        // a legitimate topic from being silently truncated.
        var kafka = new Recorder();

        Assert.Equal("kafkaesque.events", new BrokerRouter(kafka, new Recorder()).Route("kafkaesque.events").Topic);
    }

    [Fact]
    public async Task A_routed_message_reaches_the_transport_it_named()
    {
        var kafka = new Recorder();
        var amqp = new Recorder();
        var router = new BrokerRouter(kafka, amqp);

        await router.PublishAsync("amqp:orders.events", null, "x", [], CancellationToken.None);
        await router.PublishAsync("orders.commands", null, "x", [], CancellationToken.None);

        Assert.Equal(["orders.events"], amqp.Topics);
        Assert.Equal(["orders.commands"], kafka.Topics);
    }

    [Fact]
    public void A_router_over_nothing_is_a_programming_error_not_a_silent_no_op()
    {
        // Publishing into a router with no transport would look like a delivery that worked.
        var thrown = Assert.Throws<ArgumentException>(() => new BrokerRouter(null, null));

        // The message is the whole value of throwing here — this cannot reach a user, only whoever
        // wires the host, and "a router needs at least one transport" is the fix.
        Assert.Contains("transport", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }
}
