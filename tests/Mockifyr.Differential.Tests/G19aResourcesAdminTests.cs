using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Core;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire-level self-tests for <c>/__admin/resources</c> (G19a, ADR 0011) — no oracle exists
/// (WireMock has no resource concept). Driven over a real Kestrel host: CRUD round-trips with
/// verbatim unicode bodies, pagination, seed import (explicit and generated ids), tenant scoping
/// via the tenant header, the honest 404/413/422 error surface, and reset scopes.
/// </summary>
public sealed class G19aResourcesAdminTests : IAsyncDisposable
{
    private readonly MockifyrKestrelHost _host = new(services =>
        services.AddSingleton(new ResourceOptions(MaxBodyBytes: 4096)));

    private readonly HttpClient _client;

    public G19aResourcesAdminTests()
    {
        _client = new HttpClient { BaseAddress = new Uri(_host.BaseAddress) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null, string tenant = "acme")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _client.SendAsync(request);
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    [Fact]
    public async Task Put_get_list_delete_round_trip_with_verbatim_unicode_body()
    {
        var body = """{"name":"Ötö 🙂","note":"line1\nline2"}""";

        using var put = await SendAsync(HttpMethod.Put, "/__admin/resources/orders/ord-1", body);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var created = await ReadJsonAsync(put))
        {
            Assert.Equal("ord-1", created.RootElement.GetProperty("id").GetString());
            Assert.Equal(1, created.RootElement.GetProperty("version").GetInt64());
            Assert.Equal("Ötö 🙂", created.RootElement.GetProperty("body").GetProperty("name").GetString());
        }

        using var get = await SendAsync(HttpMethod.Get, "/__admin/resources/orders/ord-1");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        using var list = await SendAsync(HttpMethod.Get, "/__admin/resources/orders");
        using (var page = await ReadJsonAsync(list))
        {
            Assert.Equal(1, page.RootElement.GetProperty("total").GetInt32());
            Assert.Single(page.RootElement.GetProperty("documents").EnumerateArray());
        }

        using var collections = await SendAsync(HttpMethod.Get, "/__admin/resources");
        using (var doc = await ReadJsonAsync(collections))
        {
            var entry = Assert.Single(doc.RootElement.GetProperty("collections").EnumerateArray());
            Assert.Equal(("orders", 1), (entry.GetProperty("name").GetString(), entry.GetProperty("count").GetInt32()));
        }

        using var delete = await SendAsync(HttpMethod.Delete, "/__admin/resources/orders/ord-1");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using var gone = await SendAsync(HttpMethod.Get, "/__admin/resources/orders/ord-1");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var deleteAgain = await SendAsync(HttpMethod.Delete, "/__admin/resources/orders/ord-1");
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
    }

    [Fact]
    public async Task Replacing_a_document_advances_its_version()
    {
        (await SendAsync(HttpMethod.Put, "/__admin/resources/orders/ord-1", """{"v":1}""")).Dispose();
        using var replaced = await SendAsync(HttpMethod.Put, "/__admin/resources/orders/ord-1", """{"v":2}""");

        using var doc = await ReadJsonAsync(replaced);
        Assert.Equal(2, doc.RootElement.GetProperty("version").GetInt64());
        Assert.Equal(2, doc.RootElement.GetProperty("body").GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task Listing_is_paginated_and_an_unknown_collection_is_an_honest_empty_page()
    {
        for (var i = 1; i <= 5; i++)
        {
            (await SendAsync(HttpMethod.Put, $"/__admin/resources/orders/ord-{i}", $$"""{"n":{{i}}}""")).Dispose();
        }

        using var page = await SendAsync(HttpMethod.Get, "/__admin/resources/orders?limit=2&offset=2");
        using (var doc = await ReadJsonAsync(page))
        {
            Assert.Equal(5, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(["ord-3", "ord-4"],
                doc.RootElement.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("id").GetString()));
        }

        using var unknown = await SendAsync(HttpMethod.Get, "/__admin/resources/nothing");
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        using (var doc = await ReadJsonAsync(unknown))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
        }
    }

    [Fact]
    public async Task Seeding_accepts_explicit_and_generated_ids_and_is_transactional()
    {
        using var seed = await SendAsync(HttpMethod.Post, "/__admin/resources/customers/seed",
            """[{"id":"cus-1","name":"Ada"},{"name":"Grace"}]""");
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        using (var doc = await ReadJsonAsync(seed))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("seeded").GetInt32());
        }

