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
/// Embedding a related document at the wire (#378): the served <c>read</c> and <c>list</c> and the
/// admin read, against a real host. Mockifyr-specific — no oracle has a sandbox resource model — so a
/// self-test; no Docker.
/// </summary>
public sealed class ResourceExpansionWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0"]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        // Orders belong to customers, keyed by customerId — the declaration an OpenAPI import derives
        // from /customers/{customerId}/orders, written by hand here.
        using (var relation = await _client.PutAsync("/__admin/relations/orders",
            Json("""{"belongsTo":[{"collection":"customers","via":"customerId"}]}""")))
        {
            Assert.Equal(HttpStatusCode.OK, relation.StatusCode);
        }

        // urlPath, not url: `url` matches the query string too, so /orders would stop matching the
        // moment a caller expands — the request that most wants this feature would 404 (#353).
        foreach (var stub in (string[])[
            """
            {"request":{"method":"GET","urlPathPattern":"/orders/[^/]+"},
             "response":{"status":200,"headers":{"Content-Type":"application/json"},
                         "body":"{{state.body}}",
                         "state":{"operation":"read","collection":"orders","id":"{{request.pathSegments.[1]}}"}}}
            """,
            """
            {"request":{"method":"GET","urlPath":"/orders"},
             "response":{"status":200,"headers":{"Content-Type":"application/json"},
                         "body":"{{state.list}}","state":{"operation":"list","collection":"orders"}}}
            """])
        {
            using var response = await _client.PostAsync("/__admin/mappings", Json(stub));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await PutAsync("customers", "c1", """{"id":"c1","name":"Ada"}""");
        await PutAsync("orders", "o1", """{"total":100,"customerId":"c1"}""");
        await PutAsync("orders", "o2", """{"total":250,"customerId":"c1"}""");
        await PutAsync("orders", "o3", """{"total":9,"customerId":"gone"}""");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task PutAsync(string collection, string id, string body)
    {
        using var response = await _client!.PutAsync($"/__admin/resources/{collection}/{id}", Json(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        using var response = await _client!.GetAsync(path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // ---- the served read -------------------------------------------------------------------------

    [Fact]
    public async Task A_read_embeds_the_parent_the_relation_already_knows_about()
    {
        // The point of the feature: one call instead of a read, a foreign key, a second read and a
        // stitch the consumer had to write themselves.
        var (status, body) = await GetAsync("/orders/o1?_expand=customer");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"total":100,"customerId":"c1","_expand":{"customer":{"id":"c1","name":"Ada"}}}""",
            body);
    }

    [Fact]
    public async Task A_read_without_expand_is_byte_identical_to_what_it_always_answered()
    {
        // The compatibility criterion: this feature is reachable only by asking for it.
        var (status, body) = await GetAsync("/orders/o1");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"total":100,"customerId":"c1"}""", body);
    }

    [Fact]
    public async Task A_parent_that_no_longer_exists_embeds_null_and_the_read_still_succeeds()
    {
        var (status, body) = await GetAsync("/orders/o3?_expand=customer");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"total":9,"customerId":"gone","_expand":{"customer":null}}""", body);
    }

    [Fact]
    public async Task An_unknown_relation_is_refused_rather_than_ignored()
    {
        // A document returned unexpanded is indistinguishable from a typo, and a consumer would debug
        // their own code for an hour before suspecting the query string.
        var (status, _) = await GetAsync("/orders/o1?_expand=custmoer");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task An_empty_expand_asks_for_nothing_rather_than_refusing()
    {
        // The shape a client produces when it builds the URL from a variable nobody set. Refusing it
        // would turn an empty option into an error the caller cannot act on.
        var (status, body) = await GetAsync("/orders/o1?_expand=");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"total":100,"customerId":"c1"}""", body);
    }

    [Fact]
    public async Task Depth_is_refused_by_the_same_rule_rather_than_by_a_special_case()
    {
        var (status, _) = await GetAsync("/orders/o1?_expand=customer.address");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ---- the served list -------------------------------------------------------------------------

    [Fact]
    public async Task A_list_expands_every_document_it_returns()
    {
        var (status, body) = await GetAsync("/orders?_expand=customer");

        Assert.Equal(HttpStatusCode.OK, status);
        var items = JsonDocument.Parse(body).RootElement.EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal("Ada", items[0].GetProperty("_expand").GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal("Ada", items[1].GetProperty("_expand").GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("_expand").GetProperty("customer").ValueKind);
    }

    [Fact]
    public async Task Expansion_composes_with_filtering_sorting_and_field_selection()
    {
        // The four controls are one query string, and a caller who filters should not lose the embed.
        // Selecting fields deliberately drops customerId — the key is read from the STORED document, so
        // the expansion survives a projection that removed the field naming it.
        var (status, body) = await GetAsync("/orders?customerId=c1&_sort=-total&_fields=total&_expand=customer");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """[{"total":250,"_expand":{"customer":{"id":"c1","name":"Ada"}}},"""
            + """{"total":100,"_expand":{"customer":{"id":"c1","name":"Ada"}}}]""",
            body);
    }

    [Fact]
    public async Task An_unknown_relation_refuses_the_list_too()
    {
        var (status, _) = await GetAsync("/orders?_expand=custmoer");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    // ---- the admin read --------------------------------------------------------------------------

    [Fact]
    public async Task The_admin_read_expands_the_same_document_the_same_way()
    {
        var (status, body) = await GetAsync("/__admin/resources/orders/o1?_expand=customer");

        Assert.Equal(HttpStatusCode.OK, status);
        var document = JsonDocument.Parse(body).RootElement.GetProperty("body");
        Assert.Equal("Ada", document.GetProperty("_expand").GetProperty("customer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_admin_read_refuses_an_unknown_relation_by_name()
    {
        using var response = await _client!.GetAsync("/__admin/resources/orders/o1?_expand=custmoer");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Resource.UnknownRelation", error.GetProperty("error").GetString());
        // The message has to teach, or the caller has nothing to do with it but guess again.
        Assert.Contains("customer", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_typo_answers_the_same_way_whether_or_not_the_id_exists()
    {
        // Otherwise the refusal doubles as an id oracle: 400 for a real document, 404 for a made-up one
        // would tell an unauthenticated caller which ids are there.
        using var real = await _client!.GetAsync("/__admin/resources/orders/o1?_expand=custmoer");
        using var absent = await _client!.GetAsync("/__admin/resources/orders/nope?_expand=custmoer");

        Assert.Equal(HttpStatusCode.BadRequest, real.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, absent.StatusCode);
    }

    [Fact]
    public async Task A_relation_never_reaches_across_tenants()
    {
        // globex declares the same relation and holds an order naming c1 — which is acme's customer.
        using (var relation = new HttpRequestMessage(HttpMethod.Put, "/__admin/relations/orders")
        {
            Content = Json("""{"belongsTo":[{"collection":"customers","via":"customerId"}]}"""),
        })
        {
            relation.Headers.Add("X-Mockifyr-Tenant", "globex");
            using var response = await _client!.SendAsync(relation);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var seed = new HttpRequestMessage(HttpMethod.Put, "/__admin/resources/orders/o1")
        {
            Content = Json("""{"total":1,"customerId":"c1"}"""),
        })
        {
            seed.Headers.Add("X-Mockifyr-Tenant", "globex");
            using var response = await _client!.SendAsync(seed);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var read = new HttpRequestMessage(HttpMethod.Get, "/__admin/resources/orders/o1?_expand=customer");
        read.Headers.Add("X-Mockifyr-Tenant", "globex");
        using var result = await _client!.SendAsync(read);
        var body = JsonDocument.Parse(await result.Content.ReadAsStringAsync()).RootElement.GetProperty("body");

        Assert.Equal(JsonValueKind.Null, body.GetProperty("_expand").GetProperty("customer").ValueKind);
    }
}

/// <summary>
/// The partner-facing half of #378: <c>/__sandbox/resources/{collection}/{id}?_expand=</c> answers
/// exactly as the operator's read does, reached with the <c>mfk_</c> key the partner already holds
/// (#347) and with no tenant header anywhere.
/// </summary>
public sealed class SandboxResourceExpansionTests : IAsyncLifetime
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "mockifyr-378-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _host;
    private HttpClient _client = null!;
    private string _key = null!;

    private static readonly string AdminBasic =
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("op:secret"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_rootDir);
        _host = MockifyrHost.Build(
            ["--port", "0", "--sandbox-auth", "true", "--admin-user", "op", "--admin-pass", "secret", "--root-dir", _rootDir]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        await AdminAsync(HttpMethod.Put, "/__admin/relations/orders",
            """{"belongsTo":[{"collection":"customers","via":"customerId"}]}""");
        await AdminAsync(HttpMethod.Put, "/__admin/resources/customers/c1", """{"id":"c1","name":"Ada"}""");
        await AdminAsync(HttpMethod.Put, "/__admin/resources/orders/o1", """{"total":100,"customerId":"c1"}""");

        using var issued = await AdminAsync(HttpMethod.Post, "/__admin/apikeys", """{"name":"partner"}""");
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        _key = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString()!;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_host is not null) await _host.DisposeAsync();
        if (Directory.Exists(_rootDir)) Directory.Delete(_rootDir, recursive: true);
    }

    private async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", AdminBasic);
        request.Headers.Add("X-Mockifyr-Tenant", "acme");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} answered {(int)response.StatusCode}");
        return response;
    }

    private async Task<HttpResponseMessage> SandboxAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", _key);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task A_partner_expands_their_own_document_with_their_own_key()
    {
        using var response = await SandboxAsync("/__sandbox/resources/orders/o1?_expand=customer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // This surface reports `body` as the document's raw JSON text, where the admin one reports it
        // as an object — a difference that predates this change (#347) and is left alone by it.
        var raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("body").GetString()!;
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Ada", body.GetProperty("_expand").GetProperty("customer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_partner_surface_refuses_an_unknown_relation_the_same_way()
    {
        using var response = await SandboxAsync("/__sandbox/resources/orders/o1?_expand=custmoer");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
