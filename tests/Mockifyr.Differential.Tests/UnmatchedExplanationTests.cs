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
/// Telling the CONSUMER why a request did not match (#457): <c>GET /__sandbox/requests/{id}/near-misses</c>
/// on the partner surface, always; and, for a tenant the host names with <c>--explain-unmatched</c>, an
/// unmatched 404 that carries the same summary and a link to it.
/// </summary>
/// <remarks>
/// The reference engine has no partner surface and no per-tenant switch, so this is a self-test suite per
/// the standing rule; the bare 404 of an unnamed tenant is what the differential suite keeps proving.
/// No Docker.
/// </remarks>
public sealed class UnmatchedExplanationTests : IAsyncLifetime
{
    /// <summary>A stub whose matcher holds a value the consumer must never be shown.</summary>
    private const string Stub =
        """
        {"request":{"method":"POST","urlPath":"/api/orders",
                    "headers":{"X-Partner-Token":{"equalTo":"s3cr3t-expected"}}},
         "response":{"status":201}}
        """;

    private WebApplication? _host;
    private HttpClient? _client;
    private string _acmeKey = string.Empty;
    private string _globexKey = string.Empty;

    public async Task InitializeAsync()
    {
        (_host, _client) = await StartAsync("--sandbox-auth", "true", "--explain-unmatched", "acme", "--mask-headers", "X-Card-Number");
        _acmeKey = await IssueKey(_client, "acme");
        _globexKey = await IssueKey(_client, "globex");
        await CreateFor(_client, "acme", Stub);
        await CreateFor(_client, "globex", Stub);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task A_partner_reads_why_their_own_request_did_not_match_attribute_by_attribute()
    {
        using (await Serve(_acmeKey, "wrong")) { }
        var id = await LastRequestId(_acmeKey);

        var (status, body) = await AsPartner(_acmeKey, $"/__sandbox/requests/{id}/near-misses");

        Assert.Equal(HttpStatusCode.OK, status);
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.GetProperty("wasMatched").GetBoolean());
        var attributes = Attributes(document.RootElement.GetProperty("nearMisses")[0]);
        Assert.True(attributes["method"].GetProperty("matched").GetBoolean());
        Assert.True(attributes["urlPath"].GetProperty("matched").GetBoolean());
        Assert.False(attributes["headers['X-Partner-Token']"].GetProperty("matched").GetBoolean());
        Assert.Equal("wrong", attributes["headers['X-Partner-Token']"].GetProperty("actual").GetString());
        AssertRevealsNoStub(body);
    }

