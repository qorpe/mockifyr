using Mockifyr.Core;
using Mockifyr.Facade.Broker;
using Mockifyr.Templating;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Serve on consume (ADR 0013, slice 3): which broker mappings a message matches, and what they emit.
/// Self-tested; no oracle has this concept — the reference engine has no broker channel at all.
/// </summary>
/// <remarks>
/// Every decision worth asserting is made before a broker is involved, which is why these run without
/// one. The Kafka container proves the wiring; this proves the behaviour.
/// </remarks>
public sealed class BrokerMappingTests
{
    private static ConsumedRecord Record(
        string topic = "orders.commands",
        string? key = "ord-7",
        string? value = """{"type":"SettleOrder","orderId":"ord-7"}""",
        params KeyValuePair<string, string>[] headers) =>
        new(topic, key, value, 0, 41, headers);

    private static BrokerMapping Read(string json) => BrokerMappingReader.Read(json, TenantId.Default);

    private static (BrokerMappingPlanner Planner, BrokerMappingStore Store) Planner()
    {
        var store = new BrokerMappingStore();
        return (new BrokerMappingPlanner(store, new MessageTemplateRenderer()), store);
    }

    private const string SettleMapping =
        """
        {"whenTopic":{"equalTo":"orders.commands"},
         "whenMessage":[{"matchesJsonPath":{"expression":"$.type","equalTo":"SettleOrder"}}],
         "publish":[{"topic":"orders.events",
                     "key":"{{jsonPath message.body '$.orderId'}}",
                     "body":"{\"type\":\"OrderSettled\",\"orderId\":\"{{jsonPath message.body '$.orderId'}}\"}"}]}
        """;

    [Fact]
    public void An_inbound_message_produces_the_outbound_one_its_mapping_declares()
    {
        // The headline case for the slice: a command arrives on a topic, an event goes out on another,
        // with a field carried across. Without this the sandbox can mock the call that starts a payment
        // and not the event that reports it settled.
        var (planner, store) = Planner();
        store.Add(Read(SettleMapping));

        var message = Assert.Single(planner.Plan(TenantId.Default, Record()));

        Assert.Equal("orders.events", message.Topic);
        Assert.Equal("ord-7", message.Key);
        Assert.Equal("""{"type":"OrderSettled","orderId":"ord-7"}""", message.Body);
    }

    [Fact]
    public void A_message_on_another_topic_produces_nothing()
    {
        var (planner, store) = Planner();
        store.Add(Read(SettleMapping));

        Assert.Empty(planner.Plan(TenantId.Default, Record(topic: "payments.commands")));
    }

    [Fact]
    public void A_message_whose_body_does_not_match_produces_nothing()
    {
        var (planner, store) = Planner();
        store.Add(Read(SettleMapping));

        Assert.Empty(planner.Plan(TenantId.Default, Record(value: """{"type":"CancelOrder"}""")));
    }

    [Fact]
    public void A_mapping_with_no_trigger_at_all_matches_every_message()
    {
        // Mirrors message-mappings, and is what makes "echo everything on this topic" one line rather
        // than a matcher that has to enumerate what it accepts.
        var (planner, store) = Planner();
        store.Add(Read("""{"publish":[{"topic":"out","body":"seen"}]}"""));

        Assert.Single(planner.Plan(TenantId.Default, Record(topic: "anything", value: "anything")));
    }

    [Fact]
    public void Every_matching_mapping_contributes_not_only_the_first()
    {
        // A fan-out is a real broker pattern: one command producing an event and an audit record from
        // two separate stubs. First-match-wins would make that inexpressible without merging unrelated
        // mappings — the one place this channel departs from HTTP serving on purpose.
        var (planner, store) = Planner();
        store.Add(Read("""{"publish":[{"topic":"orders.events","body":"event"}]}"""));
        store.Add(Read("""{"publish":[{"topic":"audit","body":"audited"}]}"""));

        var planned = planner.Plan(TenantId.Default, Record());

        Assert.Equal(["orders.events", "audit"], planned.Select(message => message.Topic));
    }

