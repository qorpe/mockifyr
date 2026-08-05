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
/// Wire validation of drift against reality (#287): record against a live upstream, then ask whether
/// the stubs already authored would have answered the way it did.
/// </summary>
/// <remarks>
/// <para>
/// The "upstream" is a second Mockifyr host, which is the honest way to run this without a network: it
/// is a real HTTP server returning real responses over a real socket, and the host under test has no
/// idea what is on the other end.
/// </para>
/// <para>Self-tested; no oracle has this concept. Needs no Docker.</para>
/// </remarks>
public sealed class RecordingDriftTests
{
    [Fact]
    public async Task A_stub_that_still_agrees_with_the_upstream_reports_nothing()
    {
        await using var upstream = await StartAsync();
        using var upstreamClient = Client(upstream);
        await CreateAsync(upstreamClient,
            """{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"body":"{\"id\":\"real\",\"total\":42}"}}""");

        await using var host = await StartAsync();
        using var client = Client(host);
        await CreateAsync(client,
            """{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"body":"{\"id\":\"stub\",\"total\":1}"}}""");

        await RecordAsync(client, BaseAddress(upstream), "/orders/1");
        var report = await VerifyAsync(client);

        // Different values, same shape. A report that fired on "id: stub vs real" would fire on every
        // exchange and mean nothing.
        Assert.True(report.RootElement.GetProperty("agrees").GetBoolean(),
            report.RootElement.GetProperty("findings").ToString());
        Assert.Equal(1, report.RootElement.GetProperty("exchanges").GetInt32());
    }

    [Fact]
    public async Task A_field_the_upstream_grew_is_reported()
    {
        await using var upstream = await StartAsync();
        using var upstreamClient = Client(upstream);
        await CreateAsync(upstreamClient,
            """{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"body":"{\"id\":\"a\",\"total\":42,\"currency\":\"EUR\"}"}}""");

        await using var host = await StartAsync();
        using var client = Client(host);
        await CreateAsync(client,
            """{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"body":"{\"id\":\"a\",\"total\":1}"}}""");

        await RecordAsync(client, BaseAddress(upstream), "/orders/1");
        var report = await VerifyAsync(client);

        // The whole point: the real API grew a field, the mock did not, and every test stayed green.
        var finding = report.RootElement.GetProperty("findings").EnumerateArray().Single();
        Assert.Equal("fieldMissing", finding.GetProperty("kind").GetString());
        Assert.Equal("/currency", finding.GetProperty("pointer").GetString());
    }

