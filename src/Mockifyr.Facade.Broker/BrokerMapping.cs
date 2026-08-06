using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;

namespace Mockifyr.Facade.Broker;

/// <summary>One message a broker mapping emits when it matches.</summary>
/// <param name="Topic">The destination topic, templated.</param>
/// <param name="Key">The partition key, templated. Null when the mapping declares none.</param>
/// <param name="Body">The payload, templated. Null publishes a tombstone.</param>
/// <param name="Headers">Message headers, values templated.</param>
public sealed record BrokerPublish(
    string Topic,
    string? Key,
    string? Body,
    IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// A broker stub (the <c>brokerMappings</c> dialect, ADR 0013 slice 3): a trigger over an inbound
/// message and the messages it emits in reply.
/// </summary>
/// <remarks>
/// <para>
/// A broker message is not a request — no method, no URL, no status — so this is its own shape rather
/// than a <c>request</c>/<c>response</c> pair, exactly as ADR 0010 says a channel should get. What it
/// is <b>not</b> is a new matching vocabulary: the trigger reuses the standard value and body matchers,
/// so <c>equalToJson</c>, <c>matchesJsonPath</c> and the rest behave here precisely as the differential
/// suite already proved them to on the HTTP side. New syntax around old, verified semantics.
/// </para>
/// <para>
/// The matchers are evaluated by handing each a purpose-built <see cref="CanonicalRequest"/> — the
/// topic as a one-header request, the message headers as a request's headers, the payload as a body.
/// That is a deliberate adapter and not a leak: it keeps one matcher implementation for both channels,
/// so a fix to <c>equalToXml</c> cannot mean two different things depending on where the bytes arrived.
/// </para>
/// </remarks>
/// <param name="Id">Identity, for listing and deletion.</param>
/// <param name="Tenant">The owning tenant — a broker stub is scoped like every other stub.</param>
/// <param name="Topic">Matcher for the topic the message arrived on. Null matches any topic.</param>
/// <param name="Headers">Matchers over the message headers.</param>
/// <param name="Body">Matchers over the payload.</param>
/// <param name="Publishes">What to emit when everything matches.</param>
/// <param name="Source">The registration JSON, kept verbatim so the admin list shows what was posted.</param>
public sealed record BrokerMapping(
    Guid Id,
    TenantId Tenant,
    IMatcher? Topic,
    IReadOnlyList<IMatcher> Headers,
    IReadOnlyList<IMatcher> Body,
    IReadOnlyList<BrokerPublish> Publishes,
    string? Source = null)
{
    /// <summary>The pseudo-header a topic matcher is evaluated against. Never seen by a user.</summary>
    private const string TopicSlot = "Mockifyr-Broker-Topic";

    /// <summary>
    /// Whether this mapping's trigger matches the record. A mapping with no matchers at all matches
    /// every message on the subscribed topics, mirroring <c>message-mappings</c>.
    /// </summary>
    public bool Matches(ConsumedRecord record)
    {
        if (Topic is { } topic && !topic.Match(Input(record.Topic, [new(TopicSlot, record.Topic)])).IsExactMatch)
        {
            return false;
        }

        // Headers and body see the same input: a matcher reads only the part it was built for, and
        // building one request rather than two keeps the two checks from disagreeing about the message.
        var input = Input(record.Value ?? string.Empty, record.Headers);

        return Headers.All(matcher => matcher.Match(input).IsExactMatch)
            && Body.All(matcher => matcher.Match(input).IsExactMatch);
    }

    private static MatchInput Input(string body, IReadOnlyList<KeyValuePair<string, string>> headers) => new()
    {
        // "MESSAGE" and "/" are placeholders no matcher on this path can read: a broker mapping has no
        // method or URL to match on, and offering one would invent a concept the channel does not have.
        Request = CanonicalRequestBuilder.Build("MESSAGE", "/", headers, Encoding.UTF8.GetBytes(body)),
    };
}

/// <summary>Parses a <c>brokerMappings</c> entry into a <see cref="BrokerMapping"/>.</summary>
public static class BrokerMappingReader
{
    /// <summary>
    /// Reads <c>{ "whenTopic": &lt;matcher&gt;, "whenMessage": [&lt;matcher&gt;…],
    /// "whenHeaders": { "&lt;name&gt;": &lt;matcher&gt; }, "publish": [{ "topic": …, "key": …,
    /// "body": …, "headers": {…} }] }</c>, per ADR 0013.
    /// </summary>
    /// <remarks>
    /// Every matcher is read through <see cref="MappingJsonReader.ReadRequestPattern"/> rather than
    /// parsed here, which is the whole point: the dialect's matchers arrive with the semantics the
    /// oracle already pinned, and a new matcher added on the HTTP side works here the day it lands.
    /// </remarks>
    /// <exception cref="JsonException">The document is not JSON.</exception>
    /// <exception cref="InvalidOperationException">A field is well-formed JSON of the wrong shape.</exception>
    public static BrokerMapping Read(string json, TenantId tenant)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("a broker mapping must be a JSON object.");
        }

        IMatcher? topic = null;
        if (root.TryGetProperty("whenTopic", out var whenTopic) && whenTopic.ValueKind == JsonValueKind.Object)
        {
            // Read as a header matcher over a reserved name, so `equalTo`, `matches`, `contains` and
            // the rest of the value matchers all apply to a topic without a second implementation.
            topic = MappingJsonReader
                .ReadRequestPattern(
                    $$$"""{"headers":{"Mockifyr-Broker-Topic":{{{whenTopic.GetRawText()}}}}}""")
                .Headers.FirstOrDefault();
        }

        IReadOnlyList<IMatcher> headers = [];
        if (root.TryGetProperty("whenHeaders", out var whenHeaders) && whenHeaders.ValueKind == JsonValueKind.Object)
        {
            headers = MappingJsonReader
                .ReadRequestPattern($$$"""{"headers":{{{whenHeaders.GetRawText()}}}}""")
                .Headers;
        }

        IReadOnlyList<IMatcher> body = [];
        if (root.TryGetProperty("whenMessage", out var whenMessage) && whenMessage.ValueKind == JsonValueKind.Array)
        {
            body = MappingJsonReader
                .ReadRequestPattern($$$"""{"bodyPatterns":{{{whenMessage.GetRawText()}}}}""")
                .Body;
        }

        var publishes = new List<BrokerPublish>();
        if (root.TryGetProperty("publish", out var publish) && publish.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in publish.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("topic", out var destination) ||
                    destination.ValueKind != JsonValueKind.String ||
                    destination.GetString() is not { Length: > 0 } destinationTopic)
                {
                    // A publish with no topic is not a publish. Dropping it at registration beats
                    // accepting it and failing per message, forever, in a log nobody is reading.
                    continue;
                }

                publishes.Add(new BrokerPublish(
                    destinationTopic,
                    Text(entry, "key"),
                    Text(entry, "body"),
                    ReadHeaders(entry)));
            }
        }

        return new BrokerMapping(Guid.NewGuid(), tenant, topic, headers, body, publishes, json);
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<KeyValuePair<string, string>> ReadHeaders(JsonElement entry)
    {
        if (!entry.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return
        [
            .. headers.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .Select(property => new KeyValuePair<string, string>(property.Name, property.Value.GetString()!)),
        ];
    }
}

