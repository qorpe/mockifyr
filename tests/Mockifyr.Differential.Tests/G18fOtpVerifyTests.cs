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
/// The G18f e2e proof (ADR 0009): an "application" sends an OTP mail over real SMTP and an OTP SMS
/// through the Twilio profile, and the "test" reads each code back with <b>one admin GET</b> —
/// `/__admin/messages/otp?recipient=…&amp;channel=…`. Plus the verify additions: the `matches`
/// regex filter on list/count, custom OTP patterns with a capture group, and honest 404/422 shapes.
/// No oracle exists for message channels.
/// </summary>
public sealed class G18fOtpVerifyTests
{
    private static async Task<(IAsyncDisposable App, HttpClient Admin, int SmtpPort)> StartHostAsync()
    {
        var app = MockifyrHost.Build(["--port", "0", "--smtp-port", "0", "--sms-profile", "twilio"]);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses
            .First().Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        var smtpPort = app.Services.GetRequiredService<Mockifyr.Facade.Smtp.SmtpCaptureServer>().Port;
        return (app, new HttpClient { BaseAddress = new Uri(address) }, smtpPort);
    }

    [Fact]
    public async Task OtpFlow_MailAndSms_EachCodeReadInOneAdminCall()
    {
        var (app, admin, smtpPort) = await StartHostAsync();
        await using var _ = app;

        // The "application" sends the OTP mail...
        using (var client = new SmtpClient())
        {
            await client.ConnectAsync("127.0.0.1", smtpPort, SecureSocketOptions.None);
            var mail = new MimeMessage();
            mail.From.Add(MailboxAddress.Parse("auth@app.test"));
            mail.To.Add(MailboxAddress.Parse("omer@example.com"));
            mail.Subject = "Your login code";
            mail.Body = new TextPart("plain") { Text = "Use code 482913 to sign in. It expires in 5 minutes." };
            await client.SendAsync(mail);
            await client.DisconnectAsync(quit: true);
        }

        // ...and the OTP SMS (an older SMS to the same number first — latest must win).
        foreach (var body in new[] { "Old code 111111", "Your verification code is 775533" })
        {
            await admin.PostAsync("/2010-04-01/Accounts/ACtest/Messages.json", new StringContent(
                $"To=%2B905551112233&From=%2B15005550006&Body={Uri.EscapeDataString(body)}",
                Encoding.UTF8, "application/x-www-form-urlencoded"));
        }

        // The "test" reads each code back in ONE call.
        using (var mailOtp = JsonDocument.Parse(
            await admin.GetStringAsync("/__admin/messages/otp?recipient=omer@example.com&channel=email")))
        {
            Assert.Equal("482913", mailOtp.RootElement.GetProperty("otp").GetString());
        }

        using var smsOtp = JsonDocument.Parse(
            await admin.GetStringAsync("/__admin/messages/otp?recipient=%2B905551112233&channel=sms"));
        Assert.Equal("775533", smsOtp.RootElement.GetProperty("otp").GetString());

        // The by-id form agrees with the recipient form.
        var messageId = smsOtp.RootElement.GetProperty("messageId").GetString();
        using var byId = JsonDocument.Parse(await admin.GetStringAsync($"/__admin/messages/{messageId}/otp"));
        Assert.Equal("775533", byId.RootElement.GetProperty("otp").GetString());
    }

    [Fact]
    public async Task CustomPattern_WithCaptureGroup_ReturnsTheGroup()
    {
        var (app, admin, _) = await StartHostAsync();
        await using var _ = app;
        await admin.PostAsync("/2010-04-01/Accounts/ACtest/Messages.json", new StringContent(
            "To=%2B901&From=%2B2&Body=" + Uri.EscapeDataString("Activation key: AB-9931-XY"),
            Encoding.UTF8, "application/x-www-form-urlencoded"));

        using var otp = JsonDocument.Parse(await admin.GetStringAsync(
            "/__admin/messages/otp?recipient=%2B901&pattern=" + Uri.EscapeDataString(@"AB-(\d+)-XY")));

        Assert.Equal("9931", otp.RootElement.GetProperty("otp").GetString());
    }

    [Fact]
    public async Task OtpErrors_AreHonest()
    {
        var (app, admin, _) = await StartHostAsync();
        await using var _ = app;

        // No message at all → Message.NotFound.
        using (var missing = await admin.GetAsync("/__admin/messages/otp?recipient=nobody"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var body = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Assert.Equal("Message.NotFound", body.RootElement.GetProperty("code").GetString());
        }

        // A message with no digits → Otp.NoMatch (a different, honest code).
        await admin.PostAsync("/2010-04-01/Accounts/ACtest/Messages.json", new StringContent(
            "To=%2B902&From=%2B2&Body=hello+there", Encoding.UTF8, "application/x-www-form-urlencoded"));
        using (var noMatch = await admin.GetAsync("/__admin/messages/otp?recipient=%2B902"))
        {
            Assert.Equal(HttpStatusCode.NotFound, noMatch.StatusCode);
            using var body = JsonDocument.Parse(await noMatch.Content.ReadAsStringAsync());
            Assert.Equal("Otp.NoMatch", body.RootElement.GetProperty("code").GetString());
        }

        // A broken pattern → 422, not a 500.
        using var invalid = await admin.GetAsync("/__admin/messages/otp?recipient=%2B902&pattern=" + Uri.EscapeDataString("(["));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }

    [Fact]
    public async Task MatchesRegexFilter_OnListAndCount()
    {
        var (app, admin, _) = await StartHostAsync();
        await using var _ = app;
        foreach (var body in new[] { "Your code is 482913", "Delivery update", "code 99" })
        {
            await admin.PostAsync("/2010-04-01/Accounts/ACtest/Messages.json", new StringContent(
                "To=%2B903&From=%2B2&Body=" + Uri.EscapeDataString(body),
                Encoding.UTF8, "application/x-www-form-urlencoded"));
        }

        var pattern = Uri.EscapeDataString(@"code is \d{6}");
        using (var list = JsonDocument.Parse(await admin.GetStringAsync($"/__admin/messages?matches={pattern}")))
        {
            Assert.Equal("Your code is 482913",
                Assert.Single(list.RootElement.GetProperty("messages").EnumerateArray()).GetProperty("body").GetString());
        }

        using (var count = JsonDocument.Parse(await admin.GetStringAsync($"/__admin/messages/count?matches={pattern}")))
        {
            Assert.Equal(1, count.RootElement.GetProperty("count").GetInt32());
        }

        // A malformed regex filters to nothing — the admin surface never 500s over an input.
        using var broken = await admin.GetAsync("/__admin/messages/count?matches=" + Uri.EscapeDataString("(["));
        Assert.Equal(HttpStatusCode.OK, broken.StatusCode);
        using var brokenBody = JsonDocument.Parse(await broken.Content.ReadAsStringAsync());
        Assert.Equal(0, brokenBody.RootElement.GetProperty("count").GetInt32());
    }
}
