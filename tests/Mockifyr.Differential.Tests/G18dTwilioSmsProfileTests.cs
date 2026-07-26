using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;
using Twilio.Clients;
using Twilio.Rest.Api.V2010.Account;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Self-tests for the Twilio SMS provider profile (G18d, ADR 0009). No oracle exists — WireMock has
/// no provider emulation — so the load-bearing claim is verified with the <b>official Twilio C#
/// SDK</b>: pointed at Mockifyr, <c>MessageResource.CreateAsync</c> must succeed and parse our
/// response. Plus wire-level checks: capture into the inbox, Twilio-shaped validation errors,
/// stub-wins-over-profile, and tenant scoping.
/// </summary>
public sealed class G18dTwilioSmsProfileTests
{
    private static async Task<(IAsyncDisposable App, System.Net.Http.HttpClient Client)> StartHostAsync()
    {
        var app = MockifyrHost.Build(["--port", "0", "--sms-profile", "twilio"]);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .First().Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (app, new System.Net.Http.HttpClient { BaseAddress = new Uri(address) });
    }

    /// <summary>
    /// The official SDK builds requests against api.twilio.com; this client keeps the SDK's request
    /// and response handling intact and only redirects the URL at Mockifyr.
    /// </summary>
    private sealed class RedirectingTwilioClient(System.Net.Http.HttpClient http, Uri baseAddress) : ITwilioRestClient
    {
        public string AccountSid => "ACtest";

        public string? Region => null;

        public Twilio.Http.HttpClient HttpClient => throw new NotSupportedException();

        public Twilio.Http.Response Request(Twilio.Http.Request request) => RequestAsync(request).GetAwaiter().GetResult();

        public async Task<Twilio.Http.Response> RequestAsync(Twilio.Http.Request request)
        {
            var original = new Uri(request.ConstructUrl().ToString());
            using var message = new HttpRequestMessage(
                new System.Net.Http.HttpMethod(request.Method.ToString()),
                new Uri(baseAddress, original.PathAndQuery));
            if (request.PostParams.Count > 0)
            {
                message.Content = new FormUrlEncodedContent(
                    request.PostParams.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
            }

            using var response = await http.SendAsync(message);
            return new Twilio.Http.Response(response.StatusCode, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task OfficialSdk_SendsAnSms_AndAcceptsOurResponse()
    {
        var (app, client) = await StartHostAsync();
        await using var _ = app;

        var twilio = new RedirectingTwilioClient(client, client.BaseAddress!);
        var message = await MessageResource.CreateAsync(
            to: new Twilio.Types.PhoneNumber("+905551112233"),
            from: new Twilio.Types.PhoneNumber("+15005550006"),
            body: "Your verification code is 482913",
            client: twilio,
            pathAccountSid: "ACtest");

        // The SDK parsed our resource: sid/status/body round-tripped through Twilio's own model.
        Assert.StartsWith("SM", message.Sid);
        Assert.Equal(MessageResource.StatusEnum.Queued, message.Status);
        Assert.Equal("Your verification code is 482913", message.Body);
        Assert.Equal("+905551112233", message.To);

        // And the send was captured as an SMS envelope, queryable like any message.
        using var list = JsonDocument.Parse(await client.GetStringAsync("/__admin/messages?channel=sms"));
        var captured = Assert.Single(list.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("+905551112233", captured.GetProperty("to")[0].GetString());
        Assert.Equal("+15005550006", captured.GetProperty("from").GetString());
        Assert.Equal(message.Sid, captured.GetProperty("meta").GetProperty("sid").GetString());
        Assert.Equal("twilio", captured.GetProperty("meta").GetProperty("provider").GetString());
    }

    [Theory]
    [InlineData("From=%2B15005550006&Body=hi", 21604)] // missing To
    [InlineData("To=%2B905551112233&Body=hi", 21603)] // missing From/MessagingServiceSid
    [InlineData("To=%2B905551112233&From=%2B15005550006", 21602)] // missing Body
    public async Task MissingFields_AnswerTwilioShapedErrors(string form, int expectedCode)
    {
        var (app, client) = await StartHostAsync();
        await using var _ = app;

        using var response = await client.PostAsync("/2010-04-01/Accounts/ACtest/Messages.json",
            new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, error.RootElement.GetProperty("code").GetInt32());
        Assert.Equal(400, error.RootElement.GetProperty("status").GetInt32());
        Assert.Contains($"/errors/{expectedCode}", error.RootElement.GetProperty("more_info").GetString());
    }

    [Fact]
    public async Task AHandWrittenStub_OnTheSameUrl_StillWins()
    {
        var (app, client) = await StartHostAsync();
        await using var _ = app;

        const string stub =
            """{"request":{"method":"POST","urlPath":"/2010-04-01/Accounts/ACtest/Messages.json"},"response":{"status":503,"body":"stubbed outage"}}""";
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsync("/__admin/mappings", new StringContent(stub, Encoding.UTF8, "application/json"))).StatusCode);

        using var response = await client.PostAsync("/2010-04-01/Accounts/ACtest/Messages.json",
            new StringContent("To=%2B905551112233&From=%2B15005550006&Body=hi", Encoding.UTF8, "application/x-www-form-urlencoded"));

        // The profile stepped aside: the stub's full behavior served, and nothing was captured.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("stubbed outage", await response.Content.ReadAsStringAsync());
        using var count = JsonDocument.Parse(await client.GetStringAsync("/__admin/messages/count?channel=sms"));
        Assert.Equal(0, count.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TenantHeader_ScopesTheCapture()
    {
        var (app, client) = await StartHostAsync();
        await using var _ = app;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/2010-04-01/Accounts/ACtest/Messages.json")
        {
            Content = new StringContent("To=%2B905551112233&From=%2B15005550006&Body=tenant+sms",
                Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Add("X-Mockifyr-Tenant", "acme");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);

        using var fallback = JsonDocument.Parse(await client.GetStringAsync("/__admin/messages?channel=sms"));
        Assert.Empty(fallback.RootElement.GetProperty("messages").EnumerateArray());

        using var acmeRequest = new HttpRequestMessage(HttpMethod.Get, "/__admin/messages?channel=sms");
        acmeRequest.Headers.Add("X-Mockifyr-Tenant", "acme");
        using var acmeResponse = await client.SendAsync(acmeRequest);
        using var acme = JsonDocument.Parse(await acmeResponse.Content.ReadAsStringAsync());
        Assert.Equal("tenant sms",
            Assert.Single(acme.RootElement.GetProperty("messages").EnumerateArray()).GetProperty("body").GetString());
    }

    [Fact]
    public async Task WithoutTheFlag_TheProfileRouteDoesNotExist()
    {
        var app = MockifyrHost.Build(["--port", "0"]);
        await app.StartAsync();
        await using var _ = app;
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .First().Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        using var response = await client.PostAsync("/2010-04-01/Accounts/ACtest/Messages.json",
            new StringContent("To=%2B1&From=%2B2&Body=x", Encoding.UTF8, "application/x-www-form-urlencoded"));

        // No flag → no route: the request falls through to normal mock-serving (404, no stub).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