    [Fact]
    public void One_mapping_can_emit_several_messages()
    {
        var (planner, store) = Planner();
        store.Add(Read("""{"publish":[{"topic":"a","body":"1"},{"topic":"b","body":"2"}]}"""));

        Assert.Equal(["a", "b"], planner.Plan(TenantId.Default, Record()).Select(message => message.Topic));
    }

    [Fact]
    public void Headers_are_matched_with_the_standard_value_matchers()
    {
        var (planner, store) = Planner();
        store.Add(Read(
            """{"whenHeaders":{"correlation-id":{"matches":"^req-.+"}},"publish":[{"topic":"out","body":"x"}]}"""));

        Assert.Single(planner.Plan(TenantId.Default, Record(headers: new KeyValuePair<string, string>("correlation-id", "req-1"))));
        Assert.Empty(planner.Plan(TenantId.Default, Record(headers: new KeyValuePair<string, string>("correlation-id", "other"))));
        Assert.Empty(planner.Plan(TenantId.Default, Record()));
    }

    [Fact]
    public void The_topic_matcher_is_a_value_matcher_and_not_only_equality()
    {
        // Reusing the value matchers is the entire reason `whenTopic` is read through the request-pattern
        // reader: a team with `orders.*` topics gets a prefix match for free, with oracle-pinned regex
        // semantics rather than a second implementation of "matches".
        var (planner, store) = Planner();
        store.Add(Read("""{"whenTopic":{"matches":"^orders\\..+"},"publish":[{"topic":"out","body":"x"}]}"""));

        Assert.Single(planner.Plan(TenantId.Default, Record(topic: "orders.commands")));
        Assert.Single(planner.Plan(TenantId.Default, Record(topic: "orders.events")));
        Assert.Empty(planner.Plan(TenantId.Default, Record(topic: "payments.commands")));
    }

    [Fact]
    public void Every_part_of_the_trigger_must_match_not_merely_one()
    {
        var (planner, store) = Planner();
        store.Add(Read(
            """
            {"whenTopic":{"equalTo":"orders.commands"},
             "whenHeaders":{"source":{"equalTo":"erp"}},
             "whenMessage":[{"contains":"SettleOrder"}],
             "publish":[{"topic":"out","body":"x"}]}
            """));

        Assert.Single(planner.Plan(TenantId.Default, Record(headers: new KeyValuePair<string, string>("source", "erp"))));

        // Right topic and body, wrong header: an "any part matches" bug would be invisible until a
        // stub started answering messages meant for somebody else.
        Assert.Empty(planner.Plan(TenantId.Default, Record(headers: new KeyValuePair<string, string>("source", "crm"))));
    }

    [Fact]
    public void A_template_can_read_the_topic_the_key_and_the_headers()
    {
        var (planner, store) = Planner();
        store.Add(Read(
            """
            {"publish":[{"topic":"out",
                         "key":"{{message.key}}",
                         "body":"{{message.topic}}|{{message.headers.correlation-id}}",
                         "headers":{"origin":"{{message.topic}}"}}]}
            """));

        var message = Assert.Single(planner.Plan(TenantId.Default, Record(headers: new KeyValuePair<string, string>("correlation-id", "abc"))));

        // A reply that cannot name where it came from forces the correlation into the body of every
        // stub by hand, which is the kind of thing people get subtly wrong once and then everywhere.
        Assert.Equal("ord-7", message.Key);
        Assert.Equal("orders.commands|abc", message.Body);
        Assert.Equal("orders.commands", Assert.Single(message.Headers).Value);
    }

