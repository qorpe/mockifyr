using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Real-client self-tests for the SMTP capture facade (G18b, ADR 0009). No oracle exists — WireMock
/// has no SMTP — so the claims verified are Mockifyr's own, driven by <b>MailKit</b>, a real SMTP
/// client, against a full <see cref="MockifyrHost"/> started with <c>--smtp-port 0</c>: plain text,
/// HTML + attachment, multiple recipients, and AUTH-as-tenant, each asserted through
/// <c>/__admin/messages</c> over the wire.
/// </summary>
public sealed class G18bSmtpCaptureTests
{
    private static async Task<(IAsyncDisposable App, HttpClient Admin, int SmtpPort)> StartHostAsync()
    {
        var app = MockifyrHost.Build(["--port", "0", "--smtp-port", "0"]);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses
            .First().Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        var smtpPort = app.Services.GetRequiredService<Mockifyr.Facade.Smtp.SmtpCaptureServer>().Port;
        return (app, new HttpClient { BaseAddress = new Uri(address) }, smtpPort);
    }

    private static MimeMessage Mail(string subject, string text, string? html = null, params string[] to)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("noreply@app.test"));
        foreach (var address in to.Length > 0 ? to : ["user@example.com"])
        {
            message.To.Add(MailboxAddress.Parse(address));
        }

        message.Subject = subject;
        var builder = new BodyBuilder { TextBody = text };
        if (html is not null)
        {
            builder.HtmlBody = html;
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private static async Task<JsonDocument> MessagesAsync(HttpClient admin, string? tenant = null, string query = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/messages" + query);
        if (tenant is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenant);
        }

        using var response = await admin.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PlainMail_FromARealClient_IsCaptured()
    {
        var (app, admin, smtpPort) = await StartHostAsync();
        await using var _ = app;

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
            await client.SendAsync(Mail("Welcome!", "Hello from Mockifyr tests."));
            await client.DisconnectAsync(quit: true);
        }

        using var list = await MessagesAsync(admin);
        var message = Assert.Single(list.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("email", message.GetProperty("channel").GetString());
        Assert.Equal("noreply@app.test", message.GetProperty("from").GetString());
        Assert.Equal("user@example.com", message.GetProperty("to")[0].GetString());
        Assert.Equal("Welcome!", message.GetProperty("subject").GetString());
        Assert.Equal("Hello from Mockifyr tests.", message.GetProperty("body").GetString());
        Assert.Equal("noreply@app.test", message.GetProperty("meta").GetProperty("envelopeFrom").GetString());
    }

    [Fact]
    public async Task HtmlMailWithAttachment_ToTwoRecipients_CapturesEverything()
    {
        var (app, admin, smtpPort) = await StartHostAsync();
        await using var _ = app;

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse("billing@app.test"));
        mail.To.Add(MailboxAddress.Parse("a@example.com"));
        mail.To.Add(MailboxAddress.Parse("b@example.com"));
        mail.Subject = "Invoice";
        var builder = new BodyBuilder { TextBody = "See attachment.", HtmlBody = "<b>See attachment.</b>" };
        builder.Attachments.Add("invoice.txt", System.Text.Encoding.UTF8.GetBytes("total: 42.00"), ContentType.Parse("text/plain"));
        mail.Body = builder.ToMessageBody();

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
            await client.SendAsync(mail);
            await client.DisconnectAsync(quit: true);
        }

        using var list = await MessagesAsync(admin);
        var message = Assert.Single(list.RootElement.GetProperty("messages").EnumerateArray());
        // Envelope recipients — who actually received it — not just the header list.
        Assert.Equal(new[] { "a@example.com", "b@example.com" },
            message.GetProperty("to").EnumerateArray().Select(t => t.GetString()).ToArray());
        Assert.Equal("<b>See attachment.</b>", message.GetProperty("htmlBody").GetString());
        var attachment = Assert.Single(message.GetProperty("attachments").EnumerateArray());
        Assert.Equal("invoice.txt", attachment.GetProperty("name").GetString());
        Assert.Equal("text/plain", attachment.GetProperty("contentType").GetString());
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("total: 42.00"), attachment.GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task AuthUsername_NamesTheTenant()
    {
        var (app, admin, smtpPort) = await StartHostAsync();
        await using var _ = app;

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
            // Any password is accepted; the username scopes the capture — like X-Mockifyr-Tenant.
            await client.AuthenticateAsync("acme", "whatever");
            await client.SendAsync(Mail("Tenant mail", "scoped"));
            await client.DisconnectAsync(quit: true);
        }

        using var acme = await MessagesAsync(admin, tenant: "acme");
        Assert.Equal("Tenant mail",
            Assert.Single(acme.RootElement.GetProperty("messages").EnumerateArray()).GetProperty("subject").GetString());

        using var fallback = await MessagesAsync(admin);
        Assert.Empty(fallback.RootElement.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task TwoMailsOnOneConnection_BothCaptured()
    {
        var (app, admin, smtpPort) = await StartHostAsync();
        await using var _ = app;

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
            await client.SendAsync(Mail("first", "1"));
            await client.SendAsync(Mail("second", "2"));
            await client.DisconnectAsync(quit: true);
        }

        using var list = await MessagesAsync(admin);
        Assert.Equal(new[] { "second", "first" },
            list.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString()).ToArray());
    }

    [Fact]
    public async Task DotStuffedBody_IsUnstuffed()
    {
        var (app, admin, smtpPort) = await StartHostAsync();
        await using var _ = app;

        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
            // A body line starting with '.' exercises RFC 5321 dot-stuffing end to end.
            await client.SendAsync(Mail("dots", ".hidden dot line\r\nsecond line"));
            await client.DisconnectAsync(quit: true);
        }

        using var list = await MessagesAsync(admin);
        var body = Assert.Single(list.RootElement.GetProperty("messages").EnumerateArray()).GetProperty("body").GetString()!;
        Assert.StartsWith(".hidden dot line", body);
        Assert.Contains("second line", body);
    }
}
