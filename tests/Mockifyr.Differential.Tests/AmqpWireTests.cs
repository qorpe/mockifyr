using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Testcontainers.RabbitMq;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Integration validation of the AMQP transport (ADR 0013, slice 4): publishing, capture and serve on
/// consume, against a <b>real RabbitMQ</b> driven by the official client.
/// </summary>
/// <remarks>
/// <para>
/// The point of the slice is that everything above the transport already worked: the mappings,
/// templates, inbox and admin routes are the ones slice 3 shipped, unchanged. So these tests assert
/// the transport's own decisions — how a topic becomes an exchange and routing key, that a partition
/// key survives as something a consumer can read, and that the capture ordering guarantee holds here
/// too — plus one end-to-end pass proving the stack above really is transport-free.
/// </para>
/// <para>Self-tested; no oracle has a broker concept. Requires Docker.</para>
/// </remarks>
public sealed class AmqpWireTests(RabbitFixture fixture) : IClassFixture<RabbitFixture>
{
    private readonly string _inbound = $"in-{Guid.NewGuid():N}";
    private readonly string _outbound = $"out-{Guid.NewGuid():N}";

    [Fact]
    public async Task An_http_stub_can_answer_and_publish_to_amqp()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await DeclareAsync(_outbound);

        using var created = await client.PostAsync("/__admin/mappings", Json(Mapping(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{
               "topic":"OUTBOUND","key":"pay-1","body":"{\"type\":\"PaymentAccepted\"}"}}]}
            """)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var served = await client.PostAsync("/payments", Json("""{"orderId":"ord-7"}"""));
        Assert.Equal(HttpStatusCode.Created, served.StatusCode);

        var delivered = await ReceiveAsync(_outbound);

        // A topic with no slash is the default exchange, which delivers straight to a queue of that
        // name — so the same stub JSON means the obvious thing on both transports.
        Assert.Equal("""{"type":"PaymentAccepted"}""", Encoding.UTF8.GetString(delivered.Body.Span));

        // A partition key has no AMQP counterpart, so it becomes MessageId rather than being dropped.
        Assert.Equal("pay-1", delivered.BasicProperties.MessageId);
    }

    [Fact]
    public async Task A_message_the_system_publishes_lands_in_the_inbox()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        await PublishAsync(_inbound, """{"type":"OrderSettled"}""", headers: [new("correlation-id", "abc")]);

        var message = await WaitForMessageAsync(client);

        // One inbox for every channel: the endpoints people already use answer for AMQP with nothing
        // new to learn, exactly as they do for Kafka.
        Assert.Equal("broker", message.GetProperty("channel").GetString());
        Assert.Equal(_inbound, message.GetProperty("from").GetString());

        var id = message.GetProperty("id").GetString();
        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/__admin/messages/{id}"));
        var meta = detail.RootElement.GetProperty("message").GetProperty("meta");

        // The queue stands in for the topic, and an AMQP header is decoded rather than stored as
        // "System.Byte[]" — which is what a matcher would otherwise be asked to match against.
        Assert.Equal(_inbound, meta.GetProperty("topic").GetString());
        Assert.Equal("abc", meta.GetProperty("header.correlation-id").GetString());
    }

    [Fact]
    public async Task An_inbound_message_produces_the_reply_its_mapping_declares()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await DeclareAsync(_outbound);

        // The same mapping shape slice 3 shipped, unchanged. That it works here without a line of
        // AMQP-specific matching is the point of the slice.
        using var registered = await client.PostAsync("/__admin/broker-mappings", Json(Mapping(
            """
            {"whenTopic":{"equalTo":"INBOUND"},
             "whenMessage":[{"matchesJsonPath":{"expression":"$.type","equalTo":"SettleOrder"}}],
             "publish":[{"topic":"OUTBOUND","body":"settled"}]}
            """)));
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        await PublishAsync(_inbound, """{"type":"SettleOrder","orderId":"ord-7"}""");

        Assert.Equal("settled", Encoding.UTF8.GetString((await ReceiveAsync(_outbound)).Body.Span));
    }

    [Fact]
    public async Task A_message_matching_no_mapping_is_acknowledged_rather_than_parked()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await DeclareAsync(_outbound);
        using var registered = await client.PostAsync("/__admin/broker-mappings", Json(Mapping(
            """{"whenMessage":[{"contains":"SettleOrder"}],"publish":[{"topic":"OUTBOUND","body":"settled"}]}""")));
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        // Unmatched first. If it stalled the queue the second would never be served — and a queue that
        // one forgotten mapping can block is worse than an unmatched request's 404.
        await PublishAsync(_inbound, "nothing matches this");
        await PublishAsync(_inbound, """{"type":"SettleOrder"}""");

        Assert.Equal("settled", Encoding.UTF8.GetString((await ReceiveAsync(_outbound)).Body.Span));
    }

    [Fact]
    public async Task A_producer_can_address_a_tenant()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        await PublishAsync(_inbound, "for acme", headers: [new("X-Mockifyr-Tenant", "acme")]);

        var acme = await WaitForMessageAsync(client, tenant: "acme");
        Assert.Equal("for acme", acme.GetProperty("body").GetString());

        // The default tenant does not see it. One tenancy rule across every channel, or teams share an
        // inbox they did not agree to share.
        Assert.Empty(await MessagesAsync(client));
    }

    [Fact]
    public async Task A_topic_can_name_an_exchange_and_a_routing_key()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        // Bound to the built-in topic exchange, which is the case a single-segment topic cannot express
        // and the reason the slash convention exists at all.
        var queue = $"bound-{Guid.NewGuid():N}";
        await BindAsync(queue, exchange: "amq.topic", routingKey: "order.settled");

        using var created = await client.PostAsync("/__admin/mappings", Json("""
            {"request":{"method":"GET","urlPath":"/emit"},
             "response":{"status":200},
             "postServeActions":[{"name":"publish","parameters":{
               "topic":"amq.topic/order.settled","body":"routed"}}]}
            """));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var served = await client.GetAsync("/emit");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        Assert.Equal("routed", Encoding.UTF8.GetString((await ReceiveAsync(queue)).Body.Span));
    }

    [Fact]
    public async Task An_unreachable_broker_is_recorded_and_never_takes_the_response_down()
    {
        // The 1.10.1 guarantee, on the second transport: the caller still gets its answer, and the
        // failure is on the journal entry with the message it was carrying.
        var app = MockifyrHost.Build(["--port", "0", "--amqp-uri", "amqp://guest:guest@127.0.0.1:5999/"]);
        await using (app)
        {
            await app.StartAsync();
            using var client = Client(app);

            using var created = await client.PostAsync("/__admin/mappings", Json("""
                {"request":{"method":"GET","urlPath":"/emit"},
                 "response":{"status":200},
                 "postServeActions":[{"name":"publish","parameters":{"topic":"nowhere","body":"lost"}}]}
                """));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            using var served = await client.GetAsync("/emit");
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);

            var id = await JournalIdAsync(client);
            using var detail = JsonDocument.Parse(await client.GetStringAsync($"/__admin/requests/{id}"));
            var publish = detail.RootElement.GetProperty("publishes").EnumerateArray().Single();

            Assert.False(publish.GetProperty("delivered").GetBoolean());
            Assert.Equal("lost", publish.GetProperty("body").GetString());

            await app.StopAsync();
        }
    }

    [Fact]
    public async Task A_host_with_a_broker_no_longer_warns_about_a_publish_action()
    {
        // The warning asks the container whether a publisher exists, not which transport it is — so
        // AMQP alone must satisfy it exactly as Kafka does.
        await using var app = await StartAsync();
        using var client = Client(app);

        using var created = await client.PostAsync("/__admin/mappings", Json("""
            {"request":{"method":"GET","urlPath":"/x"},"response":{"status":200},
             "postServeActions":[{"name":"publish","parameters":{"topic":"t","body":"b"}}]}
            """));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False(JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.TryGetProperty("warnings", out _));
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>Substitutes this test's queue names — Handlebars braces and C# interpolation do not mix.</summary>
    private string Mapping(string json) => json.Replace("INBOUND", _inbound).Replace("OUTBOUND", _outbound);

    private static async Task<string> JournalIdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/__admin/requests"));
        return document.RootElement.GetProperty("requests").EnumerateArray().First().GetProperty("id").GetString()!;
    }

    private async Task<IChannel> ChannelAsync()
    {
        var factory = new ConnectionFactory { Uri = new Uri(fixture.Container.GetConnectionString()) };
        var connection = await factory.CreateConnectionAsync();
        return await connection.CreateChannelAsync();
    }

    private async Task DeclareAsync(string queue)
    {
        var channel = await ChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
    }

    private async Task BindAsync(string queue, string exchange, string routingKey)
    {
        var channel = await ChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);
        await channel.QueueBindAsync(queue, exchange, routingKey);
    }

    private async Task PublishAsync(string queue, string body, KeyValuePair<string, string>[]? headers = null)
    {
        var channel = await ChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);

        var properties = new BasicProperties();
        if (headers is { Length: > 0 })
        {
            properties.Headers = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, value) in headers)
            {
                properties.Headers[name] = Encoding.UTF8.GetBytes(value);
            }
        }

        await channel.BasicPublishAsync(
            string.Empty, queue, mandatory: false, properties, Encoding.UTF8.GetBytes(body));
    }

    /// <summary>Waits for one message on a queue, polling because delivery is asynchronous.</summary>
    private async Task<BasicDeliverEventArgs> ReceiveAsync(string queue)
    {
        var channel = await ChannelAsync();
        for (var attempt = 0; attempt < 90; attempt++)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true) is { } result)
            {
                return new BasicDeliverEventArgs(
                    string.Empty, result.DeliveryTag, result.Redelivered,
                    result.Exchange, result.RoutingKey, result.BasicProperties, result.Body);
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException($"nothing arrived on {queue}");
    }

    private static async Task<JsonElement> WaitForMessageAsync(HttpClient client, string? tenant = null)
    {
        for (var attempt = 0; attempt < 90; attempt++)
        {
            var messages = await MessagesAsync(client, tenant);
            if (messages.Count > 0)
            {
                return messages[0];
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException("no message reached the inbox");
    }

    private static async Task<List<JsonElement>> MessagesAsync(HttpClient client, string? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/messages");
        if (tenant is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenant);
        }

        using var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. document.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone())];
    }

    private async Task<WebApplication> StartAsync()
    {
        var app = MockifyrHost.Build(
        [
            "--port", "0",
            "--amqp-uri", fixture.Container.GetConnectionString(),
            "--amqp-subscribe", _inbound,
        ]);

        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}

/// <summary>One RabbitMQ container shared by the AMQP suite — starting one per test is minutes wasted.</summary>
public sealed class RabbitFixture : IAsyncLifetime
{
    public RabbitMqContainer Container { get; } = new RabbitMqBuilder("rabbitmq:3.13-management").Build();

    public Task InitializeAsync() => Container.StartAsync();

    public async Task DisposeAsync() => await Container.DisposeAsync();
}