    [Fact]
    public void A_message_with_no_key_renders_an_empty_one_rather_than_the_word_null()
    {
        var (planner, store) = Planner();
        store.Add(Read("""{"publish":[{"topic":"out","body":"[{{message.key}}]"}]}"""));

        Assert.Equal("[]", Assert.Single(planner.Plan(TenantId.Default, Record(key: null))).Body);
    }

    [Fact]
    public void A_destination_topic_is_templated_too()
    {
        // Routing by content is why: one mapping can fan a command out to the topic its payload names,
        // instead of one mapping per destination.
        var (planner, store) = Planner();
        store.Add(Read("""{"publish":[{"topic":"{{jsonPath message.body '$.type'}}.events","body":"x"}]}"""));

        Assert.Equal("SettleOrder.events", Assert.Single(planner.Plan(TenantId.Default, Record())).Topic);
    }

    [Fact]
    public void A_broken_template_drops_its_own_message_and_no_other()
    {
        var (planner, store) = Planner();
        store.Add(Read("""{"publish":[{"topic":"broken","body":"{{#each items}}"},{"topic":"fine","body":"x"}]}"""));

        // A typo in an audit stub must not also stop the event the system under test is waiting for.
        Assert.Equal("fine", Assert.Single(planner.Plan(TenantId.Default, Record())).Topic);

        // And it is not silent: `publish` shipped silent once, and 1.10.1 exists because of it.
        Assert.Equal("broken", Assert.Single(planner.Failures.Snapshot()).Topic);
    }

    [Fact]
    public void A_mapping_is_scoped_to_its_tenant()
    {
        var store = new BrokerMappingStore();
        var planner = new BrokerMappingPlanner(store, new MessageTemplateRenderer());
        store.Add(BrokerMappingReader.Read("""{"publish":[{"topic":"out","body":"x"}]}""", new TenantId("acme")));

        Assert.Single(planner.Plan(new TenantId("acme"), Record()));

        // The invariant no oracle can check for us, and the one whose absence would be a data leak
        // rather than a wrong answer.
        Assert.Empty(planner.Plan(new TenantId("globex"), Record()));
        Assert.Empty(planner.Plan(TenantId.Default, Record()));
    }

    [Fact]
    public void A_publish_with_no_topic_is_dropped_at_registration()
    {
        // Accepting it would mean failing per message, forever, in a log nobody is reading.
        Assert.Empty(Read("""{"publish":[{"body":"x"}]}""").Publishes);
        Assert.Empty(Read("""{"publish":[{"topic":"","body":"x"}]}""").Publishes);
        Assert.Empty(Read("""{"publish":[{"topic":42,"body":"x"}]}""").Publishes);
        Assert.Empty(Read("""{"publish":["not an object"]}""").Publishes);
    }

    [Fact]
    public void A_publish_with_no_body_is_a_tombstone_not_an_empty_string()
    {
        // A null payload is a real Kafka message with a meaning of its own — deleting a key. Coercing
        // it to "" would silently turn a delete into an empty record.
        Assert.Null(Assert.Single(Read("""{"publish":[{"topic":"out"}]}""").Publishes).Body);
    }

    [Fact]
    public void A_tombstone_stays_a_tombstone_all_the_way_through_planning()
    {
        // Reading it as null is half the job; planning it as null is the half a broker sees. Rendering
        // a null body would turn a delete into an empty record, and mutation testing found that the
        // read-level assertion alone did not pin it.
        var (planner, store) = Planner();
        store.Add(Read("""{"publish":[{"topic":"out","key":"k"}]}"""));

        var message = Assert.Single(planner.Plan(TenantId.Default, Record()));

        Assert.Null(message.Body);
        Assert.Equal("k", message.Key);
    }

