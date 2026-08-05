using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;
using Mockifyr.Facade.Broker;
using Mockifyr.Templating;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for the <c>publish</c> post-serve action (ADR 0013, slice 1): what gets parsed
/// from a mapping, and what the listener would send — without a broker anywhere near it. The wire
/// behaviour against a real Kafka is proven separately in <c>BrokerPublishTests</c>.
/// </summary>
public sealed class PublishActionTests
{
    /// <summary>Records what would have gone to a broker, so the plan is assertable without one.</summary>
    private sealed class RecordingPublisher : IBrokerPublisher
    {
        public List<(string Topic, string? Key, string? Body, IReadOnlyList<KeyValuePair<string, string>> Headers)> Sent { get; } = [];

        public Exception? Throw { get; set; }

        public Task PublishAsync(
            string topic, string? key, string? body,
            IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
        {
            if (Throw is { } failure)
            {
                return Task.FromException(failure);
            }

            Sent.Add((topic, key, body, headers));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static StubMapping Read(string mappingJson) => MappingJsonReader.Read(mappingJson, TenantId.Default)[0];

    private static ServeEvent ServeEventFor(StubMapping stub, string body = """{"orderId":"ord-7"}""") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantId.Default,
        Request = CanonicalRequestBuilder.Build(
            "POST", "/payments", [new("X-Correlation-Id", "abc")], System.Text.Encoding.UTF8.GetBytes(body)),
        MatchedStub = stub,
        Timestamp = DateTimeOffset.UnixEpoch,
    };

    private static async Task<RecordingPublisher> RunAsync(StubMapping stub, string? body = null)
    {
        var publisher = new RecordingPublisher();
        var listener = new PublishServeEventListener(publisher, new WebhookTemplateRenderer());
        await listener.OnServeEventAsync(
            body is null ? ServeEventFor(stub) : ServeEventFor(stub, body), CancellationToken.None);
        return publisher;
    }

    [Fact]
    public async Task A_stub_can_answer_and_emit_in_one_mapping()
    {
        // The headline case for the whole channel: 201 to the caller, an event to everyone else.
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{
               "topic":"payments.events","body":"{\"type\":\"PaymentAccepted\"}"}}]}
            """);

        Assert.Equal(201, stub.Response.Status);
        var sent = Assert.Single((await RunAsync(stub)).Sent);
        Assert.Equal("payments.events", sent.Topic);
        Assert.Equal("""{"type":"PaymentAccepted"}""", sent.Body);
    }

    [Fact]
    public async Task Every_field_is_templated_against_the_triggering_request()
    {
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{
               "topic":"payments.events",
               "key":"{{jsonPath originalRequest.body '$.orderId'}}",
               "body":"{\"order\":\"{{jsonPath originalRequest.body '$.orderId'}}\"}",
               "headers":{"correlation-id":"{{originalRequest.headers.X-Correlation-Id}}"}}}]}
            """);

        var sent = Assert.Single((await RunAsync(stub)).Sent);

        // A key taken from the request body is what makes ordering per entity work, so it has to be
        // templated rather than fixed.
        Assert.Equal("ord-7", sent.Key);
        Assert.Equal("""{"order":"ord-7"}""", sent.Body);
        Assert.Equal("abc", sent.Headers.Single().Value);
    }

    [Fact]
    public async Task Several_publishes_all_fire_in_order()
    {
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[
               {"name":"publish","parameters":{"topic":"first"}},
               {"name":"publish","parameters":{"topic":"second"}}]}
            """);

        Assert.Equal(["first", "second"], (await RunAsync(stub)).Sent.Select(s => s.Topic));
    }

    [Fact]
    public async Task A_publish_sits_beside_a_webhook_rather_than_replacing_it()
    {
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[
               {"name":"webhook","parameters":{"method":"POST","url":"http://example.com/hook"}},
               {"name":"publish","parameters":{"topic":"payments.events"}}]}
            """);

        // Both live in the same array. Reading one must not consume or hide the other — a stub that
        // silently lost its webhook when somebody added a publish would be a nasty surprise.
        Assert.Single(stub.Webhooks);
        Assert.Single(stub.Publishes);
        Assert.Equal("payments.events", Assert.Single((await RunAsync(stub)).Sent).Topic);
    }

    [Fact]
    public void The_3x_action_array_is_read_too()
    {
        // serveEventListeners is the newer spelling of the same array; publishes must be found in both
        // for the same reason webhooks are (#147).
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "serveEventListeners":[{"name":"publish","parameters":{"topic":"payments.events"}}]}
            """);

        Assert.Single(stub.Publishes);
    }

    [Fact]
    public void A_publish_without_a_topic_is_not_a_publish()
    {
        // Nowhere to send it. Accepting the action and dropping the message at delivery time would make
        // a typo look like a working stub.
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{"body":"{}"}}]}
            """);

        Assert.Empty(stub.Publishes);
    }

    [Fact]
    public async Task A_stub_that_declares_nothing_publishes_nothing()
    {
        var stub = Read("""{"request":{"method":"GET","urlPath":"/x"},"response":{"status":200}}""");

        Assert.Empty((await RunAsync(stub)).Sent);
    }

    [Fact]
    public async Task A_delivery_failure_is_recorded_and_never_thrown()
    {
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{"topic":"payments.events"}}]}
            """);

        var publisher = new RecordingPublisher { Throw = new InvalidOperationException("broker is down") };
        var listener = new PublishServeEventListener(publisher, new WebhookTemplateRenderer());
        var serveEvent = ServeEventFor(stub);

        // The client already has its 201 by the time this runs. An unreachable broker must land in the
        // journal, not take the served response down with it.
        await listener.OnServeEventAsync(serveEvent, CancellationToken.None);

        var recorded = Assert.Single(serveEvent.SubEvents);
        Assert.Equal(PublishServeEventListener.FailedType, recorded.Type);
        Assert.Contains("broker is down", ((PublishErrorData)recorded.Data!).Error);
    }

    [Fact]
    public async Task A_delivered_message_is_recorded_so_the_journal_can_show_it()
    {
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{"topic":"payments.events","body":"{}"}}]}
            """);

        var serveEvent = ServeEventFor(stub);
        await new PublishServeEventListener(new RecordingPublisher(), new WebhookTemplateRenderer())
            .OnServeEventAsync(serveEvent, CancellationToken.None);

        var recorded = Assert.Single(serveEvent.SubEvents);
        Assert.Equal(PublishServeEventListener.PublishedType, recorded.Type);
        Assert.Equal("payments.events", ((PublishData)recorded.Data!).Topic);
    }

    [Fact]
    public async Task A_template_that_cannot_render_is_recorded_rather_than_thrown()
    {
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{
               "topic":"t","body":"{{#each items}}unclosed"}}]}
            """);

        var serveEvent = ServeEventFor(stub);
        var publisher = new RecordingPublisher();
        await new PublishServeEventListener(publisher, new WebhookTemplateRenderer())
            .OnServeEventAsync(serveEvent, CancellationToken.None);

        // Nothing was sent, and the reason is on the serve event rather than in a swallowed exception.
        Assert.Empty(publisher.Sent);
        Assert.Equal(PublishServeEventListener.FailedType, Assert.Single(serveEvent.SubEvents).Type);
    }

    [Fact]
    public void A_declared_delay_is_carried_through()
    {
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{"topic":"t","delay":250}}]}
            """);

        Assert.Equal(250, stub.Publishes[0].DelayMilliseconds);
    }

    [Fact]
    public void Headers_keep_their_declaration_order()
    {
        var stub = Read(
            """
            {"request":{"method":"POST","urlPath":"/payments"},
             "response":{"status":201},
             "postServeActions":[{"name":"publish","parameters":{"topic":"t",
               "headers":{"b":"2","a":"1"}}}]}
            """);

        Assert.Equal(["b", "a"], stub.Publishes[0].Headers.Select(h => h.Key));
    }
}
