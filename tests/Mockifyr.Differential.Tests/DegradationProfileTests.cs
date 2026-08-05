using System.Diagnostics;
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
/// Wire validation of tenant degradation profiles (#289) against a real host: a whole dependency
/// misbehaving, rather than one stub declaring that it does.
/// </summary>
/// <remarks>
/// No oracle — the reference engine has no tenant-wide degradation — so this is a self-test suite. The
/// rates themselves are asserted in <c>DegradationPlanTests</c> over ten thousand samples; what these
/// prove is that the decision reaches the wire, that it reaches only the tenant that asked, and that it
/// never reaches the admin API.
/// </remarks>
public sealed class DegradationProfileTests
{
    private const string Stub =
        """{"request":{"method":"GET","url":"/svc"},"response":{"status":200,"body":"healthy"}}""";

    [Fact]
    public async Task A_degraded_dependency_answers_with_its_error_status()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        Assert.Equal("healthy", await client.GetStringAsync("/svc"));

        using var set = await client.PutAsync("/__admin/degradation",
            Json("""{"errorRate":{"ratio":1.0,"status":503}}"""));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using var degraded = await client.GetAsync("/svc");

        // The stub still matches — that is the point. What changed is the dependency, not the contract.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, degraded.StatusCode);
        Assert.Empty(await degraded.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Latency_is_added_to_every_response()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        using var set = await client.PutAsync("/__admin/degradation", Json("""{"latency":{"fixedMs":300}}"""));

        var stopwatch = Stopwatch.StartNew();
        Assert.Equal("healthy", await client.GetStringAsync("/svc"));
        stopwatch.Stop();

        // Generous lower bound, no upper bound: this asserts the delay happened, not how precisely a
        // shared CI machine can sleep.
        Assert.True(stopwatch.ElapsedMilliseconds >= 250, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_broken_connection_reaches_the_client_as_a_failed_request()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        using var set = await client.PutAsync("/__admin/degradation",
            Json("""{"faultRate":{"ratio":1.0,"fault":"CONNECTION_RESET_BY_PEER"}}"""));

        // The whole point of a fault over a status: the client sees a transport failure, which is the
        // path most retry logic never gets exercised on.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetStringAsync("/svc"));
    }

    [Fact]
    public async Task The_admin_api_is_never_degraded()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        using var set = await client.PutAsync("/__admin/degradation",
            Json("""{"errorRate":{"ratio":1.0,"status":500},"latency":{"fixedMs":0}}"""));

        // If degradation reached the control plane, an operator could degrade a tenant and then be
        // unable to un-degrade it — the profile would be a trap rather than an instrument.
        using var mappings = await client.GetAsync("/__admin/mappings");
        using var health = await client.GetAsync("/__admin/health");
        using var cleared = await client.DeleteAsync("/__admin/degradation");

        Assert.Equal(HttpStatusCode.OK, mappings.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal("healthy", await client.GetStringAsync("/svc"));
    }

    [Fact]
    public async Task One_tenants_outage_leaves_another_healthy()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateForAsync(client, "acme", Stub);
        await CreateForAsync(client, "globex", Stub);

        using var set = await SendAsync(client, HttpMethod.Put, "/__admin/degradation", "acme",
            """{"errorRate":{"ratio":1.0,"status":503}}""");
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // Degrading a shared host for everybody is exactly the failure this feature exists to avoid.
        using var degraded = await GetAsync(client, "/svc", "acme");
        using var healthy = await GetAsync(client, "/svc", "globex");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, degraded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal("healthy", await healthy.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Clearing_the_profile_restores_the_dependency()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        using var set = await client.PutAsync("/__admin/degradation", Json("""{"errorRate":{"ratio":1.0}}"""));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/svc")).StatusCode);

        using var cleared = await client.DeleteAsync("/__admin/degradation");

        // A drill has to be bounded by one call, or it becomes a cleanup project nobody finishes.
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal("healthy", await client.GetStringAsync("/svc"));
        Assert.False(await DegradedAsync(client));
    }

    [Fact]
    public async Task A_seed_is_always_reported_so_a_run_can_be_replayed()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var set = await client.PutAsync("/__admin/degradation", Json("""{"errorRate":{"ratio":0.5}}"""));
        using var document = JsonDocument.Parse(await set.Content.ReadAsStringAsync());
        var generated = document.RootElement.GetProperty("seed").GetInt32();

        using var read = await client.GetAsync("/__admin/degradation");
        using var stored = JsonDocument.Parse(await read.Content.ReadAsStringAsync());

        // Nobody supplies a seed until the run turns up something interesting, by which time it is too
        // late to start recording one. So the host always picks one and says what it picked.
        Assert.Equal(generated, stored.RootElement.GetProperty("seed").GetInt32());
        Assert.True(stored.RootElement.GetProperty("degraded").GetBoolean());
    }

