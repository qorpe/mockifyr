using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Mockifyr.Adapters.MappingJson;
using Mockifyr.Core;

namespace Mockifyr.Facade.WebSocket;

/// <summary>
/// A WebSocket message stub (the <c>message-mappings</c> JSON dialect, G15d; self-tested): a trigger
/// that matches an inbound message body and a set of templated responses to send back to the originating
/// channel. The trigger reuses the standard body value-matchers; the responses reuse the templating engine.
/// </summary>
/// <summary>A <c>send</c> action: the templated message data and whether it broadcasts (vs. the originating channel).</summary>
public sealed record SendAction(string Data, bool Broadcast);

public sealed record MessageMapping(
    Guid Id,
    TenantId Tenant,
    IReadOnlyList<IMatcher> Trigger,
    IReadOnlyList<SendAction> Responses,
    bool OnConnect = false,
    string? Source = null)
{
    /// <summary>Whether this mapping's trigger matches the given inbound message. An empty trigger matches any.</summary>
    public bool Matches(string message)
    {
        if (Trigger.Count == 0)
        {
            return true;
        }

        var input = new MatchInput
        {
            Request = CanonicalRequestBuilder.Build("MESSAGE", "/", headers: null, Encoding.UTF8.GetBytes(message)),
        };

        return Trigger.All(matcher => matcher.Match(input).IsExactMatch);
    }
}

/// <summary>Parses a message-mapping JSON document into a <see cref="MessageMapping"/>.</summary>
public static class MessageMappingReader
{
    /// <summary>
    /// Reads <c>{ "trigger": { "message": { "body": &lt;matcher&gt; } }, "actions": [ { "type": "send",
    /// "message": { "body": { "data": &lt;template&gt; } } } ] }</c>. The trigger body reuses the standard
    /// value-matchers (via the request-pattern reader), so <c>equalTo</c>/<c>matches</c>/
    /// <c>matchesJsonPath</c> all work; an absent trigger body matches every message. A
    /// <c>"trigger": { "type": "connection" }</c> makes the mapping <b>connect-time</b> (G15g): its actions
    /// fire once when a client connects, unsolicited, instead of on an inbound message. A send action's
    /// body may be <c>{ "data": &lt;template&gt; }</c> or <c>{ "filePath": &lt;name&gt; }</c> (G15g), the
    /// latter read from <paramref name="filesDirectory"/> (the <c>__files</c> asset directory) at
    /// registration.
    /// </summary>
    public static MessageMapping Read(string json, TenantId tenant, string? filesDirectory = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var onConnect = false;
        IReadOnlyList<IMatcher> trigger = [];
        if (root.TryGetProperty("trigger", out var t))
        {
            // A connection trigger fires on connect; a message trigger reuses the standard body matchers.
            onConnect = t.TryGetProperty("type", out var triggerType) && triggerType.ValueKind == JsonValueKind.String &&
                        string.Equals(triggerType.GetString(), "connection", StringComparison.OrdinalIgnoreCase);

            if (t.TryGetProperty("message", out var triggerMessage) &&
                triggerMessage.TryGetProperty("body", out var body) &&
                body.ValueKind == JsonValueKind.Object)
            {
                trigger = MappingJsonReader.ReadRequestPattern("{\"bodyPatterns\":[" + body.GetRawText() + "]}").Body;
            }
        }

        var responses = new List<SendAction>();
        if (root.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var action in actions.EnumerateArray())
            {
                if (action.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                    string.Equals(type.GetString(), "send", StringComparison.OrdinalIgnoreCase) &&
                    action.TryGetProperty("message", out var actionMessage) &&
                    actionMessage.TryGetProperty("body", out var actionBody) &&
                    ResolveData(actionBody, filesDirectory) is { } payload)
                {
                    // channelTarget defaults to originating; any other type broadcasts to all channels.
                    var broadcast = action.TryGetProperty("channelTarget", out var channelTarget) &&
                                    channelTarget.ValueKind == JsonValueKind.Object &&
                                    channelTarget.TryGetProperty("type", out var ctType) &&
                                    ctType.ValueKind == JsonValueKind.String &&
                                    !string.Equals(ctType.GetString(), "originating", StringComparison.OrdinalIgnoreCase);
                    responses.Add(new SendAction(payload, broadcast));
                }
            }
        }

        // The raw registration JSON is retained so the admin list (G18-pre) can show the mapping
        // exactly as it was posted — the same as-is rule stub mappings follow.
        return new MessageMapping(Guid.NewGuid(), tenant, trigger, responses, onConnect, json);
    }

    // A send body is either inline `data` or a `filePath` read from the files directory (the
    // __files asset directory). filePath is resolved to its text at registration, so the runtime path is unchanged.
    // Returns null when neither is present (or a filePath can't be read), so the action is skipped.
    private static string? ResolveData(JsonElement actionBody, string? filesDirectory)
    {
        if (actionBody.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
        {
            return data.GetString();
        }

        if (actionBody.TryGetProperty("filePath", out var filePath) && filePath.ValueKind == JsonValueKind.String &&
            filesDirectory is not null && filePath.GetString() is { } name)
        {
            var path = Path.Combine(filesDirectory, name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        return null;
    }
}

/// <summary>An in-memory, tenant-scoped store of message mappings (shared by the admin + WS endpoints).</summary>
public sealed class MessageMappingStore
{
    private readonly ConcurrentDictionary<TenantId, List<MessageMapping>> _byTenant = new();
    private readonly object _gate = new();

    /// <summary>Registers a mapping.</summary>
    public void Add(MessageMapping mapping)
    {
        lock (_gate)
        {
            _byTenant.GetOrAdd(mapping.Tenant, _ => []).Add(mapping);
        }
    }

    /// <summary>A snapshot of the tenant's mappings.</summary>
    public IReadOnlyList<MessageMapping> For(TenantId tenant)
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
            return _byTenant.TryGetValue(tenant, out var list) && list.RemoveAll(m => m.Id == id) > 0;
        }
    }
}

/// <summary>
/// Tracks the open WebSocket channels per tenant so a message can be **broadcast** to them — both by a
/// <c>channelTarget</c> broadcast action and by the admin <c>POST /__admin/channels/send</c> push (G15d+).
/// </summary>
public sealed class WebSocketChannelRegistry
{
    private readonly ConcurrentDictionary<Guid, (System.Net.WebSockets.WebSocket Socket, TenantId Tenant)> _channels = new();

    /// <summary>Registers an open channel; returns its id (pass it to <see cref="Remove"/> on close).</summary>
    public Guid Add(System.Net.WebSockets.WebSocket socket, TenantId tenant)
    {
        var id = Guid.NewGuid();
        _channels[id] = (socket, tenant);
        return id;
    }

    /// <summary>Unregisters a closed channel.</summary>
    public void Remove(Guid id) => _channels.TryRemove(id, out _);

    /// <summary>Sends the text to every open channel of the tenant.</summary>
    public async Task BroadcastAsync(TenantId tenant, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        foreach (var (_, channel) in _channels)
        {
            if (channel.Tenant != tenant || channel.Socket.State != System.Net.WebSockets.WebSocketState.Open)
            {
                continue;
            }

            try
            {
                await channel.Socket.SendAsync(
                    bytes, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            catch (Exception)
            {
                // A racing close; skip this channel.
            }
        }
    }
}
