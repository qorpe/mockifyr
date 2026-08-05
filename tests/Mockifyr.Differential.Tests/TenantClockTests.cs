using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire validation of tenant clock control (#290) against a real host.
/// </summary>
/// <remarks>
/// <para>
/// No oracle: the reference engine has no clock surface, so per the G18 honesty rule this is a
/// self-test suite. What it can prove absolutely is that the output is <em>deterministic</em>, which is
/// the whole point — before this, <c>{{now}}</c> could only be checked for plausibility, and
/// <c>docs/parity/g2-response.md</c> records it as a racy helper for exactly that reason.
/// </para>
/// <para>Needs no Docker — the host runs in-process on an ephemeral port.</para>
/// </remarks>
public sealed class TenantClockTests
{
    private const string Frozen = "2027-01-01T00:00:00+00:00";

    private const string NowStub =
        """
        {"request":{"method":"GET","url":"/when"},
         "response":{"status":200,"body":"{{now format='yyyy-MM-dd'}}","transformers":["response-template"]}}
        """;

    [Fact]
    public async Task A_frozen_clock_makes_now_deterministic()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, NowStub);

        using var set = await client.PutAsync("/__admin/clock", Json($$"""{"frozenAt":"{{Frozen}}"}"""));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // Two reads with a real second between them: the point of freezing is that they agree.
        var first = await client.GetStringAsync("/when");
        await Task.Delay(1100);
        var second = await client.GetStringAsync("/when");

        Assert.Equal("2027-01-01", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Clearing_the_clock_returns_the_tenant_to_real_time()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, NowStub);

        using var set = await client.PutAsync("/__admin/clock", Json($$"""{"frozenAt":"{{Frozen}}"}"""));
        Assert.Equal("2027-01-01", await client.GetStringAsync("/when"));

        using var cleared = await client.DeleteAsync("/__admin/clock");
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        Assert.Equal(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), await client.GetStringAsync("/when"));
    }

    [Fact]
    public async Task An_offset_clock_still_runs()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, NowStub);

        using var set = await client.PutAsync("/__admin/clock", Json("""{"offsetSeconds":86400}"""));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        Assert.Equal(DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd"), await client.GetStringAsync("/when"));
    }

    [Fact]
    public async Task One_tenants_clock_does_not_move_another()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateForAsync(client, "acme", NowStub);
        await CreateForAsync(client, "globex", NowStub);

        using var set = await SendAsync(client, HttpMethod.Put, "/__admin/clock", "acme",
            $$"""{"frozenAt":"{{Frozen}}"}""");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        Assert.Equal("2027-01-01", await GetAsync(client, "/when", "acme"));
        Assert.Equal(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), await GetAsync(client, "/when", "globex"));
    }

    [Fact]
    public async Task A_minted_token_expires_on_the_tenants_terms()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client,
            """
            {"request":{"method":"GET","url":"/token"},
             "response":{"status":200,"body":"{{jwt maxAge='1 hours'}}","transformers":["response-template"]}}
            """);

        using var set = await client.PutAsync("/__admin/clock", Json($$"""{"frozenAt":"{{Frozen}}"}"""));

        // The body and the token have to agree about what time it is — a response that said one thing
        // while its own token said another would be worse than having no clock control.
        var token = (await client.GetStringAsync("/token")).Trim('"');
        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Base64Url(token.Split('.')[1])));
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.RootElement.GetProperty("iat").GetInt64());
        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.RootElement.GetProperty("exp").GetInt64());

        Assert.Equal(DateTimeOffset.Parse(Frozen), issuedAt);
        Assert.Equal(DateTimeOffset.Parse(Frozen).AddHours(1), expires);
    }

    [Fact]
    public async Task The_journal_records_when_things_really_happened()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, NowStub);

        using var set = await client.PutAsync("/__admin/clock", Json($$"""{"frozenAt":"{{Frozen}}"}"""));
        using (await client.GetAsync("/when")) { }

        using var journal = await client.GetAsync("/__admin/requests");
        using var document = JsonDocument.Parse(await journal.Content.ReadAsStringAsync());
        var logged = document.RootElement.GetProperty("requests")[0].GetProperty("loggedDate").GetString();

        // A forensic record that follows a test's fiction is worthless. The audit trail, the journal and
        // the message inbox record when something actually happened, whatever the tenant believes.
        Assert.StartsWith(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), logged);
    }

    [Fact]
    public async Task The_clock_reads_back_as_it_was_set()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        Assert.Equal("real", await ModeAsync(client));

        using var frozen = await client.PutAsync("/__admin/clock", Json($$"""{"frozenAt":"{{Frozen}}"}"""));
        Assert.Equal("frozen", await ModeAsync(client));

        using var offset = await client.PutAsync("/__admin/clock", Json("""{"offsetSeconds":-3600}"""));
        Assert.Equal("offset", await ModeAsync(client));

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/__admin/clock"));
        Assert.Equal(-3600, doc.RootElement.GetProperty("offsetSeconds").GetInt64());

        using var cleared = await client.DeleteAsync("/__admin/clock");
        Assert.Equal("real", await ModeAsync(client));
    }

    [Fact]
    public async Task A_body_asking_for_both_modes_is_refused()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.PutAsync(
            "/__admin/clock", Json($$"""{"frozenAt":"{{Frozen}}","offsetSeconds":60}"""));

        // Refused rather than resolved: "frozen and drifting" means one thing to whoever wrote it and
        // another to whoever reads it next.
        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Clock.Ambiguous", doc.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal("real", await ModeAsync(client));
    }

    [Fact]
    public async Task A_malformed_instant_is_refused_and_changes_nothing()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.PutAsync("/__admin/clock", Json("""{"frozenAt":"yesterday"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Equal("real", await ModeAsync(client));
    }

    [Fact]
    public async Task A_host_with_no_clock_set_renders_the_real_time()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, NowStub);

        // The default path must be untouched — this is the assertion that says the feature costs
        // nothing to a host that never uses it.
        Assert.Equal(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), await client.GetStringAsync("/when"));
    }

    private static byte[] Base64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private static async Task<string> ModeAsync(HttpClient client)
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/__admin/clock"));
        return doc.RootElement.GetProperty("mode").GetString()!;
    }

    private static async Task CreateAsync(HttpClient client, string stubJson)
    {
        using var response = await client.PostAsync("/__admin/mappings", Json(stubJson));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task CreateForAsync(HttpClient client, string tenant, string stubJson)
    {
        using var response = await SendAsync(client, HttpMethod.Post, "/__admin/mappings", tenant, stubJson);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string tenant, string body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = Json(body) };
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetAsync(HttpClient client, string path, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<WebApplication> StartAsync()
    {
        var app = MockifyrHost.Build(["--port", "0"]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