    [Fact]
    public async Task A_status_the_upstream_changed_is_reported()
    {
        await using var upstream = await StartAsync();
        using var upstreamClient = Client(upstream);
        await CreateAsync(upstreamClient,
            """{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":202}}""");

        await using var host = await StartAsync();
        using var client = Client(host);
        await CreateAsync(client,
            """{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200}}""");

        await RecordAsync(client, BaseAddress(upstream), "/orders/1");
        var report = await VerifyAsync(client);

        var finding = report.RootElement.GetProperty("findings").EnumerateArray().Single();
        Assert.Equal("statusDiffers", finding.GetProperty("kind").GetString());
        Assert.Contains("202", finding.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task An_endpoint_with_no_stub_at_all_is_reported()
    {
        await using var upstream = await StartAsync();
        using var upstreamClient = Client(upstream);
        await CreateAsync(upstreamClient,
            """{"request":{"method":"GET","urlPath":"/orders/2"},"response":{"status":200,"body":"{\"id\":\"a\"}"}}""");

        await using var host = await StartAsync();
        using var client = Client(host);

        await RecordAsync(client, BaseAddress(upstream), "/orders/2");
        var report = await VerifyAsync(client);

        // A gap in the mock rather than a difference in it: the client is exercising something nobody
        // has modelled.
        var finding = report.RootElement.GetProperty("findings").EnumerateArray().Single();
        Assert.Equal("noStub", finding.GetProperty("kind").GetString());
        Assert.Equal("/orders/2", finding.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Verifying_serves_nothing_and_advances_nothing()
    {
        await using var upstream = await StartAsync();
        using var upstreamClient = Client(upstream);
        await CreateAsync(upstreamClient,
            """{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"body":"{\"id\":\"a\"}"}}""");

        await using var host = await StartAsync();
        using var client = Client(host);
        await CreateAsync(client,
            """
            {"scenarioName":"s","requiredScenarioState":"Started","newScenarioState":"Advanced",
             "request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"body":"{\"id\":\"a\"}"}}
            """);

        await RecordAsync(client, BaseAddress(upstream), "/orders/1");

        var journalBefore = await CountAsync(client);
        await VerifyAsync(client);
        await VerifyAsync(client);

        // A diagnostic that journaled its own probing, or walked a scenario forward, would change the
        // system it is meant to be describing — and the second run would answer differently from the
        // first for no reason the reader could see.
        Assert.Equal(journalBefore, await CountAsync(client));
        using var scenarios = JsonDocument.Parse(await client.GetStringAsync("/__admin/scenarios"));
        Assert.Equal("Started", scenarios.RootElement.GetProperty("scenarios")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_tenant_sees_only_its_own_recording()
    {
        await using var upstream = await StartAsync();
        using var upstreamClient = Client(upstream);
        await CreateAsync(upstreamClient,
            """{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"body":"{\"id\":\"a\",\"extra\":1}"}}""");

        await using var host = await StartAsync();
        using var client = Client(host);
        await CreateForAsync(client, "acme",
            """{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"body":"{\"id\":\"a\"}"}}""");

        await RecordForAsync(client, "acme", BaseAddress(upstream), "/orders/1");

        using var acme = await VerifyForAsync(client, "acme");
        using var globex = await VerifyForAsync(client, "globex");

        Assert.NotEmpty(acme.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Equal(0, globex.RootElement.GetProperty("exchanges").GetInt32());
        Assert.True(globex.RootElement.GetProperty("agrees").GetBoolean());
    }

    [Fact]
    public async Task Verifying_without_a_recording_answers_rather_than_erroring()
    {
        await using var host = await StartAsync();
        using var client = Client(host);

        var report = await VerifyAsync(client);

        // Asking before recording is a reasonable mistake; answering "nothing captured" is how the
        // caller finds out, rather than a 4xx they have to look up.
        Assert.False(report.RootElement.GetProperty("recording").GetBoolean());
        Assert.Equal(0, report.RootElement.GetProperty("exchanges").GetInt32());
        Assert.True(report.RootElement.GetProperty("agrees").GetBoolean());
    }

    private static async Task RecordAsync(HttpClient client, string target, string path)
    {
        using var started = await client.PostAsync("/__admin/recordings/start", Json($$"""{"targetBaseUrl":"{{target}}"}"""));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        using var proxied = await client.GetAsync(path);
        Assert.True(proxied.IsSuccessStatusCode || proxied.StatusCode == HttpStatusCode.Accepted);
    }

    private static async Task RecordForAsync(HttpClient client, string tenant, string target, string path)
    {
        using var start = new HttpRequestMessage(HttpMethod.Post, "/__admin/recordings/start")
        {
            Content = Json($$"""{"targetBaseUrl":"{{target}}"}"""),
        };
        start.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var started = await client.SendAsync(start);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        using var proxy = new HttpRequestMessage(HttpMethod.Get, path);
        proxy.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var proxied = await client.SendAsync(proxy);
    }

    private static async Task<JsonDocument> VerifyAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/__admin/recordings/verify", Json("{}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> VerifyForAsync(HttpClient client, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/recordings/verify") { Content = Json("{}") };
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<int> CountAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/__admin/requests/count", Json("{}"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }

    private static async Task CreateAsync(HttpClient client, string stubJson)
    {
        using var response = await client.PostAsync("/__admin/mappings", Json(stubJson));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task CreateForAsync(HttpClient client, string tenant, string stubJson)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings") { Content = Json(stubJson) };
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<WebApplication> StartAsync()
    {
        var app = MockifyrHost.Build(["--port", "0"]);
        await app.StartAsync();
        return app;
    }

    private static string BaseAddress(WebApplication app) =>
        app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(BaseAddress(app)) };
}
