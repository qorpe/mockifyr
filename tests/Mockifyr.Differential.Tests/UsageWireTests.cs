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
/// Usage reporting at the wire (#356): what a real host counts for a real key, and what a partner can
/// read about themselves. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class UsageWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--sandbox-auth", "true", "--usage", "true"]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        using var stub = await _client.PostAsync("/__admin/mappings", new StringContent(
            """{"request":{"method":"GET","urlPath":"/orders"},"response":{"status":200,"body":"ok"}}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        // A stub that answers 404 itself, to prove a modelled 404 is not reported as unmatched.
        using var modelled = await _client.PostAsync("/__admin/mappings", new StringContent(
            """{"request":{"method":"GET","urlPath":"/orders/missing"},"response":{"status":404,"body":"gone"}}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, modelled.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<JsonElement> IssueAsync(string body)
    {
        using var response = await _client!.PostAsync(
            "/__admin/apikeys", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> Call(string key, string path, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", key);
        return await _client!.SendAsync(request);
    }

    private async Task<JsonElement> UsageAsync(string query = "")
    {
        using var response = await _client!.GetAsync("/__admin/usage" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Every_outcome_a_key_meets_is_counted_including_the_refusals()
    {
        var key = (await IssueAsync("""{"name":"partner","quotaPerHour":2}""")).GetProperty("key").GetString()!;

        await Call(key, "/orders");
        await Call(key, "/v2/orders");          // unmatched
        await Call(key, "/orders");             // over the quota of 2
        var readOnly = (await IssueAsync("""{"name":"monitor","scope":"read"}""")).GetProperty("key").GetString()!;
        await Call(readOnly, "/orders", HttpMethod.Post);

        var report = (await UsageAsync()).GetProperty("keys").EnumerateArray().ToList();

        var partner = report.Single(k => k.GetProperty("name").GetString() == "partner");
        Assert.Equal(3, partner.GetProperty("total").GetInt32());
        Assert.Equal(1, partner.GetProperty("matched").GetInt32());
        Assert.Equal(1, partner.GetProperty("unmatched").GetInt32());
        Assert.Equal(1, partner.GetProperty("rateLimited").GetInt32());

        var monitor = report.Single(k => k.GetProperty("name").GetString() == "monitor");
        Assert.Equal(1, monitor.GetProperty("forbidden").GetInt32());

        // The unmatched path is named, because that is the integration going wrong.
        Assert.Equal("/v2/orders",
            partner.GetProperty("topUnmatchedPaths").EnumerateArray().Single().GetProperty("path").GetString());
    }

    [Fact]
    public async Task A_stub_that_answers_404_is_not_reported_as_unmatched()
    {
        // A modelled 404 and a call the sandbox does not model at all are opposite findings, and the
        // status code alone cannot tell them apart.
        var key = (await IssueAsync("""{"name":"modelled"}""")).GetProperty("key").GetString()!;

        using var response = await Call(key, "/orders/missing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var entry = (await UsageAsync()).GetProperty("keys").EnumerateArray()
            .Single(k => k.GetProperty("name").GetString() == "modelled");

        Assert.Equal(1, entry.GetProperty("matched").GetInt32());
        Assert.Equal(0, entry.GetProperty("unmatched").GetInt32());
    }

    [Fact]
    public async Task An_unknown_token_is_not_recorded_against_anybody()
    {
        using var refused = await Call("mfk_not-a-key", "/orders");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        Assert.Empty((await UsageAsync()).GetProperty("keys").EnumerateArray());
    }

    [Fact]
    public async Task A_request_with_no_key_at_all_is_not_per_consumer_usage()
    {
        // Anonymous traffic is the journal's business; a usage report is about a consumer.
        using var served = await _client!.GetAsync("/orders");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        Assert.Empty((await UsageAsync()).GetProperty("keys").EnumerateArray());
    }

    [Fact]
    public async Task A_partner_reads_their_own_usage_with_the_key_they_already_hold()
    {
        var key = (await IssueAsync("""{"name":"self-serve"}""")).GetProperty("key").GetString()!;
        await Call(key, "/orders");
        await Call(key, "/v2/orders");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/__sandbox/usage");
        request.Headers.Add("X-Api-Key", key);
        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var entry = body.GetProperty("keys").EnumerateArray()
            .Single(k => k.GetProperty("name").GetString() == "self-serve");
        // Two — the partner's own read of /__sandbox/usage is not traffic. Their self-service surface
        // is a control plane, and counting it would mean looking at your usage changes it.
        Assert.Equal(2, entry.GetProperty("total").GetInt32());
        Assert.Equal("/v2/orders",
            entry.GetProperty("topUnmatchedPaths").EnumerateArray().Single().GetProperty("path").GetString());
    }

    [Fact]
    public async Task Usage_carries_no_headers_or_bodies_at_all()
    {
        // The masking that keeps secrets out of the journal (#227) must not be walkable around by
        // reading usage instead — so this asserts on the shape of the whole document, not one field.
        var key = (await IssueAsync("""{"name":"secretive"}""")).GetProperty("key").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders?token=super-secret");
        request.Headers.Add("X-Api-Key", key);
        request.Headers.Add("Authorization", "Bearer super-secret");
        await _client!.SendAsync(request);

        using var raw = await _client.GetAsync("/__admin/usage");
        var document = await raw.Content.ReadAsStringAsync();

        Assert.DoesNotContain("super-secret", document, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", document, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A host that was not asked to remember (#356).</summary>
public sealed class UsageOffByDefaultTests
{
    [Fact]
    public async Task Without_the_flag_the_report_is_empty_and_serving_is_unaffected()
    {
        var host = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--sandbox-auth", "true"]);
        await host.StartAsync();
        try
        {
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var stub = await client.PostAsync("/__admin/mappings", new StringContent(
                """{"request":{"method":"GET","urlPath":"/orders"},"response":{"status":200,"body":"ok"}}""",
                Encoding.UTF8, "application/json"));
            using var issued = await client.PostAsync("/__admin/apikeys", new StringContent(
                """{"name":"partner"}""", Encoding.UTF8, "application/json"));
            var key = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement
                .GetProperty("key").GetString()!;

            using var call = new HttpRequestMessage(HttpMethod.Get, "/orders");
            call.Headers.Add("X-Api-Key", key);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(call)).StatusCode);

            using var usage = await client.GetAsync("/__admin/usage");
            Assert.Equal(HttpStatusCode.OK, usage.StatusCode);
            Assert.Equal("""{"keys":[]}""", await usage.Content.ReadAsStringAsync());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
