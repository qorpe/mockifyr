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
/// Wire validation of contract conformance (#287) against a real host: import a specification, then
/// ask whether the stub set still tells the truth about it.
/// </summary>
/// <remarks>
/// No oracle — the reference engine has no conformance surface — so this is a self-test suite. What it
/// proves that the unit tests cannot is the round trip: stubs the importer itself generated must verify
/// clean, or the two halves of the feature disagree about the same document.
/// </remarks>
public sealed class ContractVerifyTests
{
    private const string Spec =
        """
        {
          "openapi": "3.0.0",
          "info": { "title": "orders", "version": "1.0" },
          "paths": {
            "/orders/{id}": {
              "get": {
                "responses": {
                  "200": { "description": "ok", "content": { "application/json": { "schema": {
                    "type": "object", "required": ["id", "total"],
                    "properties": { "id": { "type": "string" }, "total": { "type": "number" } } } } } }
                }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task What_the_importer_generated_verifies_clean()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var imported = await client.PostAsync("/__admin/openapi/import", Json(Spec));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        var report = await VerifyAsync(client, Spec);

        // The two halves of the feature must agree about the same document. If import produced stubs
        // that verify dirty, one of them is wrong and a user has no way to tell which.
        Assert.True(report.RootElement.GetProperty("conforms").GetBoolean(),
            report.RootElement.GetProperty("findings").ToString());
        Assert.Equal(1, report.RootElement.GetProperty("operationsInSpec").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("operationsCovered").GetInt32());
    }

    [Fact]
    public async Task A_stub_that_drifted_from_the_spec_is_reported()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        // The classic drift: the upstream added a required field, the stub never followed.
        await CreateAsync(client,
            """{"request":{"method":"GET","urlPath":"/orders/42"},"response":{"status":200,"body":"{\"id\":\"a\"}"}}""");

        var report = await VerifyAsync(client, Spec);
        var findings = report.RootElement.GetProperty("findings").EnumerateArray().ToList();

        Assert.False(report.RootElement.GetProperty("conforms").GetBoolean());
        Assert.Contains(findings, f => f.GetProperty("kind").GetString() == "schemaViolation");
        Assert.Contains(findings, f => f.GetProperty("detail").GetString()!.Contains("total", StringComparison.OrdinalIgnoreCase));
        Assert.All(findings, f => Assert.NotEqual(Guid.Empty, f.GetProperty("stubId").GetGuid()));
    }

    [Fact]
    public async Task An_endpoint_the_spec_dropped_is_reported_against_the_stub()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client,
            """{"request":{"method":"GET","urlPath":"/legacy"},"response":{"status":200}}""");

        var report = await VerifyAsync(client, Spec);
        var kinds = report.RootElement.GetProperty("findings").EnumerateArray()
            .Select(f => f.GetProperty("kind").GetString()).ToList();

        Assert.Contains("undeclaredOperation", kinds);
        Assert.Contains("uncoveredOperation", kinds);
    }

    [Fact]
    public async Task Verification_never_changes_the_stubs()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateAsync(client,
            """{"request":{"method":"GET","urlPath":"/legacy"},"response":{"status":200}}""");

        var before = await client.GetStringAsync("/__admin/mappings");
        await VerifyAsync(client, Spec);
        var after = await client.GetStringAsync("/__admin/mappings");

        // A report, never a mutation: which side is wrong is a judgement about the caller's system, and
        // a tool that "fixed" the drift itself would be deciding that for them.
        Assert.Equal(before, after);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/legacy")).StatusCode);
    }

    [Fact]
    public async Task One_tenants_stubs_are_never_verified_against_anothers_spec()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await CreateForAsync(client, "acme",
            """{"request":{"method":"GET","urlPath":"/orders/42"},"response":{"status":200,"body":"{\"id\":\"a\",\"total\":1}"}}""");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/openapi/verify") { Content = Json(Spec) };
        request.Headers.Add("X-Mockifyr-Tenant", "globex");
        using var response = await request.SendWith(client);
        using var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // globex has no stubs, so the operation is uncovered — and acme's stub, which would have covered
        // it, must not be visible here at all.
        var findings = report.RootElement.GetProperty("findings").EnumerateArray().ToList();
        Assert.Single(findings);
        Assert.Equal("uncoveredOperation", findings[0].GetProperty("kind").GetString());
        Assert.Equal(0, report.RootElement.GetProperty("operationsCovered").GetInt32());
    }

    [Fact]
    public async Task A_spec_with_an_external_ref_is_refused()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.PostAsync("/__admin/openapi/verify", Json(
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{"/thing":{"get":{"responses":{"200":{"description":"ok",
               "content":{"application/json":{"schema":{"$ref":"https://evil.example.com/s.json"}}}}}}}}}
            """));

        // Verification takes a document from the same untrusted place an import does, so it applies the
        // same guard — nothing is ever fetched.
        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("OpenApi.ExternalRef", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unparseable_spec_is_refused()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.PostAsync("/__admin/openapi/verify", Json("{ not a spec"));

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
    }

    [Fact]
    public async Task Coverage_is_reported_even_when_everything_conforms()
    {
        await using var app = await StartAsync();
        using var client = Client(app);

        var report = await VerifyAsync(client, Spec);

        // "Conforms" on an empty stub set would be true and useless; the counts are what tell an
        // operator they have modelled one operation out of forty.
        Assert.False(report.RootElement.GetProperty("conforms").GetBoolean());
        Assert.Equal(0, report.RootElement.GetProperty("operationsCovered").GetInt32());
        Assert.Equal(1, report.RootElement.GetProperty("operationsInSpec").GetInt32());
    }

    private static async Task<JsonDocument> VerifyAsync(HttpClient client, string spec)
    {
        using var response = await client.PostAsync("/__admin/openapi/verify", Json(spec));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

internal static class RequestExtensions
{
    /// <summary>Sends a prepared request, keeping the tenant header alongside the body at the call site.</summary>
    public static Task<HttpResponseMessage> SendWith(this HttpRequestMessage request, HttpClient client) =>
        client.SendAsync(request);
}
