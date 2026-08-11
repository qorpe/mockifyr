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
/// Resource querying at the wire (#353): filter, sort and field selection, on <b>both</b> the admin
/// listing and the served <c>list</c>. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class ResourceQueryWireTests : IAsyncLifetime
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

        // A stub that lists the collection, so the SAME data can be asked for two ways.
        //
        // urlPath, not url: in the mapping dialect `url` matches the path AND the query string, so
        // "/orders" would stop matching the moment a caller filters — the request that most wants this
        // feature is the one that would 404. Anyone wiring serve-time filtering has to know this, so it
        // is stated here and in the docs rather than left to be discovered.
        using var stub = await _client.PostAsync("/__admin/mappings", Json("""
        {"request":{"method":"GET","urlPath":"/orders"},
         "response":{"status":200,"headers":{"Content-Type":"application/json"},
                     "body":"{{state.list}}","state":{"operation":"list","collection":"orders"}}}
        """));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        foreach (var (id, body) in ((string, string)[])[
            ("o1", """{"status":"settled","total":100,"note":"first"}"""),
            ("o2", """{"status":"pending","total":9}"""),
            ("o3", """{"status":"settled","total":250,"note":"rush"}"""),
        ])
        {
            using var put = await _client.PutAsync($"/__admin/resources/orders/{id}", Json(body));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>The ids the admin listing returns for a query, plus the total it reports.</summary>
    private async Task<(string[] Ids, int Total)> Admin(string query)
    {
        using var response = await _client!.GetAsync($"/__admin/resources/orders{query}");
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return ([.. root.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("id").GetString()!)],
                root.GetProperty("total").GetInt32());
    }

    /// <summary>What the served list answers for the same query.</summary>
    private async Task<string> Served(string query)
    {
        using var response = await _client!.GetAsync($"/orders{query}");
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_filter_narrows_the_listing_and_the_total_counts_matches()
    {
        // total must mean "matching", not "in the collection", or the paging control lies about how
        // many pages there are.
        var (ids, total) = await Admin("?status=settled");

        Assert.Equal(["o1", "o3"], ids);
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task Both_surfaces_answer_the_same_question_the_same_way()
    {
        // The acceptance criterion that matters most: a sandbox and the screen watching it must not
        // disagree about what a collection contains.
        var (ids, _) = await Admin("?status=settled&_sort=-total");
        var served = await Served("?status=settled&_sort=-total");

        Assert.Equal(["o3", "o1"], ids);
        // Same order, same membership — read off the served array rather than trusting it by shape.
        var totals = JsonDocument.Parse(served).RootElement.EnumerateArray()
            .Select(d => d.GetProperty("total").GetInt32()).ToArray();
        Assert.Equal([250, 100], totals);
    }

    [Fact]
    public async Task Sorting_a_number_is_numeric_at_the_wire_too()
    {
        // As text "9" sorts after "250"; this is the assertion that the comparer reached the edge.
        var (ids, _) = await Admin("?_sort=total");

        Assert.Equal(["o2", "o1", "o3"], ids);
    }

    [Fact]
    public async Task Field_selection_returns_the_summary_shape()
    {
        var served = await Served("?_fields=status");

        Assert.Equal("""[{"status":"settled"},{"status":"pending"},{"status":"settled"}]""", served);
    }

    [Fact]
    public async Task Contains_and_absent_reach_the_wire_with_the_dialect_s_own_words()
    {
        Assert.Equal((string[])["o3"], (await Admin("?note:contains=rus")).Ids);
        Assert.Equal((string[])["o2"], (await Admin("?note:absent=true")).Ids);
    }

    [Fact]
    public async Task Paging_still_works_and_composes_with_a_filter()
    {
        var (ids, total) = await Admin("?status=settled&limit=1&offset=1");

        Assert.Equal(["o3"], ids);
        // The page is one document; the total is still the number that matched.
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task A_caller_that_sends_no_query_gets_exactly_what_they_got_before()
    {
        // The compatibility promise, at both surfaces.
        var (ids, total) = await Admin(string.Empty);
        Assert.Equal(["o1", "o2", "o3"], ids);
        Assert.Equal(3, total);

        var served = await Served(string.Empty);
        Assert.Equal(
            """[{"status":"settled","total":100,"note":"first"},{"status":"pending","total":9},{"status":"settled","total":250,"note":"rush"}]""",
            served);
    }
}
