using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// WebSocket broadcast + admin push (G15e), extending G15d. Like all WebSocket serving it has no stable
/// WireMock oracle, so it is validated by a self-test with <em>two</em> connected clients: the admin
/// <c>POST /__admin/channels/send</c> reaches both, and a message-mapping whose <c>send</c> action has a
/// non-originating <c>channelTarget</c> broadcasts to both. See docs/parity/g15-extras.md.
/// </summary>
public sealed class G15eWebSocketBroadcastTests
{
    [Fact]
    public async Task AdminChannelsSend_ReachesAllConnectedClients()
    {
        await using var host = MockifyrHost.Build(["--port", "0", "--https-port", "0"]);
        await host.StartAsync();
        var http = HttpAddress(host);

        await RegisterReadyEchoAsync(http);
        using var a = await ConnectRegisteredAsync(http, "/ch");
        using var b = await ConnectRegisteredAsync(http, "/ch");

        using (var admin = new HttpClient { BaseAddress = http })
        using (var content = new StringContent("""{"message":{"body":{"data":"broadcast-hello"}}}""", Encoding.UTF8, "application/json"))
        {
            var response = await admin.PostAsync("/__admin/channels/send", content);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal("broadcast-hello", await ReceiveAsync(a));
        Assert.Equal("broadcast-hello", await ReceiveAsync(b));
    }

    [Fact]
    public async Task BroadcastChannelTarget_ReachesAllClients()
    {
        await using var host = MockifyrHost.Build(["--port", "0", "--https-port", "0"]);
        await host.StartAsync();
        var http = HttpAddress(host);

        const string mapping =
            """
            {
              "trigger": { "type": "message", "message": { "body": { "equalTo": "shout" } } },
              "actions": [
                { "type": "send", "channelTarget": { "type": "broadcast" },
                  "message": { "body": { "data": "everyone: {{message.body}}" } } }
              ]
            }
            """;
        using (var admin = new HttpClient { BaseAddress = http })
        using (var content = new StringContent(mapping, Encoding.UTF8, "application/json"))
        {
            (await admin.PostAsync("/__admin/message-mappings", content)).EnsureSuccessStatusCode();
        }

        await RegisterReadyEchoAsync(http);
        using var a = await ConnectRegisteredAsync(http, "/room");
        using var b = await ConnectRegisteredAsync(http, "/room");

        await SendAsync(a, "shout");

        // The broadcast reaches every connected client (including the originator).
        Assert.Equal("everyone: shout", await ReceiveAsync(a));
        Assert.Equal("everyone: shout", await ReceiveAsync(b));
    }

    private static Uri HttpAddress(WebApplication host)
    {
        var address = host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return new Uri(address);
    }

    /// <summary>The probe a client sends to learn that the server has registered its channel.</summary>
    private const string ReadyProbe = "__mockifyr-ready";

    /// <summary>What the echo mapping answers it with.</summary>
    private const string ReadyReply = "__mockifyr-registered";

    /// <summary>
    /// Registers an echo mapping used only to observe channel registration. Its trigger is a literal
    /// nothing else in these tests sends, so it cannot collide with the messages under test.
    /// </summary>
    private static async Task RegisterReadyEchoAsync(Uri http)
    {
        const string mapping =
            """
            {
              "trigger": { "type": "message", "message": { "body": { "equalTo": "__mockifyr-ready" } } },
              "actions": [ { "type": "send", "message": { "body": { "data": "__mockifyr-registered" } } } ]
            }
            """;

        using var admin = new HttpClient { BaseAddress = http };
        using var content = new StringContent(mapping, Encoding.UTF8, "application/json");
        (await admin.PostAsync("/__admin/message-mappings", content)).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Connects, then waits until the server has actually registered the channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ClientWebSocket.ConnectAsync</c> returns when the <b>client</b> finishes the handshake. The
    /// server adds the channel to its registry only after <c>AcceptWebSocketAsync()</c> returns, so
    /// between those two moments the client believes it is connected and a broadcast would skip it.
    /// That window is what made these tests fail intermittently (#325).
    /// </para>
    /// <para>
    /// The endpoint registers the channel <b>before</b> entering its receive loop, so a reply to the
    /// probe is proof of registration. Sleeping instead would hide the race rather than close it, and
    /// would cost every run the time it takes to hide it.
    /// </para>
    /// </remarks>
    private static async Task<ClientWebSocket> ConnectRegisteredAsync(Uri http, string path)
    {
        var client = await ConnectAsync(http, path);
        await SendAsync(client, ReadyProbe);
        Assert.Equal(ReadyReply, await ReceiveAsync(client));
        return client;
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri http, string path)
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://{http.Host}:{http.Port}{path}"), CancellationToken.None);
        return client;
    }

    private static Task SendAsync(ClientWebSocket client, string text) =>
        client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    private static async Task<string> ReceiveAsync(ClientWebSocket client)
    {
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.ReceiveAsync(buffer, cts.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }
}