        using var explicitId = await SendAsync(HttpMethod.Get, "/__admin/resources/customers/cus-1");
        Assert.Equal(HttpStatusCode.OK, explicitId.StatusCode);

        // One bad element (an id over the cap) means NOTHING lands — the collection count is unchanged.
        var hugeId = new string('x', 300);
        using var invalid = await SendAsync(HttpMethod.Post, "/__admin/resources/customers/seed",
            $$"""[{"id":"cus-3","ok":true},{"id":"{{hugeId}}","bad":true}]""");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        using var list = await SendAsync(HttpMethod.Get, "/__admin/resources/customers");
        using (var doc = await ReadJsonAsync(list))
        {
            Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        }
    }

    [Fact]
    public async Task Tenants_only_see_their_own_documents()
    {
        (await SendAsync(HttpMethod.Put, "/__admin/resources/orders/ord-1", """{"owner":"acme"}""", tenant: "acme")).Dispose();

        using var otherGet = await SendAsync(HttpMethod.Get, "/__admin/resources/orders/ord-1", tenant: "globex");
        Assert.Equal(HttpStatusCode.NotFound, otherGet.StatusCode);

        using var otherCollections = await SendAsync(HttpMethod.Get, "/__admin/resources", tenant: "globex");
        using (var doc = await ReadJsonAsync(otherCollections))
        {
            Assert.Empty(doc.RootElement.GetProperty("collections").EnumerateArray());
        }
    }

    [Fact]
    public async Task The_error_surface_is_honest_413_and_422()
    {
        using var tooLarge = await SendAsync(HttpMethod.Put, "/__admin/resources/orders/big",
            $$"""{"pad":"{{new string('x', 5000)}}"}""");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);

        using var notJson = await SendAsync(HttpMethod.Put, "/__admin/resources/orders/bad", "{not json");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notJson.StatusCode);

        using var badCollection = await SendAsync(HttpMethod.Put, "/__admin/resources/9bad!/x", "{}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badCollection.StatusCode);

        using var badSeed = await SendAsync(HttpMethod.Post, "/__admin/resources/orders/seed", """{"not":"array"}""");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badSeed.StatusCode);
    }

    [Fact]
    public async Task Reset_scopes_one_collection_or_the_whole_tenant()
    {
        (await SendAsync(HttpMethod.Put, "/__admin/resources/orders/a", "{}")).Dispose();
        (await SendAsync(HttpMethod.Put, "/__admin/resources/customers/b", "{}")).Dispose();

        (await SendAsync(HttpMethod.Post, "/__admin/resources/orders/reset")).Dispose();
        using var afterOne = await SendAsync(HttpMethod.Get, "/__admin/resources");
        using (var doc = await ReadJsonAsync(afterOne))
        {
            var entry = Assert.Single(doc.RootElement.GetProperty("collections").EnumerateArray());
            Assert.Equal("customers", entry.GetProperty("name").GetString());
        }

        (await SendAsync(HttpMethod.Post, "/__admin/resources/reset")).Dispose();
        using var afterAll = await SendAsync(HttpMethod.Get, "/__admin/resources");
        using (var doc = await ReadJsonAsync(afterAll))
        {
            Assert.Empty(doc.RootElement.GetProperty("collections").EnumerateArray());
        }
    }
}
