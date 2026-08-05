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
/// Wire validation of near-miss diagnostics over the admin API (#288).
/// </summary>
/// <remarks>
/// <para>
/// The reference engine answers this question in the body of its 404. Mockifyr answers it as an admin
/// query instead, so the served 404 stays byte-identical to what the differential suite already proves
/// and computing a diagnostic never touches the serve path. Only the ranking is comparable between the
/// two engines — the shape is not — so this is a self-test suite per the standing rule.
/// </para>
/// <para>Needs no Docker.</para>
/// </remarks>
public sealed class NearMissAdminTests
{
    private const string Stub =
        """
        {"request":{"method":"POST","urlPath":"/api/orders",
                    "headers":{"X-Api-Key":{"equalTo":"secret"}}},
         "response":{"status":201}}
        """;

    [Fact]
    public async Task An_unmatched_request_explains_itself_attribute_by_attribute()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        // The classic afternoon-waster: right path, right body, wrong header value.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Api-Key", "wrong");
        using (await client.SendAsync(request)) { }

        var id = await LastRequestIdAsync(client);
        using var response = await client.GetAsync($"/__admin/requests/{id}/near-misses");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("wasMatched").GetBoolean());

        var near = document.RootElement.GetProperty("nearMisses")[0];
        var attributes = near.GetProperty("attributes").EnumerateArray()
            .ToDictionary(a => a.GetProperty("attribute").GetString()!, a => a);

        // The stub was written with urlPath, so that is the slot the diagnostic names.
        Assert.True(attributes["urlPath"].GetProperty("matched").GetBoolean());
        Assert.Equal("/api/orders", attributes["urlPath"].GetProperty("actual").GetString());
        Assert.True(attributes["method"].GetProperty("matched").GetBoolean());
        Assert.False(attributes["headers['X-Api-Key']"].GetProperty("matched").GetBoolean());
        Assert.Equal("wrong", attributes["headers['X-Api-Key']"].GetProperty("actual").GetString());
    }

    [Fact]
    public async Task The_stub_that_was_expected_rides_along()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);
        using (await client.GetAsync("/api/orders")) { }

        var id = await LastRequestIdAsync(client);
        using var response = await client.GetAsync($"/__admin/requests/{id}/near-misses");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var near = document.RootElement.GetProperty("nearMisses")[0];

        // The attribute names are the mapping JSON's own vocabulary, so the expected side is the stub's
        // request block: a reader points at the line rather than at an index.
        Assert.Equal("POST", near.GetProperty("expected").GetProperty("method").GetString());
        Assert.NotEqual(Guid.Empty, near.GetProperty("stubId").GetGuid());
    }

    [Fact]
    public async Task A_hypothetical_request_can_be_explained_before_a_client_exists()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        using var response = await client.PostAsync("/__admin/near-misses/request", Json(
            """{"method":"POST","url":"/api/orders","headers":{"X-Api-Key":"nope"},"body":"{}"}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var attributes = document.RootElement.GetProperty("nearMisses")[0].GetProperty("attributes")
            .EnumerateArray().ToDictionary(a => a.GetProperty("attribute").GetString()!, a => a);

        // Debugging a stub before the client is wired up is the other half of the workflow, and it must
        // not require sending traffic just to have something to ask about.
        Assert.False(attributes["headers['X-Api-Key']"].GetProperty("matched").GetBoolean());
        Assert.Equal("nope", attributes["headers['X-Api-Key']"].GetProperty("actual").GetString());
    }

    [Fact]
    public async Task Near_misses_are_ranked_closest_first()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);
        await CreateAsync(client,
            """{"request":{"method":"DELETE","urlPath":"/somewhere/else"},"response":{"status":204}}""");

        using var response = await client.PostAsync("/__admin/near-misses/request", Json(
            """{"method":"POST","url":"/api/orders","headers":{"X-Api-Key":"nope"}}"""));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var misses = document.RootElement.GetProperty("nearMisses").EnumerateArray().ToList();

        // The stub that failed one attribute must come before the one that failed everything, or the
        // ranking is decoration.
        Assert.True(misses[0].GetProperty("distance").GetDouble() < misses[1].GetProperty("distance").GetDouble());
        Assert.Equal("POST", misses[0].GetProperty("expected").GetProperty("method").GetString());
    }

    [Fact]
    public async Task A_matched_request_says_so_and_still_answers()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, """{"request":{"method":"GET","url":"/ok"},"response":{"status":200}}""");
        using (await client.GetAsync("/ok")) { }

        var id = await LastRequestIdAsync(client);
        using var response = await client.GetAsync($"/__admin/requests/{id}/near-misses");

        // Asking why a *matched* request did not match is a reasonable mistake to make; answering with
        // wasMatched rather than an error is how the caller finds out they are chasing the wrong entry.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("wasMatched").GetBoolean());
    }

    [Fact]
    public async Task One_tenants_stubs_never_appear_in_anothers_diagnosis()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateForAsync(client, "acme", Stub);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/near-misses/request")
        {
            Content = Json("""{"method":"POST","url":"/api/orders"}"""),
        };
        request.Headers.Add("X-Mockifyr-Tenant", "globex");
        using var response = await client.SendAsync(request);

        // A diagnostic that leaked another tenant's stub ids and request patterns would be a data leak
        // wearing a helpful face.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Empty(document.RootElement.GetProperty("nearMisses").EnumerateArray());
    }

    [Fact]
    public async Task An_unknown_request_id_is_a_404()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var unknown = await client.GetAsync($"/__admin/requests/{Guid.NewGuid()}/near-misses");
        using var garbled = await client.GetAsync("/__admin/requests/not-a-guid/near-misses");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, garbled.StatusCode);
    }

    [Fact]
    public async Task A_candidate_request_without_a_url_is_refused()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.PostAsync("/__admin/near-misses/request", Json("""{"method":"GET"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("NearMiss.InvalidBody",
            document.RootElement.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_served_404_is_still_a_bare_404()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, Stub);

        using var response = await client.GetAsync("/api/orders");

        // The whole point of putting diagnostics on the admin surface: the response a client receives is
        // unchanged, so the differential suite keeps proving what it proved before.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> LastRequestIdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/__admin/requests"));
        return document.RootElement.GetProperty("requests")[0].GetProperty("id").GetString()!;
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

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
