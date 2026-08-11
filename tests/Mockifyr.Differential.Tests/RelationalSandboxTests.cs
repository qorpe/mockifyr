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
/// The defect ADR 0015 exists for, proven end to end: a specification with
/// <c>/customers/{customerId}/orders</c> is imported and then <em>served</em>, and each modelled
/// customer sees only their own orders. Import claims are proven by serving, not by inspection —
/// the G19c rule. Mockifyr-specific (no oracle has a sandbox resource model), so a self-test, and
/// no Docker is required.
/// </summary>
public sealed class RelationalSandboxTests : IAsyncLifetime
{
    private const string NestedSpec = """
    {
      "openapi": "3.0.0",
      "info": { "title": "orders", "version": "1.0.0" },
      "paths": {
        "/customers": {
          "post": { "responses": { "201": { "description": "created" } } },
          "get":  { "responses": { "200": { "description": "listed" } } }
        },
        "/customers/{customerId}": {
          "get":    { "responses": { "200": { "description": "read" } } },
          "put":    { "responses": { "200": { "description": "updated" } } },
          "delete": { "responses": { "204": { "description": "deleted" } } }
        },
        "/customers/{customerId}/orders": {
          "post": { "responses": { "201": { "description": "created" } } },
          "get":  { "responses": { "200": { "description": "listed" } } }
        },
        "/customers/{customerId}/orders/{orderId}": {
          "get":    { "responses": { "200": { "description": "read" } } },
          "put":    { "responses": { "200": { "description": "updated" } } },
          "delete": { "responses": { "204": { "description": "deleted" } } }
        }
      }
    }
    """;

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

        using var import = await _client.PostAsync(
            "/__admin/openapi/import?stateful=true",
            new StringContent(NestedSpec, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, string Body, string? Location)> Send(
        HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _client!.SendAsync(request);
        return (
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Headers.TryGetValues("Location", out var location) ? location.First() : null);
    }

    /// <summary>
    /// The id the sandbox generated, taken from Location. Not from the response body: the body is the
    /// document exactly as it was sent, and the id the store assigned is not in it.
    /// </summary>
    private static string IdOf(string? location) =>
        location!.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1];

    private async Task<string> CreateCustomer(string name)
    {
        var (status, _, location) = await Send(HttpMethod.Post, "/customers", $$"""{"name":"{{name}}"}""");
        Assert.Equal(HttpStatusCode.Created, status);
        // A Location a client cannot follow is not a Location: it must carry no template text.
        Assert.DoesNotContain("{", location!, StringComparison.Ordinal);
        return IdOf(location);
    }