    [Fact]
    public async Task A_supplied_seed_replays_the_same_pattern()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        var first = await StatusSequenceAsync(client);
        var second = await StatusSequenceAsync(client);

        Assert.Equal(first, second);

        // And it is a mixture, not all-or-nothing — a sequence of twenty identical answers would pass an
        // equality check while proving nothing about the generator.
        Assert.Contains(HttpStatusCode.OK, first);
        Assert.Contains(HttpStatusCode.ServiceUnavailable, first);
    }

    [Fact]
    public async Task The_profile_composes_with_a_stubs_own_delay()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client,
            """{"request":{"method":"GET","url":"/slow"},"response":{"status":200,"body":"ok","fixedDelayMilliseconds":200}}""");

        using var set = await client.PutAsync("/__admin/degradation", Json("""{"latency":{"fixedMs":200}}"""));

        var stopwatch = Stopwatch.StartNew();
        Assert.Equal("ok", await client.GetStringAsync("/slow"));
        stopwatch.Stop();

        // A stub that asks for 200 ms still gets 200 ms, plus whatever the dependency is adding today —
        // the profile degrades the stub set, it does not replace it.
        Assert.True(stopwatch.ElapsedMilliseconds >= 350, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("""{"errorRate":{"ratio":1.5}}""", "Degradation.OutOfRange")]
    [InlineData("""{"faultRate":{"ratio":-0.1}}""", "Degradation.OutOfRange")]
    [InlineData("""{"latency":{"fixedMs":-5}}""", "Degradation.OutOfRange")]
    [InlineData("""{"errorRate":{"ratio":0.5,"status":99}}""", "Degradation.OutOfRange")]
    [InlineData("""{"faultRate":{"ratio":0.5,"fault":"KABOOM"}}""", "Degradation.OutOfRange")]
    public async Task A_profile_that_cannot_mean_what_it_says_is_refused(string body, string code)
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        using var response = await client.PutAsync("/__admin/degradation", Json(body));

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());

        // Nothing half-lands: the tenant is exactly as healthy as it was before the bad request.
        Assert.False(await DegradedAsync(client));
        Assert.Equal("healthy", await client.GetStringAsync("/svc"));
    }

    [Fact]
    public async Task An_empty_profile_reads_as_healthy()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        using var set = await client.PutAsync("/__admin/degradation", Json("{}"));

        // A client that always PUTs its whole configuration sends this when it wants nothing; it must
        // not leave the tenant marked as degraded while behaving normally.
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.False(await DegradedAsync(client));
        Assert.Equal("healthy", await client.GetStringAsync("/svc"));
    }

    private static async Task<List<HttpStatusCode>> StatusSequenceAsync(HttpClient client)
    {
        using var set = await client.PutAsync("/__admin/degradation",
            Json("""{"errorRate":{"ratio":0.5,"status":503},"seed":4242}"""));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 20; i++)
        {
            using var response = await client.GetAsync("/svc");
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }

    private static async Task<bool> DegradedAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/__admin/degradation"));
        return document.RootElement.GetProperty("degraded").GetBoolean();
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

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        return await client.SendAsync(request);
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
