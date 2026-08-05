using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;
using Testcontainers.Kafka;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Integration validation of broker capture (ADR 0013, slice 2): messages the system under test
/// publishes land in the tenant's inbox, so "assert we emitted <c>OrderSettled</c>" is one call against
/// the surface people already query.
/// </summary>
/// <remarks>
/// Produced with the <b>official Kafka client</b> against a real broker, exactly as the system under
/// test would. Self-tested; no oracle has this concept. Requires Docker.
/// </remarks>
public sealed class BrokerCaptureWireTests(KafkaFixture fixture) : IClassFixture<KafkaFixture>
{
    private readonly string _topic = $"c-{Guid.NewGuid():N}";

    [Fact]
    public async Task A_message_the_system_publishes_lands_in_the_inbox()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        await ProduceAsync(_topic, """{"type":"OrderSettled","orderId":"ord-7"}""", key: "ord-7");

        var message = await WaitForMessageAsync(client);

        Assert.Equal("broker", message.GetProperty("channel").GetString());
        Assert.Equal(_topic, message.GetProperty("from").GetString());
        Assert.Contains("OrderSettled", message.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_broker_message_is_never_reported_as_an_sms()
    {
        // The channel projection used to be a two-way ternary, which would have labelled every broker
        // message "sms" the moment a third channel existed. Cheap to assert, and it would have shipped.
        await using var app = await StartAsync();
        using var client = Client(app);
        await ProduceAsync(_topic, "hello");

        var message = await WaitForMessageAsync(client);

        Assert.Equal("broker", message.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Where_the_message_came_from_survives_into_the_inbox()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ProduceAsync(_topic, "payload", key: "k-1", headers: [new("correlation-id", "abc")]);

        var id = (await WaitForMessageAsync(client)).GetProperty("id").GetString();
        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/__admin/messages/{id}"));
        var meta = detail.RootElement.GetProperty("message").GetProperty("meta");

        // Topic, partition and offset are what turn "a message arrived" into "this exact one did".
        Assert.Equal(_topic, meta.GetProperty("topic").GetString());
        Assert.Equal("k-1", meta.GetProperty("key").GetString());
        Assert.Equal("abc", meta.GetProperty("header.correlation-id").GetString());
        Assert.True(meta.TryGetProperty("offset", out _));
    }

    [Fact]
    public async Task A_producer_can_address_a_tenant()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        await ProduceAsync(_topic, "for acme", headers: [new("X-Mockifyr-Tenant", "acme")]);

        // The tenant the header names sees it; the default tenant does not. A capture that ignored the
        // header would put every team's events in one inbox.
        var acme = await WaitForMessageAsync(client, tenant: "acme");
        Assert.Equal("for acme", acme.GetProperty("body").GetString());
        Assert.Empty(await MessagesAsync(client));
    }

    [Fact]
    public async Task Verification_works_on_captured_messages_with_no_new_api()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ProduceAsync(_topic, """{"type":"OrderSettled"}""");
        await WaitForMessageAsync(client);

        using var response = await client.GetAsync("/__admin/messages/count?channel=broker");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The point of one inbox (ADR 0013): the count endpoint that already existed answers for broker
        // messages without learning anything new.
        Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_host_that_subscribes_to_nothing_captures_nothing()
    {
        await using var app = await StartAsync(subscribe: false);
        using var client = Client(app);

        await ProduceAsync(_topic, "ignored");
        await Task.Delay(3000);

        // Publishing is configured, capture is not: a host must not join a consumer group it was never
        // asked to join.
        Assert.Empty(await MessagesAsync(client));
    }

    private async Task ProduceAsync(
        string topic, string value, string? key = null, KeyValuePair<string, string>[]? headers = null)
    {
        using var producer = new ProducerBuilder<string?, string?>(new ProducerConfig
        {
            BootstrapServers = fixture.Container.GetBootstrapAddress(),
        }).Build();

        var message = new Message<string?, string?> { Key = key, Value = value };
        if (headers is { Length: > 0 })
        {
            message.Headers = [];
            foreach (var (name, headerValue) in headers)
            {
                message.Headers.Add(name, Encoding.UTF8.GetBytes(headerValue));
            }
        }

        await producer.ProduceAsync(topic, message);
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private static async Task<JsonElement> WaitForMessageAsync(HttpClient client, string? tenant = null)
    {
        for (var attempt = 0; attempt < 60; attempt++)
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

    private async Task<WebApplication> StartAsync(bool subscribe = true)
    {
        string[] args = subscribe
            ? ["--port", "0", "--kafka-bootstrap", fixture.Container.GetBootstrapAddress(),
               "--kafka-subscribe", _topic, "--kafka-group", $"g-{Guid.NewGuid():N}"]
            : ["--port", "0", "--kafka-bootstrap", fixture.Container.GetBootstrapAddress()];

        var app = MockifyrHost.Build(args);
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