    [Fact]
    public async Task A_matched_request_answers_was_matched_rather_than_an_error()
    {
        using (var served = await Serve(_acmeKey, "s3cr3t-expected"))
        {
            Assert.Equal(HttpStatusCode.Created, served.StatusCode);
        }

        var (status, body) = await AsPartner(_acmeKey, $"/__sandbox/requests/{await LastRequestId(_acmeKey)}/near-misses");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("wasMatched").GetBoolean());
    }

    [Fact]
    public async Task Another_tenants_request_id_is_not_found_and_an_unknown_one_is_not_found_either()
    {
        using (await Serve(_acmeKey, "wrong")) { }
        var acmeRequest = await LastRequestId(_acmeKey);

        Assert.Equal(HttpStatusCode.NotFound, (await AsPartner(_globexKey, $"/__sandbox/requests/{acmeRequest}/near-misses")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await AsPartner(_acmeKey, $"/__sandbox/requests/{Guid.NewGuid()}/near-misses")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await AsPartner(_acmeKey, "/__sandbox/requests/not-a-guid/near-misses")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client!.GetAsync($"/__sandbox/requests/{acmeRequest}/near-misses")).StatusCode);
    }

    [Fact]
    public async Task A_named_tenants_unmatched_404_explains_itself_and_links_to_the_partner_surface()
    {
        using var response = await Serve(_acmeKey, "wrong", cardNumber: "4111111111111111");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.Equal("No stub matched this request", root.GetProperty("title").GetString());

        var id = await LastRequestId(_acmeKey);
        Assert.Equal(id, root.GetProperty("requestId").GetString());
        Assert.Equal($"/__sandbox/requests/{id}/near-misses", root.GetProperty("nearMisses").GetString());
        var attributes = Attributes(root.GetProperty("closest")[0]);
        Assert.False(attributes["headers['X-Partner-Token']"].GetProperty("matched").GetBoolean());
        Assert.Equal("wrong", attributes["headers['X-Partner-Token']"].GetProperty("actual").GetString());
        AssertRevealsNoStub(body);
        // The link resolves, to the same verdict.
        Assert.Equal(HttpStatusCode.OK, (await AsPartner(_acmeKey, root.GetProperty("nearMisses").GetString()!)).Status);
        // A header the operator keeps out of the journal is not echoed back here either.
        Assert.DoesNotContain("4111111111111111", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_masked_header_a_stub_matches_on_is_explained_with_the_placeholder()
    {
        await CreateFor(_client!, "acme",
            """{"request":{"method":"GET","urlPath":"/api/cards","headers":{"X-Card-Number":{"equalTo":"5500000000000004"}}},"response":{"status":200}}""");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/cards");
        request.Headers.Add("X-Api-Key", _acmeKey);
        request.Headers.Add("X-Card-Number", "4111111111111111");
        using var response = await _client!.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("4111111111111111", body, StringComparison.Ordinal);
        Assert.DoesNotContain("5500000000000004", body, StringComparison.Ordinal);
        Assert.Contains("\"actual\":\"***\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tenant_the_host_does_not_name_keeps_the_bare_404()
    {
        using var response = await Serve(_globexKey, "wrong");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        // ...while its partner surface still answers the question.
        var (status, _) = await AsPartner(_globexKey, $"/__sandbox/requests/{await LastRequestId(_globexKey)}/near-misses");
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Without_the_flag_every_unmatched_404_is_bare()
    {
        var (host, client) = await StartAsync();
        try
        {
            await CreateFor(client, "default", Stub);
            using var response = await client.PostAsync("/api/orders", new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task With_the_journal_disabled_the_404_still_explains_but_links_nowhere()
    {
        var (host, client) = await StartAsync("--journal-disabled", "true", "--explain-unmatched", "default");
        try
        {
            await CreateFor(client, "default", Stub);
            using var response = await client.PostAsync("/api/orders", new StringContent("{}", Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(document.RootElement.TryGetProperty("nearMisses", out _));
            Assert.False(document.RootElement.TryGetProperty("requestId", out _));
            Assert.Equal(1, document.RootElement.GetProperty("closest").GetArrayLength());
        }
        finally
        {
            client.Dispose();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_request_near_nothing_says_so()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/nothing/here");
        request.Headers.Add("X-Api-Key", _acmeKey);
        await DeleteAllFor("acme");
        using var response = await _client!.SendAsync(request);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("closest").GetArrayLength());
        Assert.Contains("models no operation", document.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    private static void AssertRevealsNoStub(string body)
    {
        Assert.DoesNotContain("s3cr3t-expected", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stubId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"expected\"", body, StringComparison.Ordinal);
    }

    private static Dictionary<string, JsonElement> Attributes(JsonElement nearMiss) =>
        nearMiss.GetProperty("attributes").EnumerateArray().ToDictionary(a => a.GetProperty("attribute").GetString()!, a => a.Clone());

    private async Task<HttpResponseMessage> Serve(string key, string token, string? cardNumber = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Api-Key", key);
        request.Headers.Add("X-Partner-Token", token);
        if (cardNumber is not null) request.Headers.Add("X-Card-Number", cardNumber);
        return await _client!.SendAsync(request);
    }

    private async Task<string> LastRequestId(string key)
    {
        var (_, body) = await AsPartner(key, "/__sandbox/requests");
        return JsonDocument.Parse(body).RootElement.GetProperty("requests")[0].GetProperty("id").GetString()!;
    }

    private async Task<(HttpStatusCode Status, string Body)> AsPartner(string key, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", key);
        using var response = await _client!.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task DeleteAllFor(string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings/reset");
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await _client!.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"POST /__admin/mappings/reset → {(int)response.StatusCode}");
    }

    private static async Task<string> IssueKey(HttpClient client, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/apikeys")
        {
            Content = new StringContent($$"""{"name":"{{tenant}}-partner"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString()!;
    }

    private static async Task CreateFor(HttpClient client, string tenant, string stubJson)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
        {
            Content = new StringContent(stubJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<(WebApplication Host, HttpClient Client)> StartAsync(params string[] flags)
    {
        var host = MockifyrHost.Build(["--port", "0", "--https-port", "0", .. flags]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }
}
