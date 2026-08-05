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
/// Wire validation of the consumer side of conformance (#287): drive real traffic through a real host,
/// then ask whether the client stayed inside the contract. Self-tested; needs no Docker.
/// </summary>
public sealed class TrafficVerifyTests
{
    private const string Spec =
        """
        {
          "openapi": "3.0.0",
          "info": { "title": "orders", "version": "1.0" },
          "paths": {
            "/orders": {
              "get": {
                "parameters": [{ "name": "page", "in": "query", "required": true, "schema": { "type": "string" } }],
                "responses": { "200": { "description": "ok" } }
              },
              "post": {
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object", "required": ["customerId"],
                  "properties": { "customerId": { "type": "string" } } } } } },
                "responses": { "201": { "description": "created" } }
              }
            }
          }
        }
        """;

    private const string CatchAll =
        """{"request":{"method":"ANY","urlPattern":".*"},"response":{"status":200,"body":"ok"}}""";

    [Fact]
    public async Task Traffic_that_stays_inside_the_contract_conforms()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, CatchAll);

        using (await client.GetAsync("/orders?page=1")) { }
        using (await client.PostAsync("/orders", Json("""{"customerId":"c-1"}"""))) { }

        var report = await VerifyAsync(client);

        Assert.True(report.RootElement.GetProperty("conforms").GetBoolean(),
            report.RootElement.GetProperty("findings").ToString());
        Assert.Equal(2, report.RootElement.GetProperty("requestsExamined").GetInt32());
    }

    [Fact]
    public async Task A_client_calling_an_undeclared_endpoint_is_caught()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, CatchAll);

        // The mock answers happily — that is exactly the problem. A permissive mock hides the call that
        // production will refuse.
        using var served = await client.GetAsync("/orders/7/history");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        var report = await VerifyAsync(client);
        var finding = report.RootElement.GetProperty("findings").EnumerateArray().Single();

        Assert.Equal("undeclaredOperation", finding.GetProperty("kind").GetString());
        Assert.Equal("/orders/7/history", finding.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_missing_required_parameter_is_caught()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, CatchAll);

        using (await client.GetAsync("/orders")) { }

        var finding = (await VerifyAsync(client)).RootElement.GetProperty("findings").EnumerateArray().Single();

        Assert.Equal("missingParameter", finding.GetProperty("kind").GetString());
        Assert.Contains("page", finding.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_request_body_the_contract_forbids_is_caught()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, CatchAll);

        using (await client.PostAsync("/orders", Json("""{"wrongField":"x"}"""))) { }

        var finding = (await VerifyAsync(client)).RootElement.GetProperty("findings").EnumerateArray().Single();

        Assert.Equal("requestSchemaViolation", finding.GetProperty("kind").GetString());
        Assert.Contains("customerId", finding.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Verifying_does_not_add_to_the_journal_it_reads()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client, CatchAll);
        using (await client.GetAsync("/orders?page=1")) { }

        var first = await VerifyAsync(client);
        var second = await VerifyAsync(client);

        // A check that journaled itself would grow its own input, and the second run would answer
        // differently from the first for no reason the reader could see.
        Assert.Equal(1, first.RootElement.GetProperty("requestsExamined").GetInt32());
        Assert.Equal(1, second.RootElement.GetProperty("requestsExamined").GetInt32());
    }

    [Fact]
    public async Task One_tenants_traffic_is_never_judged_against_anothers_contract()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateForAsync(client, "acme", CatchAll);

        using var offending = new HttpRequestMessage(HttpMethod.Get, "/orders/7/history");
        offending.Headers.Add("X-Mockifyr-Tenant", "acme");
        using (await client.SendAsync(offending)) { }

        using var acme = await VerifyForAsync(client, "acme");
        using var globex = await VerifyForAsync(client, "globex");

        Assert.NotEmpty(acme.RootElement.GetProperty("findings").EnumerateArray());
        Assert.Equal(0, globex.RootElement.GetProperty("requestsExamined").GetInt32());
    }

    [Fact]
    public async Task Both_sides_of_conformance_answer_about_the_same_document()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        // Import the spec, drive conforming traffic, and both checks should agree that nothing is wrong
        // — the stub side about the mock, the traffic side about the client.
        using var imported = await client.PostAsync("/__admin/openapi/import", Json(Spec));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        using (await client.GetAsync("/orders?page=1")) { }
        using (await client.PostAsync("/orders", Json("""{"customerId":"c-1"}"""))) { }

        using var stubs = await client.PostAsync("/__admin/openapi/verify", Json(Spec));
        using var stubReport = JsonDocument.Parse(await stubs.Content.ReadAsStringAsync());
        var trafficReport = await VerifyAsync(client);

        Assert.True(stubReport.RootElement.GetProperty("conforms").GetBoolean(),
            stubReport.RootElement.GetProperty("findings").ToString());
        Assert.True(trafficReport.RootElement.GetProperty("conforms").GetBoolean(),
            trafficReport.RootElement.GetProperty("findings").ToString());
    }

    [Fact]
    public async Task A_spec_the_import_would_refuse_is_refused_here_too()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.PostAsync("/__admin/requests/verify", Json("{ not a spec"));

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
    }

    private static async Task<JsonDocument> VerifyAsync(HttpClient client)
    {
        using var response = await client.PostAsync("/__admin/requests/verify", Json(Spec));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> VerifyForAsync(HttpClient client, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/requests/verify") { Content = Json(Spec) };
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await client.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
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
