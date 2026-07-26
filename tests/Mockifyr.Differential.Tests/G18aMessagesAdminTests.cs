using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire-level self-tests for <c>/__admin/messages</c> (G18a, ADR 0009) — no oracle exists (WireMock
/// has no message concept). A message is seeded through the host's own <see cref="IMessageSink"/>
/// (the seam the SMTP/SMS facades will use) and asserted through the REST surface: filters,
/// count/list agreement, tenant scoping via the tenant header, delete/reset round-trips.
/// </summary>
public sealed class G18aMessagesAdminTests : IAsyncDisposable
{
    private readonly MockifyrKestrelHost _host = new();
    private readonly HttpClient _client;

    public G18aMessagesAdminTests()
    {
        _client = new HttpClient { BaseAddress = new Uri(_host.BaseAddress) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private static MessageEnvelope Message(MessageChannel channel, string to, string body, string? subject = null) =>
        new(Guid.NewGuid(), channel, "noreply@app.test", [to], subject, body, null,
            new Dictionary<string, string> { ["source"] = "test" }, [], DateTimeOffset.UtcNow);

    private async Task<JsonDocument> GetJsonAsync(string path, string? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (tenant is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenant);
        }

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Messages_ListFilterCountDeleteReset_OverTheWire()
    {
        var sink = _host.Services.GetRequiredService<IMessageSink>();
        sink.Accept(TenantId.Default, Message(MessageChannel.Email, "user@example.com", "Your code is 123456", "OTP"));
        sink.Accept(TenantId.Default, Message(MessageChannel.Sms, "+905551112233", "code 999"));
        sink.Accept(new TenantId("acme"), Message(MessageChannel.Email, "x@acme.test", "acme mail"));

        // List (default tenant): both messages, newest first, with the wire shape.
        using (var list = await GetJsonAsync("/__admin/messages"))
        {
            var messages = list.RootElement.GetProperty("messages").EnumerateArray().ToList();
            Assert.Equal(2, messages.Count);
            Assert.Equal("sms", messages[0].GetProperty("channel").GetString());
            Assert.Equal("+905551112233", messages[0].GetProperty("to")[0].GetString());
            Assert.Equal("email", messages[1].GetProperty("channel").GetString());
            Assert.Equal("OTP", messages[1].GetProperty("subject").GetString());
            Assert.Equal("test", messages[1].GetProperty("meta").GetProperty("source").GetString());
        }

        // Filters: channel + contains; count agrees with list.
        using (var sms = await GetJsonAsync("/__admin/messages?channel=sms"))
        {
            Assert.Single(sms.RootElement.GetProperty("messages").EnumerateArray());
        }

        using (var count = await GetJsonAsync("/__admin/messages/count?contains=123456"))
        {
            Assert.Equal(1, count.RootElement.GetProperty("count").GetInt32());
        }

        // Tenant scoping via the header: acme sees only its own message.
        using (var acme = await GetJsonAsync("/__admin/messages", tenant: "acme"))
        {
            var messages = acme.RootElement.GetProperty("messages").EnumerateArray().ToList();
            Assert.Equal("acme mail", Assert.Single(messages).GetProperty("body").GetString());
        }

        // Get + cross-tenant 404 + delete + reset.
        string id;
        using (var list = await GetJsonAsync("/__admin/messages"))
        {
            id = list.RootElement.GetProperty("messages")[0].GetProperty("id").GetString()!;
        }

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/__admin/messages/{id}")).StatusCode);

        using (var crossTenant = new HttpRequestMessage(HttpMethod.Get, $"/__admin/messages/{id}"))
        {
            crossTenant.Headers.Add("X-Mockifyr-Tenant", "acme");
            Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(crossTenant)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync($"/__admin/messages/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/__admin/messages/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/__admin/messages/reset", content: null)).StatusCode);
        using (var emptied = await GetJsonAsync("/__admin/messages"))
        {
            Assert.Empty(emptied.RootElement.GetProperty("messages").EnumerateArray());
        }

        // Reset touched only the default tenant.
        using var acmeAfter = await GetJsonAsync("/__admin/messages", tenant: "acme");
        Assert.Single(acmeAfter.RootElement.GetProperty("messages").EnumerateArray());
    }
}
