using System.Net;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Integration validation of serve on consume (ADR 0013, slice 3): a message arrives on a topic, a
/// broker mapping matches it, and the reply reaches a <b>real consumer</b>.
/// </summary>
/// <remarks>
/// <para>
/// The reply is read back with the official client rather than from the host's own state, because the
/// question this slice answers is not "did we plan a message" — the unit tests settle that — but "did
/// the system under test actually receive one". A fake broker proving a mock works proves nothing.
/// </para>
/// <para>Self-tested; no oracle has this concept. Requires Docker.</para>
/// </remarks>
public sealed class BrokerServeWireTests(KafkaFixture fixture) : IClassFixture<KafkaFixture>
{
    private readonly string _inbound = $"in-{Guid.NewGuid():N}";
    private readonly string _outbound = $"out-{Guid.NewGuid():N}";

    [Fact]
    public async Task An_inbound_command_produces_the_event_a_real_consumer_receives()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        // Handlebars braces and C# interpolation do not mix, so the topics are substituted rather than
        // interpolated — the mapping below is otherwise exactly what a user would write.
        await RegisterAsync(client, Mapping(
            """
            {"whenTopic":{"equalTo":"INBOUND"},
             "whenMessage":[{"matchesJsonPath":{"expression":"$.type","equalTo":"SettleOrder"}}],
             "publish":[{"topic":"OUTBOUND",
                         "key":"{{jsonPath message.body '$.orderId'}}",
                         "body":"{\"type\":\"OrderSettled\",\"orderId\":\"{{jsonPath message.body '$.orderId'}}\"}",
                         "headers":{"origin":"{{message.topic}}"}}]}
            """));

        await ProduceAsync(_inbound, """{"type":"SettleOrder","orderId":"ord-7"}""");

        var reply = await ConsumeAsync(_outbound);

