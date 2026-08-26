using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Core;
using Mockifyr.Templating;

namespace Mockifyr.Facade.WebSocket;

/// <summary>
/// The WebSocket message-serving facade (G15d): register message stubs via
/// <c>POST /__admin/message-mappings</c>, then a WebSocket client's inbound message is
/// matched against each stub's trigger and the matching stubs' templated responses are sent back to the
/// originating channel. This message-serving path has no stable external oracle to differential-test against,
/// so it is verified by a self-test round-trip rather than differentially — see docs/parity/g15-extras.md.
/// </summary>
public static class WebSocketEndpoints
{
    /// <summary>
    /// Adds WebSocket message serving to the app: the <c>/__admin/message-mappings</c> registration
    /// endpoint plus a front-of-pipeline middleware that accepts WebSocket upgrades on any path and
    /// serves matched, templated responses. Call this early so upgrades are intercepted before the
    /// mock-serving fallback.
    /// </summary>
    public static WebApplication UseMockifyrWebSockets(this WebApplication app, string? filesDirectory = null)
    {
        // Resolved once and closed over by every route below (#396).
        var tenantHeaderName = app.Services.GetRequiredService<TenantHeaderOptions>().Name;
        TenantId TenantOf(HttpRequest request) => TenantFrom(request, tenantHeaderName);
        var store = new MessageMappingStore();
        var registry = new WebSocketChannelRegistry();
        var renderer = new MessageTemplateRenderer();

        app.UseWebSockets();

        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                var tenant = TenantOf(context.Request);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var channelId = registry.Add(socket, tenant);
                try
                {
                    // Connect-time mappings (G15g) fire once, unsolicited, before the receive loop.
                    await SendConnectMessagesAsync(socket, store, registry, renderer, tenant, context.RequestAborted);
                    await ServeAsync(socket, store, registry, renderer, tenant, context.RequestAborted);
                }
                finally
                {
                    registry.Remove(channelId);
                }

                return;
            }

            await next(context);
        });

        app.MapPost("/__admin/message-mappings", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                var mapping = MessageMappingReader.Read(json, TenantOf(request), filesDirectory);
                store.Add(mapping);
                return Results.Json(new { id = mapping.Id }, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // JsonException = malformed JSON; InvalidOperationException = a well-formed but wrong-typed
                // field (e.g. a string where the send body object is expected). Both are client input errors.
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
        });

        // Listing + deletion (G18-pre, ADR 0010): the dashboard shows message-mappings next to the
        // request/response stubs. Each entry is the registration JSON as posted, with the id stamped
        // in — the same shape the stub mappings list uses.
        app.MapGet("/__admin/message-mappings", (HttpRequest request) =>
        {
            var mappings = store.For(TenantOf(request)).Select(mapping =>
            {
                var node = (mapping.Source is not null
                    ? System.Text.Json.Nodes.JsonNode.Parse(mapping.Source) : null) as System.Text.Json.Nodes.JsonObject
                    ?? [];
                node["id"] = mapping.Id.ToString();
                return node;
            }).ToList();
            return Results.Json(new { messageMappings = mappings });
        });

        app.MapDelete("/__admin/message-mappings/{id:guid}", (Guid id, HttpRequest request) =>
            store.Remove(TenantOf(request), id) ? Results.Ok() : Results.NotFound());

        // Admin push (POST /__admin/channels/send): dispatch a message to connected clients.
        app.MapPost("/__admin/channels/send", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("body", out var body) &&
                    body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                {
                    await registry.BroadcastAsync(TenantOf(request), data.GetString()!, CancellationToken.None);
                    return Results.Ok();
                }

                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
            catch (JsonException)
            {
                return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
            }
        });

        return app;
    }

    // Connect-time serving (G15g): when a client connects, every connection-triggered mapping's actions
    // are sent once, unsolicited. There is no inbound message, so templates render against an empty body.
    private static async Task SendConnectMessagesAsync(
        System.Net.WebSockets.WebSocket socket,
        MessageMappingStore store,
        WebSocketChannelRegistry registry,
        MessageTemplateRenderer renderer,
        TenantId tenant,
        CancellationToken cancellationToken)
    {
        foreach (var mapping in store.For(tenant))
        {
            if (!mapping.OnConnect)
            {
                continue;
            }

            foreach (var action in mapping.Responses)
            {
                var rendered = renderer.Render(action.Data, string.Empty);
                if (action.Broadcast)
                {
                    await registry.BroadcastAsync(tenant, rendered, cancellationToken);
                }
                else
                {
                    await socket.SendAsync(
                        Encoding.UTF8.GetBytes(rendered), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                }
            }
        }
    }

    private static async Task ServeAsync(
        System.Net.WebSockets.WebSocket socket,
        MessageMappingStore store,
        WebSocketChannelRegistry registry,
        MessageTemplateRenderer renderer,
        TenantId tenant,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cancellationToken);
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(message.ToArray());
            foreach (var mapping in store.For(tenant))
            {
                // Connect-time mappings fire only on connect (SendConnectMessagesAsync), never per message.
                if (mapping.OnConnect || !mapping.Matches(text))
                {
                    continue;
                }

                foreach (var action in mapping.Responses)
                {
                    var rendered = renderer.Render(action.Data, text);
                    if (action.Broadcast)
                    {
                        await registry.BroadcastAsync(tenant, rendered, cancellationToken);
                    }
                    else
                    {
                        await socket.SendAsync(
                            Encoding.UTF8.GetBytes(rendered), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
                    }
                }
            }
        }
    }

    private static TenantId TenantFrom(HttpRequest request, string tenantHeader) =>
        request.Headers.TryGetValue(tenantHeader, out var value) && !string.IsNullOrEmpty(value)
            ? new TenantId(value!)
            : TenantId.Default;
}