    [Fact]
    public async Task One_customers_orders_are_not_another_customers_orders()
    {
        var first = await CreateCustomer("c1");
        var second = await CreateCustomer("c2");

        var (created, _, orderLocation) = await Send(HttpMethod.Post, $"/customers/{first}/orders", """{"total":100}""");
        Assert.Equal(HttpStatusCode.Created, created);
        await Send(HttpMethod.Post, $"/customers/{second}/orders", """{"total":250}""");

        var (_, mine, _) = await Send(HttpMethod.Get, $"/customers/{first}/orders");
        var (_, theirs, _) = await Send(HttpMethod.Get, $"/customers/{second}/orders");

        // Before ADR 0015 both of these listed both orders, which is the whole reason the ADR exists.
        Assert.Contains("100", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("250", mine, StringComparison.Ordinal);
        Assert.Contains("250", theirs, StringComparison.Ordinal);
        Assert.DoesNotContain("100", theirs, StringComparison.Ordinal);

        // And an order's id does not travel: guessing it under the wrong customer is a miss, or scoping
        // the list would have been decoration over a still-reachable document.
        var orderId = IdOf(orderLocation);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/customers/{second}/orders/{orderId}")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/customers/{first}/orders/{orderId}")).Status);
    }

    [Fact]
    public async Task An_order_cannot_be_created_under_a_customer_that_does_not_exist()
    {
        var (status, _, _) = await Send(HttpMethod.Post, "/customers/99/orders", """{"total":100}""");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Deleting_a_customer_that_still_has_orders_is_refused_rather_than_cascading()
    {
        // The default is `restrict` because deleting a Stripe customer does not delete their charges;
        // an imported spec must not acquire destructive behaviour the API it models does not have.
        var customer = await CreateCustomer("c3");
        await Send(HttpMethod.Post, $"/customers/{customer}/orders", """{"total":100}""");

        var (refused, _, _) = await Send(HttpMethod.Delete, $"/customers/{customer}");
        Assert.Equal(HttpStatusCode.Conflict, refused);

        // Still there — a refusal that had already deleted something would be worse than no refusal.
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/customers/{customer}")).Status);
        Assert.Contains("100", (await Send(HttpMethod.Get, $"/customers/{customer}/orders")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_created_document_is_byte_identical_to_what_was_sent()
    {
        // The contract declares no customerId on an order, so the relation is held in metadata and the
        // body is untouched. Adding a field here is what would make POST /__admin/openapi/verify report
        // our own sandbox as drifted from the document we generated it from.
        var customer = await CreateCustomer("c4");

        var (_, _, location) = await Send(HttpMethod.Post, $"/customers/{customer}/orders", """{"total":100}""");
        var (_, read, _) = await Send(HttpMethod.Get, $"/customers/{customer}/orders/{IdOf(location)}");

        Assert.Equal("""{"total":100}""", read);
    }

    [Fact]
    public async Task The_import_declared_the_relation_the_path_shape_implied()
    {
        var (status, body, _) = await Send(HttpMethod.Get, "/__admin/relations");

        Assert.Equal(HttpStatusCode.OK, status);
        var declared = JsonDocument.Parse(body).RootElement.GetProperty("relations").EnumerateArray().Single();
        Assert.Equal("orders", declared.GetProperty("collection").GetString());
        var belongsTo = declared.GetProperty("belongsTo").EnumerateArray().Single();
        Assert.Equal("customers", belongsTo.GetProperty("collection").GetString());
        Assert.Equal("customerId", belongsTo.GetProperty("via").GetString());
        Assert.Equal("restrict", belongsTo.GetProperty("onDelete").GetString());
    }

    [Fact]
    public async Task Declaring_cascade_changes_what_a_delete_does()
    {
        // The declaration has to reach behaviour, not just storage: reading it back proves the write
        // landed and nothing else.
        var customer = await CreateCustomer("c7");
        await Send(HttpMethod.Post, $"/customers/{customer}/orders", """{"total":100}""");

        var (declared, _, _) = await Send(HttpMethod.Put, "/__admin/relations/orders",
            """{"belongsTo":[{"collection":"customers","via":"customerId","onDelete":"cascade"}]}""");
        Assert.Equal(HttpStatusCode.OK, declared);

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/customers/{customer}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/customers/{customer}")).Status);

        // Put the imported declaration back: these tests share one host, and leaving `cascade` behind
        // would make another test's outcome depend on the order they ran in.
        await Send(HttpMethod.Put, "/__admin/relations/orders",
            """{"belongsTo":[{"collection":"customers","via":"customerId"}]}""");
    }

    [Fact]
    public async Task A_misspelled_delete_rule_is_refused_rather_than_defaulted()
    {
        // Reading "casade" as `restrict` would hand the operator the opposite of what they asked for,
        // in the one declaration that decides whether data is destroyed.
        var (status, body, _) = await Send(HttpMethod.Put, "/__admin/relations/orders",
            """{"belongsTo":[{"collection":"customers","via":"customerId","onDelete":"casade"}]}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("onDelete", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_relations_a_collection_never_declared_is_a_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, "/__admin/relations/nothing-here")).Status);
    }

    [Fact]
    public async Task A_flat_collection_is_untouched_by_any_of_this()
    {
        // The 1.x compatibility promise at the wire: /customers declares no owner, so it scopes nothing
        // and lists everything, exactly as it did before relations existed.
        await CreateCustomer("c5");
        await CreateCustomer("c6");

        var (status, body, _) = await Send(HttpMethod.Get, "/customers");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("c5", body, StringComparison.Ordinal);
        Assert.Contains("c6", body, StringComparison.Ordinal);
    }
}
