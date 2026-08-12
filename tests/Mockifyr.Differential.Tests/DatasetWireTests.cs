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
/// Named datasets at the wire (#351): declare a scenario across collections, load it in one call, and
/// take exactly it back out. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class DatasetWireTests : IAsyncLifetime
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

        // orders belong to customers, so the dataset below is only loadable in dependency order.
        using var relations = await _client.PutAsync("/__admin/relations/orders", Json(
            """{"belongsTo":[{"collection":"customers","via":"customerId"}]}"""));
        Assert.Equal(HttpStatusCode.OK, relations.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>Deliberately written child-first, so the loader's ordering is what makes it work.</summary>
    private const string Delinquent = """
    {"seed":42,"items":[
      {"collection":"orders","count":3,"document":{"customerId":"customer-0","total":100},"id":"order-{{index}}"},
      {"collection":"customers","count":1,"document":{"name":"{{random 'Name.fullName'}}"},"id":"customer-{{index}}"}
    ]}
    """;

    private async Task<int> Count(string collection)
    {
        using var response = await _client!.GetAsync($"/__admin/resources/{collection}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("total").GetInt32();
    }

    private async Task Declare(string name = "delinquent", string body = Delinquent)
    {
        using var response = await _client!.PutAsync($"/__admin/datasets/{name}", Json(body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_scenario_across_collections_loads_in_one_call()
    {
        await Declare();

        using var response = await _client!.PostAsync("/__admin/datasets/delinquent/load", null);
        var loaded = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("loaded").GetInt32();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, loaded);
        Assert.Equal(1, await Count("customers"));
        Assert.Equal(3, await Count("orders"));
    }

    [Fact]
    public async Task Loading_twice_leaves_one_copy_rather_than_two()
    {
        // The gesture people actually repeat between test runs. Two copies would make the second run
        // fail for reasons that have nothing to do with the code under test.
        await Declare("repeatable");

        await _client!.PostAsync("/__admin/datasets/repeatable/load", null);
        await _client.PostAsync("/__admin/datasets/repeatable/load", null);

        Assert.Equal(1, await Count("customers"));
        Assert.Equal(3, await Count("orders"));
    }

    [Fact]
    public async Task Unloading_takes_back_exactly_what_was_loaded()
    {
        await Declare("removable");
        await _client!.PostAsync("/__admin/datasets/removable/load", null);

        using var response = await _client.PostAsync("/__admin/datasets/removable/unload", null);
        var removed = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("removed").GetInt32();

        Assert.Equal(4, removed);
        Assert.Equal(0, await Count("customers"));
        Assert.Equal(0, await Count("orders"));
    }

    [Fact]
    public async Task Unloading_something_that_is_not_loaded_is_not_an_error()
    {
        // It is the state the caller asked for. A 404 here would make "reset before each run" a script
        // with an error branch in it.
        await Declare("never-loaded");

        using var response = await _client!.PostAsync("/__admin/datasets/never-loaded/unload", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_same_seed_loads_the_same_data()
    {
        await Declare("seeded");
        await _client!.PostAsync("/__admin/datasets/seeded/load", null);
        var first = await Bodies("customers");

        await _client.PostAsync("/__admin/datasets/seeded/load", null);

        Assert.Equal(first, await Bodies("customers"));
    }

    [Fact]
    public async Task A_dataset_that_cannot_load_leaves_nothing_behind()
    {
        // The order references a customer this dataset never creates, so integrity refuses it — after
        // the customers item has already written a document.
        await Declare("broken", """
        {"items":[
          {"collection":"customers","count":2,"document":{"name":"Ada"}},
          {"collection":"orders","count":1,"document":{"customerId":"nobody"}}
        ]}
        """);

        using var response = await _client!.PostAsync("/__admin/datasets/broken/load", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, await Count("customers"));
        Assert.Equal(0, await Count("orders"));
    }

    [Fact]
    public async Task A_declared_dataset_is_listed_so_an_operator_can_see_what_a_sandbox_offers()
    {
        await Declare("listed");

        using var response = await _client!.GetAsync("/__admin/datasets");
        var names = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("datasets").EnumerateArray().Select(d => d.GetProperty("name").GetString()).ToArray();

        Assert.Contains("listed", names);
    }

    [Fact]
    public async Task Loading_a_dataset_nobody_declared_is_a_404()
    {
        using var response = await _client!.PostAsync("/__admin/datasets/imaginary/load", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_bookkeeping_collections_never_show_up_beside_a_tenants_data()
    {
        await Declare("hidden");
        await _client!.PostAsync("/__admin/datasets/hidden/load", null);

        using var response = await _client.GetAsync("/__admin/resources");
        var names = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("collections").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();

        // The tenant's own collections are there; the reserved ones the datasets and relations live in
        // are not — internal bookkeeping does not belong in front of an operator.
        Assert.Contains("customers", names);
        Assert.DoesNotContain(names, n => n.StartsWith('!'));
    }

    private async Task<string[]> Bodies(string collection)
    {
        using var response = await _client!.GetAsync($"/__admin/resources/{collection}");
        return [.. JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("body").ToString())];
    }
}