        // The whole slice in four assertions: it arrived, it carried the field across, the partition key
        // came from the payload, and the reply can say where it came from.
        Assert.Equal("""{"type":"OrderSettled","orderId":"ord-7"}""", reply.Message.Value);
        Assert.Equal("ord-7", reply.Message.Key);
        Assert.Equal(
            _inbound,
            Encoding.UTF8.GetString(reply.Message.Headers.GetLastBytes("origin")));
    }

    [Fact]
    public async Task The_inbound_message_is_still_captured_while_being_served()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await RegisterAsync(client, Mapping("""{"publish":[{"topic":"OUTBOUND","body":"replied"}]}"""));

        await ProduceAsync(_inbound, "a command");
        await ConsumeAsync(_outbound);

        // Capture and serving are not alternatives. A message that produced a reply must still be
        // assertable afterwards, or debugging a mapping means guessing what arrived.
        using var response = await client.GetAsync("/__admin/messages?channel=broker");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "a command",
            document.RootElement.GetProperty("messages").EnumerateArray().Single().GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_message_matching_no_mapping_is_acknowledged_rather_than_parked()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await RegisterAsync(client, Mapping(
            """{"whenMessage":[{"contains":"SettleOrder"}],"publish":[{"topic":"OUTBOUND","body":"replied"}]}"""));

        // An unmatched message first, then a matched one. If the unmatched message stalled the
        // partition, the second would never be served — which ADR 0013 rules out for the same reason an
        // unmatched HTTP request is a 404 and not a hang.
        await ProduceAsync(_inbound, "nothing matches this");
        await ProduceAsync(_inbound, "SettleOrder please");

        Assert.Equal("replied", (await ConsumeAsync(_outbound)).Message.Value);
    }

    [Fact]
    public async Task A_mapping_belongs_to_the_tenant_that_registered_it()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await RegisterAsync(client, Mapping("""{"publish":[{"topic":"OUTBOUND","body":"acme only"}]}"""), tenant: "acme");

        // The producer addresses the default tenant, whose mappings are empty — so nothing is served.
        await ProduceAsync(_inbound, "a command");
        Assert.Null(await TryConsumeAsync(_outbound, TimeSpan.FromSeconds(6)));

        // The same message addressed to acme is served by acme's mapping. Tenant isolation is the
        // invariant no broker can check for us, and getting it wrong leaks between teams.
        await ProduceAsync(_inbound, "a command", headers: [new("X-Mockifyr-Tenant", "acme")]);
        Assert.Equal("acme only", (await ConsumeAsync(_outbound)).Message.Value);
    }

    [Fact]
    public async Task A_registered_mapping_can_be_listed_and_deleted()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        var id = await RegisterAsync(client, Mapping("""{"publish":[{"topic":"OUTBOUND","body":"replied"}]}"""));

        using (var listed = await client.GetAsync("/__admin/broker-mappings"))
        {
            using var document = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
            var mapping = document.RootElement.GetProperty("mappings").EnumerateArray().Single();

            // The registration JSON verbatim, so a reader can search their own file for what we printed.
            Assert.Contains(_outbound, mapping.GetProperty("source").GetString()!, StringComparison.Ordinal);
        }

        using (var deleted = await client.DeleteAsync($"/__admin/broker-mappings/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        // Deleted means it stops serving, not merely that it stops being listed.
        await ProduceAsync(_inbound, "a command");
        Assert.Null(await TryConsumeAsync(_outbound, TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public async Task A_malformed_mapping_is_refused_rather_than_stored()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.PostAsync(
            "/__admin/broker-mappings", new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_host_with_no_broker_exposes_no_broker_mapping_routes()
    {
        // A route that accepts a mapping which will never be evaluated is a trap, so it does not exist.
        var app = MockifyrHost.Build(["--port", "0"]);
        await using (app)
        {
            await app.StartAsync();
            using var client = Client(app);

            using var response = await client.PostAsync(
                "/__admin/broker-mappings", new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await app.StopAsync();
        }
    }

    /// <summary>Substitutes this test's topic names into a mapping written as a user would write it.</summary>
    private string Mapping(string json) => json.Replace("INBOUND", _inbound).Replace("OUTBOUND", _outbound);

    private static async Task<string> RegisterAsync(HttpClient client, string json, string? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/broker-mappings")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (tenant is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenant);
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private async Task ProduceAsync(string topic, string value, KeyValuePair<string, string>[]? headers = null)
    {
        using var producer = new ProducerBuilder<string?, string?>(new ProducerConfig
        {
            BootstrapServers = fixture.Container.GetBootstrapAddress(),
        }).Build();

        var message = new Message<string?, string?> { Value = value };
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

    private async Task<ConsumeResult<string?, string?>> ConsumeAsync(string topic) =>
        await TryConsumeAsync(topic, TimeSpan.FromSeconds(45))
        ?? throw new InvalidOperationException($"nothing arrived on {topic}");

    /// <summary>Consumes one message, or null within the budget. Tolerates a topic auto-creation race.</summary>
    private async Task<ConsumeResult<string?, string?>?> TryConsumeAsync(string topic, TimeSpan budget)
    {
        using var consumer = new ConsumerBuilder<string?, string?>(new ConsumerConfig
        {
            BootstrapServers = fixture.Container.GetBootstrapAddress(),
            GroupId = $"assert-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();

        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // A single Consume() throws while the topic is still being auto-created, so this polls
                // rather than asserting on the first attempt — the failure would look like a serving
                // bug and would be a race.
                if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is { Message: not null } result)
                {
                    consumer.Close();
                    return result;
                }
            }
            catch (ConsumeException exception) when (exception.Error.Code == ErrorCode.UnknownTopicOrPart)
            {
                await Task.Delay(200);
            }
        }

        consumer.Close();
        return null;
    }

    private async Task<WebApplication> StartAsync()
    {
        var app = MockifyrHost.Build(
        [
            "--port", "0",
            "--kafka-bootstrap", fixture.Container.GetBootstrapAddress(),
            "--kafka-subscribe", _inbound,
            "--kafka-group", $"g-{Guid.NewGuid():N}",
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
