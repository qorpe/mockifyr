using System.Net;
using System.Text;
using Confluent.Kafka;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;
using Testcontainers.Kafka;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Integration validation of the broker channel's first slice (ADR 0013): a stub that answers a
/// request <em>and</em> emits the event the rest of the system is waiting for.
/// </summary>
/// <remarks>
/// <para>
/// Against a <b>real Kafka</b> in a container, consumed with the official client. A fake broker
/// proving that a mock works would prove nothing: what has to be true is that a real consumer, which
/// knows nothing about Mockifyr, receives the message.
/// </para>
/// <para>
/// No oracle — the reference engine has no broker concept — so this is a self-test suite per the
/// standing rule. Requires Docker.
/// </para>
/// </remarks>
public sealed class BrokerPublishTests(KafkaFixture fixture) : IClassFixture<KafkaFixture>
{
    private KafkaContainer Kafka => fixture.Container;

    // One Kafka serves the whole class, so a fixed topic name would let one test read the message
    // another one published — and pass for the wrong reason.
    private readonly string _topic = $"t-{Guid.NewGuid():N}";

    [Fact]
    public async Task A_stub_answers_the_caller_and_emits_the_event()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client,
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201,"body":"{\"accepted\":true}"},
             "postServeActions":[{"name":"publish","parameters":{
               "topic":"{{TOPIC}}",
               "key":"{{jsonPath originalRequest.body '$.orderId'}}",
               "body":"{\"type\":\"PaymentAccepted\",\"order\":\"{{jsonPath originalRequest.body '$.orderId'}}\"}",
               "headers":{"correlation-id":"{{originalRequest.headers.X-Correlation-Id}}"}}}]}
            """);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = Json("""{"orderId":"ord-7"}"""),
        };
        request.Headers.Add("X-Correlation-Id", "corr-1");
        using var response = await client.SendAsync(request);

        // The caller gets its answer synchronously — the publish must not change that.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("""{"accepted":true}""", await response.Content.ReadAsStringAsync());

        var message = Consume(_topic);
        Assert.Equal("ord-7", message.Message.Key);
        Assert.Equal("""{"type":"PaymentAccepted","order":"ord-7"}""", message.Message.Value);
        Assert.Equal(
            "corr-1",
            Encoding.UTF8.GetString(message.Message.Headers.GetLastBytes("correlation-id")));
    }

    [Fact]
    public async Task A_stub_that_declares_no_publish_emits_nothing()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, """{"request":{"method":"GET","urlPath":"/quiet"},"response":{"status":200}}""");

        using (await client.GetAsync("/quiet")) { }

        // The default path must stay silent: a host with a broker configured still publishes only what
        // a stub asked it to.
        Assert.Null(TryConsume(_topic, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Several_publishes_all_reach_the_broker()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client,
            """
            {"request":{"method":"POST","urlPath":"/multi"},
             "response":{"status":202},
             "postServeActions":[
               {"name":"publish","parameters":{"topic":"{{TOPIC}}.a","body":"first"}},
               {"name":"publish","parameters":{"topic":"{{TOPIC}}.b","body":"second"}}]}
            """);

        using (await client.PostAsync("/multi", Json("{}"))) { }

        Assert.Equal("first", Consume($"{_topic}.a").Message.Value);
        Assert.Equal("second", Consume($"{_topic}.b").Message.Value);
    }

    [Fact]
    public async Task The_delivery_is_recorded_on_the_journal_entry()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client,
            """
            {"request":{"method":"POST","urlPath":"/recorded"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{"topic":"{{TOPIC}}","body":"{}"}}]}
            """);

        using (await client.PostAsync("/recorded", Json("{}"))) { }
        Consume(_topic);

        // A stub that claims to emit an event and quietly fails to would be worse than one that never
        // claimed it, so the delivery is visible where somebody debugging would look. The journal entry
        // is written before listeners fire, so poll rather than assume the sub-event has landed.
        var recorded = await PollAsync(async () =>
            (await JournalDetailAsync(client)).Contains("\"delivered\":true", StringComparison.Ordinal));

        var detail = await JournalDetailAsync(client);
        Assert.True(recorded, detail);
        Assert.Contains(_topic, detail);
    }

    [Fact]
    public async Task An_unreachable_broker_does_not_take_the_response_down_with_it()
    {
        // Pointed at a port with nothing behind it: the stub still answers, and the failure is recorded
        // rather than thrown. This is the case an operator hits on their first misconfigured run.
        await using var app = await StartAsync(bootstrap: "127.0.0.1:9");
        using var client = Client(app);
        await CreateAsync(client,
            """
            {"request":{"method":"POST","urlPath":"/unreachable"},
             "response":{"status":201,"body":"served anyway"},
             "postServeActions":[{"name":"publish","parameters":{"topic":"nowhere","body":"{}"}}]}
            """);

        using var response = await client.PostAsync("/unreachable", Json("{}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("served anyway", await response.Content.ReadAsStringAsync());

        // The producer's own timeout governs how long the failure takes to surface; poll rather than
        // assume it has already been recorded.
        // Asserted on what a reader actually sees in the journal, not on the internal sub-event name:
        // the contract here is "the journal shows this went nowhere and says why".
        var recorded = await PollAsync(async () =>
            (await JournalDetailAsync(client)).Contains("\"delivered\":false", StringComparison.Ordinal));

        var detail = await JournalDetailAsync(client);
        Assert.True(recorded, detail);
        Assert.Contains("\"topic\":\"nowhere\"", detail);
        Assert.DoesNotContain("\"error\":null", detail);
    }

    private async Task<bool> PollAsync(Func<Task<bool>> satisfied)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await satisfied())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private static async Task<string> JournalDetailAsync(HttpClient client)
    {
        using var listing = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/__admin/requests"));
        var id = listing.RootElement.GetProperty("requests")[0].GetProperty("id").GetString();
        return await client.GetStringAsync($"/__admin/requests/{id}");
    }

    private ConsumeResult<string, string> Consume(string topic) =>
        TryConsume(topic, TimeSpan.FromSeconds(30))
        ?? throw new InvalidOperationException($"nothing arrived on '{topic}'");

    private ConsumeResult<string, string>? TryConsume(string topic, TimeSpan timeout)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = Kafka.GetBootstrapAddress(),
            GroupId = $"test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();

        consumer.Subscribe(topic);
        try
        {
            // A topic is auto-created by the first produce, and its metadata takes a moment to reach a
            // consumer. A single Consume() therefore throws "unknown topic" on a race rather than
            // waiting — so poll until the deadline and treat that particular error as "not yet".
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (consumer.Consume(TimeSpan.FromMilliseconds(500)) is { } result)
                    {
                        return result;
                    }
                }
                catch (ConsumeException exception)
                    when (exception.Error.Code is ErrorCode.UnknownTopicOrPart)
                {
                    Thread.Sleep(250);
                }
            }

            return null;
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task CreateAsync(HttpClient client, string stubJson)
    {
        using var response = await client.PostAsync(
            "/__admin/mappings", Json(stubJson.Replace("{{TOPIC}}", _topic, StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<WebApplication> StartAsync(string? bootstrap = null)
    {
        var app = MockifyrHost.Build(
            ["--port", "0", "--kafka-bootstrap", bootstrap ?? Kafka.GetBootstrapAddress()]);
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

/// <summary>
/// One Kafka for the whole class. A container per test would mean five broker startups for five
/// assertions, which is minutes of CI spent proving the same daemon boots.
/// </summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    public KafkaContainer Container { get; } = new KafkaBuilder("confluentinc/cp-kafka:7.6.1").Build();

    public Task InitializeAsync() => Container.StartAsync();

    public async Task DisposeAsync() => await Container.DisposeAsync();
}