    [Theory]
    [InlineData("""{"publish":[{"topic":"out","key":42,"body":"x"}]}""")]
    [InlineData("""{"publish":[{"topic":"out","body":{"not":"a string"}}]}""")]
    [InlineData("""{"publish":[{"topic":"out","body":"x","headers":["not an object"]}]}""")]
    [InlineData("""{"publish":[{"topic":"out","body":"x","headers":{"n":42}}]}""")]
    public void A_wrong_typed_publish_field_is_ignored_rather_than_throwing(string json)
    {
        // A registration must never 500 over a field type. The whole publish is kept — its topic is
        // still valid — and only the unusable field is dropped.
        var mapping = Read(json);

        Assert.Equal("out", Assert.Single(mapping.Publishes).Topic);
    }

    [Fact]
    public void Malformed_registrations_are_rejected_rather_than_half_accepted()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Read("{ not json"));
        Assert.Throws<InvalidOperationException>(() => Read("\"a string\""));
        Assert.Throws<InvalidOperationException>(() => Read("[]"));
    }

    [Theory]
    [InlineData("""{"whenTopic":"not an object","publish":[{"topic":"out","body":"x"}]}""")]
    [InlineData("""{"whenHeaders":[],"publish":[{"topic":"out","body":"x"}]}""")]
    [InlineData("""{"whenMessage":{"not":"an array"},"publish":[{"topic":"out","body":"x"}]}""")]
    [InlineData("""{"publish":"not an array"}""")]
    public void A_wrong_shaped_trigger_field_is_ignored_rather_than_throwing(string json)
    {
        // Being permissive about the trigger and strict about the document is deliberate: a mistyped
        // `whenTopic` that matched everything is recoverable, and one that 500s an import is not.
        var mapping = Read(json);

        Assert.NotNull(mapping);
    }

    [Fact]
    public void The_registration_json_is_kept_verbatim()
    {
        // The same as-is rule stub mappings follow: a reader must be able to search their own file for
        // the string the admin list printed back at them.
        const string json = """{"publish":[{"topic":"out","body":"x"}]}""";

        Assert.Equal(json, Read(json).Source);
    }

    [Fact]
    public void A_removed_mapping_stops_producing_and_leaves_the_others_alone()
    {
        var (planner, store) = Planner();
        var doomed = Read("""{"publish":[{"topic":"gone","body":"x"}]}""");
        store.Add(doomed);
        store.Add(Read("""{"publish":[{"topic":"stays","body":"x"}]}"""));

        Assert.True(store.Remove(TenantId.Default, doomed.Id));
        Assert.False(store.Remove(TenantId.Default, doomed.Id));

        Assert.Equal("stays", Assert.Single(planner.Plan(TenantId.Default, Record())).Topic);
    }

    [Fact]
    public void A_reset_empties_one_tenant_only()
    {
        var store = new BrokerMappingStore();
        store.Add(BrokerMappingReader.Read("""{"publish":[{"topic":"a","body":"x"}]}""", new TenantId("acme")));
        store.Add(BrokerMappingReader.Read("""{"publish":[{"topic":"b","body":"x"}]}""", new TenantId("globex")));

        store.Reset(new TenantId("acme"));

        Assert.Empty(store.For(new TenantId("acme")));
        Assert.Single(store.For(new TenantId("globex")));
    }

    [Fact]
    public void Resetting_a_tenant_that_has_nothing_is_not_an_error() =>
        new BrokerMappingStore().Reset(new TenantId("never-used"));

    [Fact]
    public void The_failure_log_keeps_a_sample_rather_than_growing_without_bound()
    {
        // A broken template repeats on every message; an unbounded log of it would be the memory leak
        // rather than the diagnosis.
        var log = new PlanFailureLog();
        for (var i = 0; i < PlanFailureLog.Capacity + 10; i++)
        {
            log.Add(new PlanFailure($"topic-{i}", "boom"));
        }

        var snapshot = log.Snapshot();
        Assert.Equal(PlanFailureLog.Capacity, snapshot.Count);

        // The newest survive: an operator looking at this wants what is failing now.
        Assert.Equal($"topic-{PlanFailureLog.Capacity + 9}", snapshot[^1].Topic);
    }
}
