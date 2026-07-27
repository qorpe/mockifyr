using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire-level self-tests for the <c>state</c> response directive (G19b, ADR 0011) — no oracle
/// exists (WireMock has no dynamic CRUD state). A real <see cref="HttpClient"/> drives the full
/// sandbox loop against authored stubs: POST creates a document that GET then returns, PUT
/// updates it, LIST reflects it, DELETE removes it, and an unknown id short-circuits to the
/// configured miss status. Also covered: the admin surface sees the same store, tenants stay
/// isolated, serve-time guards answer 413/422, and a state-free stub proves zero behavior change.
/// </summary>
public sealed class G19bStateDirectiveTests : IAsyncDisposable
{
    private readonly MockifyrKestrelHost _host = new(services =>
        services.AddSingleton(new ResourceOptions(MaxBodyBytes: 4096)));

    private readonly HttpClient _client;

    public G19bStateDirectiveTests()
    {
        _client = new HttpClient { BaseAddress = new Uri(_host.BaseAddress) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private async Task LoadStubAsync(string stubJson, string tenant = "acme")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings");
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        request.Content = new StringContent(stubJson, Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method, string path, string? body = null, string tenant = "acme")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task LoadOrderStubsAsync(string tenant = "acme")
    {
        await LoadStubAsync("""
            {"request":{"method":"POST","urlPath":"/api/orders"},
             "response":{"status":201,"headers":{"Content-Type":"application/json"},
               "body":"{\"id\":\"{{state.id}}\",\"order\":{{state.body}} }",
               "state":{"operation":"create","collection":"orders"}}}
            """, tenant);
        await LoadStubAsync("""
            {"request":{"method":"GET","urlPathPattern":"/api/orders/[^/]+"},
             "response":{"status":200,"headers":{"Content-Type":"application/json"},
               "body":"{{state.body}}",
               "state":{"operation":"read","collection":"orders","id":"{{request.pathSegments.[2]}}"}}}
            """, tenant);
        await LoadStubAsync("""
            {"request":{"method":"PUT","urlPathPattern":"/api/orders/[^/]+"},
             "response":{"status":200,"headers":{"Content-Type":"application/json"},
               "body":"{{state.body}}",
               "state":{"operation":"update","collection":"orders","id":"{{request.pathSegments.[2]}}"}}}
            """, tenant);
        await LoadStubAsync("""
            {"request":{"method":"GET","urlPath":"/api/orders"},
             "response":{"status":200,"headers":{"Content-Type":"application/json"},
               "body":"{\"count\":{{state.count}},\"items\":{{state.list}} }",
               "state":{"operation":"list","collection":"orders"}}}
            """, tenant);
        await LoadStubAsync("""
            {"request":{"method":"DELETE","urlPathPattern":"/api/orders/[^/]+"},
             "response":{"status":204,
               "state":{"operation":"delete","collection":"orders","id":"{{request.pathSegments.[2]}}"}}}
            """, tenant);
    }

    [Fact]
    public async Task Post_creates_what_get_returns_then_put_list_delete_complete_the_loop()
    {
        await LoadOrderStubsAsync();

        // CREATE: the response renders the generated id and echoes the stored document.
        var (createStatus, createBody) = await SendAsync(HttpMethod.Post, "/api/orders", """{"item":"book","qty":1}""");
        Assert.Equal(HttpStatusCode.Created, createStatus);
        string id;
        using (var doc = JsonDocument.Parse(createBody))
        {
            id = doc.RootElement.GetProperty("id").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.Equal("book", doc.RootElement.GetProperty("order").GetProperty("item").GetString());
        }

        // READ: the same document comes back verbatim.
        var (getStatus, getBody) = await SendAsync(HttpMethod.Get, $"/api/orders/{id}");
        Assert.Equal(HttpStatusCode.OK, getStatus);
        Assert.Equal("""{"item":"book","qty":1}""", getBody);

        // UPDATE: the request body replaces the document.
        var (putStatus, putBody) = await SendAsync(HttpMethod.Put, $"/api/orders/{id}", """{"item":"book","qty":2}""");
        Assert.Equal(HttpStatusCode.OK, putStatus);
        Assert.Equal("""{"item":"book","qty":2}""", putBody);
        var (_, reread) = await SendAsync(HttpMethod.Get, $"/api/orders/{id}");
        Assert.Equal("""{"item":"book","qty":2}""", reread);

        // LIST: count and items reflect live state.
        var (listStatus, listBody) = await SendAsync(HttpMethod.Get, "/api/orders");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        using (var doc = JsonDocument.Parse(listBody))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("count").GetInt32());
            Assert.Equal("book", doc.RootElement.GetProperty("items")[0].GetProperty("item").GetString());
        }

        // The ADMIN surface reads the same store — the serve path and /__admin/resources agree.
        var (adminStatus, adminBody) = await SendAsync(HttpMethod.Get, $"/__admin/resources/orders/{id}");
        Assert.Equal(HttpStatusCode.OK, adminStatus);
        Assert.Contains("\"qty\":2", adminBody.Replace(" ", ""));

        // DELETE, then the miss short-circuit answers for the now-unknown id.
        var (deleteStatus, _) = await SendAsync(HttpMethod.Delete, $"/api/orders/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteStatus);
        var (missStatus, _) = await SendAsync(HttpMethod.Get, $"/api/orders/{id}");
        Assert.Equal(HttpStatusCode.NotFound, missStatus);
        var (deleteMiss, _) = await SendAsync(HttpMethod.Delete, $"/api/orders/{id}");
        Assert.Equal(HttpStatusCode.NotFound, deleteMiss);
    }

    [Fact]
    public async Task The_miss_status_is_configurable_per_stub()
    {
        await LoadStubAsync("""
            {"request":{"method":"GET","urlPathPattern":"/api/archived/[^/]+"},
             "response":{"status":200,"body":"{{state.body}}",
               "state":{"operation":"read","collection":"archive","id":"{{request.pathSegments.[2]}}","missStatus":410}}}
            """);

        var (status, _) = await SendAsync(HttpMethod.Get, "/api/archived/long-gone");
        Assert.Equal(HttpStatusCode.Gone, status);
    }

    [Fact]
    public async Task Tenants_never_see_each_others_state()
    {
        await LoadOrderStubsAsync("acme");
        await LoadOrderStubsAsync("globex");

        var (_, createBody) = await SendAsync(HttpMethod.Post, "/api/orders", """{"owner":"acme"}""", tenant: "acme");
        string id;
        using (var doc = JsonDocument.Parse(createBody))
        {
            id = doc.RootElement.GetProperty("id").GetString()!;
        }

        var (otherStatus, _) = await SendAsync(HttpMethod.Get, $"/api/orders/{id}", tenant: "globex");
        Assert.Equal(HttpStatusCode.NotFound, otherStatus);

        var (otherList, otherListBody) = await SendAsync(HttpMethod.Get, "/api/orders", tenant: "globex");
        Assert.Equal(HttpStatusCode.OK, otherList);
        Assert.Contains("\"count\":0", otherListBody);
    }

    [Fact]
    public async Task Serve_time_guards_answer_413_over_the_cap_and_422_for_non_json()
    {
        await LoadOrderStubsAsync();

        var (tooLarge, _) = await SendAsync(HttpMethod.Post, "/api/orders",
            $$"""{"pad":"{{new string('x', 5000)}}"}""");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge);

        var (notJson, _) = await SendAsync(HttpMethod.Post, "/api/orders", "{definitely not json");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notJson);

        // Nothing landed from either refusal.
        var (_, listBody) = await SendAsync(HttpMethod.Get, "/api/orders");
        Assert.Contains("\"count\":0", listBody);
    }

    [Fact]
    public async Task A_state_free_stub_behaves_exactly_as_before_and_touches_no_state()
    {
        await LoadStubAsync("""
            {"request":{"method":"GET","urlPath":"/plain"},"response":{"status":200,"body":"static"}}
            """);

        var (status, body) = await SendAsync(HttpMethod.Get, "/plain");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("static", body);

        var (_, collections) = await SendAsync(HttpMethod.Get, "/__admin/resources");
        Assert.Contains("\"collections\":[]", collections.Replace(" ", ""));
    }
}
