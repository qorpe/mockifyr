using Mockifyr.Core;
using Mockifyr.Facade.Broker;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for broker capture (ADR 0013, slice 2): which tenant a consumed message belongs
/// to and what the inbox ends up holding — decided here, so it is assertable without a broker.
/// Self-tested; no oracle has this concept.
/// </summary>
public sealed class BrokerCaptureTests
{
    private static readonly DateTimeOffset Received = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static ConsumedRecord Record(
        string topic = "orders.events",
        string? key = null,
        string? value = """{"type":"OrderSettled"}""",
        int partition = 0,
        long offset = 41,
        params KeyValuePair<string, string>[] headers) =>
        new(topic, key, value, partition, offset, headers);

    [Fact]
    public void A_consumed_message_becomes_a_broker_channel_envelope()
    {
        var envelope = BrokerMessageFactory.Build(Record(), Received);

        // One inbox for every channel (ADR 0013) is what makes "assert we emitted OrderSettled" the
        // call people already know.
        Assert.Equal(MessageChannel.Broker, envelope.Channel);
        Assert.Equal("""{"type":"OrderSettled"}""", envelope.Body);
        Assert.Equal(Received, envelope.ReceivedAt);
    }

    [Fact]
    public void The_topic_stands_in_for_the_sender()
    {
        // A broker message has no sender, and the topic is what somebody scanning the inbox is looking
        // for. Leaving From empty would make every broker row look identical in a listing.
        Assert.Equal("orders.events", BrokerMessageFactory.Build(Record(), Received).From);
    }

    [Fact]
    public void There_are_no_recipients()
    {
        // A published message is addressed to a topic, not to anybody. Inventing a consumer group here
        // would suggest a delivery guarantee the inbox does not make.
        Assert.Empty(BrokerMessageFactory.Build(Record(), Received).To);
    }

    [Fact]
    public void The_email_only_fields_stay_null()
    {
        var envelope = BrokerMessageFactory.Build(Record(), Received);

        Assert.Null(envelope.Subject);
        Assert.Null(envelope.HtmlBody);
        Assert.Empty(envelope.Attachments);
    }

    [Fact]
    public void Where_the_message_came_from_is_recoverable()
    {
        var envelope = BrokerMessageFactory.Build(Record(partition: 3, offset: 4711, key: "ord-7"), Received);

        // Topic, partition and offset are what turn "a message arrived" into "this exact message
        // arrived", which is the difference between a demo and a debugging session.
        Assert.Equal("orders.events", envelope.Meta["topic"]);
        Assert.Equal("3", envelope.Meta["partition"]);
        Assert.Equal("4711", envelope.Meta["offset"]);
        Assert.Equal("ord-7", envelope.Meta["key"]);
    }

    [Fact]
    public void A_message_with_no_key_carries_none_rather_than_an_empty_one()
    {
        // "The producer set no key" and "the producer set an empty key" are different facts.
        Assert.False(BrokerMessageFactory.Build(Record(key: null), Received).Meta.ContainsKey("key"));
        Assert.False(BrokerMessageFactory.Build(Record(key: ""), Received).Meta.ContainsKey("key"));
    }

    [Fact]
    public void Headers_are_prefixed_so_they_cannot_overwrite_the_facts()
    {
        var envelope = BrokerMessageFactory.Build(
            Record(headers: [new("correlation-id", "abc"), new("topic", "a lie"), new("offset", "0")]),
            Received);

        // A producer must not be able to rewrite the topic or offset an operator is reading. The prefix
        // is what makes those three trustworthy rather than merely usually right.
        Assert.Equal("orders.events", envelope.Meta["topic"]);
        Assert.Equal("41", envelope.Meta["offset"]);
        Assert.Equal("abc", envelope.Meta["header.correlation-id"]);
        Assert.Equal("a lie", envelope.Meta["header.topic"]);
    }

    [Fact]
    public void A_null_payload_is_stored_as_an_empty_body()
    {
        // A tombstone is a real Kafka message. Storing null would push the null into every consumer of
        // the inbox instead of stopping here.
        Assert.Equal(string.Empty, BrokerMessageFactory.Build(Record(value: null), Received).Body);
    }

    [Fact]
    public void A_message_with_no_tenant_header_belongs_to_the_default_tenant()
    {
        // A topic carries no tenancy of its own, and a single-tenant host must find its messages where
        // it already looks for everything else.
        Assert.Equal(TenantId.Default, BrokerMessageFactory.TenantOf(Record()));
    }

    [Fact]
    public void A_tenant_header_addresses_a_tenant()
    {
        var record = Record(headers: [new(BrokerMessageFactory.TenantHeader, "acme")]);

        Assert.Equal(new TenantId("acme"), BrokerMessageFactory.TenantOf(record));
    }

    [Fact]
    public void The_tenant_header_is_matched_without_regard_to_case()
    {
        // Header names are case-insensitive everywhere else in this product; a producer spelling it
        // lowercase must not silently land in the wrong tenant.
        var record = Record(headers: [new("x-mockifyr-tenant", "acme")]);

        Assert.Equal(new TenantId("acme"), BrokerMessageFactory.TenantOf(record));
    }

    [Fact]
    public void A_blank_tenant_header_falls_back_rather_than_creating_an_empty_tenant()
    {
        Assert.Equal(TenantId.Default, BrokerMessageFactory.TenantOf(Record(headers: [new(BrokerMessageFactory.TenantHeader, "  ")])));
    }

    [Fact]
    public void The_first_tenant_header_wins_when_a_producer_sets_two()
    {
        // Kafka allows repeated header names. Picking deterministically beats picking whichever the
        // client happened to enumerate last.
        var record = Record(headers:
        [
            new(BrokerMessageFactory.TenantHeader, "first"),
            new(BrokerMessageFactory.TenantHeader, "second"),
        ]);

        Assert.Equal(new TenantId("first"), BrokerMessageFactory.TenantOf(record));
    }
}
