using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Real-client self-tests for message behaviors (G18e, ADR 0009): SMTP fault directives felt by a
/// real MailKit client, simulated SMS provider errors, the capture webhook (delivered to a stub on
/// the same host, then asserted through the request journal — the whole loop stays inside
/// Mockifyr), and the <c>--message-limit</c> inbox bound. No oracle exists for any of this.
/// </summary>
public sealed class G18eMessageBehaviorTests
{
    private static async Task<(IAsyncDisposable App, HttpClient Admin, int SmtpPort, string BaseAddress)> StartHostAsync(
        params string[] extraArgs)
    {
        var app = MockifyrHost.Build(["--port", "0", "--smtp-port", "0", "--sms-profile", "twilio", .. extraArgs]);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses
            .First().Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        var smtpPort = app.Services.GetRequiredService<Mockifyr.Facade.Smtp.SmtpCaptureServer>().Port;
        return (app, new HttpClient { BaseAddress = new Uri(address) }, smtpPort, address);
    }

    private static MimeMessage Mail(string subject = "hello", string body = "body")
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("noreply@app.test"));
        message.To.Add(MailboxAddress.Parse("user@example.com"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        return message;
    }

    private static Task<HttpResponseMessage> PutBehaviorsAsync(HttpClient admin, string json) =>
        admin.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/__admin/messages/behaviors")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task SmtpReject_BouncesWithA550_TheClientFeelsIt()
    {
        var (app, admin, smtpPort, _) = await StartHostAsync();
        await using var _ = app;
        Assert.Equal(HttpStatusCode.OK, (await PutBehaviorsAsync(admin, """{"smtpFault":"reject"}""")).StatusCode);

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
        var thrown = await Assert.ThrowsAnyAsync<SmtpCommandException>(() => client.SendAsync(Mail()));
        Assert.Equal(SmtpStatusCode.MailboxUnavailable, thrown.StatusCode); // 550

        // Nothing was captured — the bounce is a bounce.
        using var count = JsonDocument.Parse(await admin.GetStringAsync("/__admin/messages/count"));
        Assert.Equal(0, count.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task SmtpDelay_HoldsTheDataAck()
    {
        var (app, admin, smtpPort, _) = await StartHostAsync();
        await using var _ = app;
        await PutBehaviorsAsync(admin, """{"smtpDelayMs":400}""");

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
        var watch = Stopwatch.StartNew();
        await client.SendAsync(Mail());
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds >= 350, $"send returned in {watch.ElapsedMilliseconds}ms");
        using var count = JsonDocument.Parse(await admin.GetStringAsync("/__admin/messages/count"));
        Assert.Equal(1, count.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task SmtpDrop_ClosesTheConnectionMidTransaction()
    {
        var (app, admin, smtpPort, _) = await StartHostAsync();
        await using var _ = app;
        await PutBehaviorsAsync(admin, """{"smtpFault":"drop"}""");

        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(Mail()));
    }

    [Fact]
    public async Task SmsErrorDirective_AnswersTheSimulatedProviderError()
    {
        var (app, admin, _, _) = await StartHostAsync();
        await using var _ = app;
        await PutBehaviorsAsync(admin, """{"smsErrorCode":21211}""");

        using var response = await admin.PostAsync("/2010-04-01/Accounts/ACtest/Messages.json",
            new StringContent("To=%2B1&From=%2B2&Body=x", Encoding.UTF8, "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(21211, error.RootElement.GetProperty("code").GetInt32());
        using var count = JsonDocument.Parse(await admin.GetStringAsync("/__admin/messages/count"));
        Assert.Equal(0, count.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task CaptureWebhook_PostsTheMessage_ToTheConfiguredUrl()
    {
        var (app, admin, smtpPort, baseAddress) = await StartHostAsync();
        await using var _ = app;

        // The webhook target is a stub on this same host, so the whole loop — capture → webhook →
        // stub → journal — is asserted without any external listener.
        const string hook = """{"request":{"method":"POST","urlPath":"/message-hook"},"response":{"status":200}}""";
        await admin.PostAsync("/__admin/mappings", new StringContent(hook, Encoding.UTF8, "application/json"));
        await PutBehaviorsAsync(admin, $$"""{"webhookUrl":"{{baseAddress}}/message-hook"}""");

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
            await client.SendAsync(Mail("hooked", "notify me"));
            await client.DisconnectAsync(quit: true);
        }

        // Delivery is fire-and-forget; poll the journal briefly for the hook hit (the list is lean —
        // url only — so fetch the entry detail for the delivered body).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string? payload = null;
        while (DateTime.UtcNow < deadline && payload is null)
        {
            using var requests = JsonDocument.Parse(await admin.GetStringAsync("/__admin/requests"));
            var id = requests.RootElement.GetProperty("requests").EnumerateArray()
                .Where(r => r.GetProperty("url").GetString() == "/message-hook")
                .Select(r => r.GetProperty("id").GetString())
                .FirstOrDefault();
            if (id is null)
            {
                await Task.Delay(100);
                continue;
            }

            using var detail = JsonDocument.Parse(await admin.GetStringAsync($"/__admin/requests/{id}"));
            payload = detail.RootElement.GetProperty("request").GetProperty("body").GetString();
        }

        Assert.NotNull(payload);
        Assert.Contains("hooked", payload);
        Assert.Contains("notify me", payload);
        Assert.Contains("email", payload);
    }

    [Fact]
    public async Task MessageLimit_BoundsTheInbox()
    {
        var (app, admin, smtpPort, _) = await StartHostAsync("--message-limit", "2");
        await using var _ = app;

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
            foreach (var n in new[] { "1", "2", "3" })
            {
                await client.SendAsync(Mail($"m{n}"));
            }

            await client.DisconnectAsync(quit: true);
        }

        using var list = JsonDocument.Parse(await admin.GetStringAsync("/__admin/messages"));
        Assert.Equal(new[] { "m3", "m2" },
            list.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString()).ToArray());
    }

    [Fact]
    public async Task Behaviors_AreTenantScoped_AndValidated()
    {
        var (app, admin, smtpPort, _) = await StartHostAsync();
        await using var _ = app;

        // acme gets a reject directive; the default tenant must stay healthy.
        using (var put = new HttpRequestMessage(HttpMethod.Put, "/__admin/messages/behaviors")
        {
            Content = new StringContent("""{"smtpFault":"reject"}""", Encoding.UTF8, "application/json"),
        })
        {
            put.Headers.Add("X-Mockifyr-Tenant", "acme");
            Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(put)).StatusCode);
        }

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
            await client.SendAsync(Mail()); // default tenant: no directive, must pass
            await client.DisconnectAsync(quit: true);
        }

        // Validation refuses nonsense instead of storing it.
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await PutBehaviorsAsync(admin, """{"smtpDelayMs":-5}""")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await PutBehaviorsAsync(admin, """{"smsErrorCode":42}""")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await PutBehaviorsAsync(admin, """{"smtpFault":"explode"}""")).StatusCode);

        // Reset returns the tenant to the default.
        using (var reset = new HttpRequestMessage(HttpMethod.Delete, "/__admin/messages/behaviors"))
        {
            reset.Headers.Add("X-Mockifyr-Tenant", "acme");
            Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(reset)).StatusCode);
        }

        using var get = new HttpRequestMessage(HttpMethod.Get, "/__admin/messages/behaviors");
        get.Headers.Add("X-Mockifyr-Tenant", "acme");
        using var response = await admin.SendAsync(get);
        using var behaviors = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("none", behaviors.RootElement.GetProperty("smtpFault").GetString());
    }
}