/// <summary>A tenant-scoped store of broker mappings, shared by the admin routes and the consumer.</summary>
public sealed class BrokerMappingStore
{
    private readonly ConcurrentDictionary<TenantId, List<BrokerMapping>> _byTenant = new();
    private readonly Lock _gate = new();

    /// <summary>Registers a mapping.</summary>
    public void Add(BrokerMapping mapping)
    {
        lock (_gate)
        {
            _byTenant.GetOrAdd(mapping.Tenant, _ => []).Add(mapping);
        }
    }

    /// <summary>A snapshot of the tenant's mappings, in registration order.</summary>
    public IReadOnlyList<BrokerMapping> For(TenantId tenant)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var list) ? [.. list] : [];
        }
    }

    /// <summary>Removes the tenant's mapping with the given id; false when it does not exist.</summary>
    public bool Remove(TenantId tenant, Guid id)
    {
        lock (_gate)
        {
            return _byTenant.TryGetValue(tenant, out var list) && list.RemoveAll(mapping => mapping.Id == id) > 0;
        }
    }

    /// <summary>Removes every mapping the tenant owns, leaving other tenants alone.</summary>
    public void Reset(TenantId tenant)
    {
        lock (_gate)
        {
            _byTenant.TryRemove(tenant, out _);
        }
    }
}
