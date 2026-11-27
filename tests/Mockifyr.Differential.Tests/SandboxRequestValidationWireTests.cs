using System.Globalization;
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
/// Serve-time contract validation (#448, ADR 0018), on a real host: an import with
/// <c>?validate=true</c> answers a request that breaks the operation's contract with a 400 problem
/// document naming each violation, instead of the happy answer. No oracle exists for this (the reference
/// engine has no OpenAPI import), so it is self-tested against the contract's own words. Needs no Docker.
/// </summary>
public sealed class SandboxRequestValidationWireTests
{
    private const string Spec30 =
        """
        {
          "openapi": "3.0.3",
          "info": { "title": "payments", "version": "1.0" },
          "components": { "schemas": {
            "Money": { "type": "object", "required": ["amount", "currency"], "additionalProperties": false,
              "properties": {
                "amount": { "type": "number", "minimum": 0, "maximum": 1000, "exclusiveMinimum": true },
                "currency": { "type": "string", "enum": ["EUR", "TRY", "USD"] } } } } },
          "paths": {
            "/payments": {
              "get": {
                "parameters": [
                  { "name": "limit", "in": "query", "schema": { "type": "integer", "minimum": 1, "maximum": 100 } },
                  { "name": "X-Request-Id", "in": "header", "required": true, "schema": { "type": "string" } } ],
                "responses": { "200": { "description": "ok", "content": { "application/json": { "example": { "items": [] } } } } }
              },
              "post": {
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object", "required": ["payer", "money"], "additionalProperties": false,
                  "properties": {
                    "payer": { "type": "string", "format": "email" },
                    "money": { "$ref": "#/components/schemas/Money" },
                    "note": { "type": "string", "nullable": true },
                    "due": { "type": "string", "format": "date" } } } } } },
                "responses": { "201": { "description": "created", "content": { "application/json": { "example": { "id": "p-1" } } } } }
              }
            },
            "/payments/{paymentId}": {
              "parameters": [ { "name": "paymentId", "in": "path", "required": true, "schema": { "type": "integer" } } ],
              "get": { "responses": { "200": { "description": "ok", "content": { "application/json": { "example": { "id": 7 } } } } } }
            }
          }
        }
        """;

    private const string Valid = """{"payer":"a@example.com","money":{"amount":12.5,"currency":"EUR"}}""";

    [Fact]
    public async Task Without_the_flag_an_import_serves_a_violating_request_as_it_always_did()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Spec30, validate: false);

        using var response = await client.PostAsync("/payments", Json("""{"money":{"amount":1,"currency":"EUR"}}"""));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_request_that_satisfies_the_contract_gets_the_stubs_answer()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Spec30);

        using var created = await client.PostAsync("/payments", Json(Valid));
        using var listed = await Send(client, HttpMethod.Get, "/payments?limit=10", header: ("X-Request-Id", "r-1"));
        using var read = await client.GetAsync("/payments/7");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    public static TheoryData<string, string, string, string> BodyViolations() => new()
    {
        // body, location, pointer, rule
        { """{"money":{"amount":1,"currency":"EUR"}}""", "body", "", "required" },                          // missing required property
        { """{"payer":"a@example.com","money":{"amount":"12","currency":"EUR"}}""", "body", "/money/amount", "type" },   // wrong type
        { """{"payer":"a@example.com","money":{"amount":1,"currency":"GBP"}}""", "body", "/money/currency", "enum" },    // enum
        { """{"payer":"not-an-email","money":{"amount":1,"currency":"EUR"}}""", "body", "/payer", "format" },           // format
        { """{"payer":"a@example.com","money":{"amount":1,"currency":"EUR"},"extra":1}""", "body", "", "additionalProperties" },
        { """{"payer":"a@example.com","money":{"amount":0,"currency":"EUR"}}""", "body", "/money/amount", "exclusiveMinimum" }, // 3.0 boolean form
        { """{"payer":"a@example.com","money":{"amount":1001,"currency":"EUR"}}""", "body", "/money/amount", "maximum" },
        { """{"payer":"a@example.com","money":{"amount":1,"currency":"EUR"},"due":"2026-02-30"}""", "body", "/due", "format" },
        { "not json", "body", "", "json" },
        { "", "body", "", "required" },
    };

    [Theory]
    [MemberData(nameof(BodyViolations))]
    public async Task A_body_that_breaks_the_contract_is_answered_400_naming_the_violation(string body, string location, string pointer, string rule)
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Spec30);

        using var response = await client.PostAsync("/payments", new StringContent(body, Encoding.UTF8, "application/json"));
        var violations = await ViolationsAsync(response);

        Assert.Contains(violations, v => v.Location == location && v.Pointer == pointer && v.Rule == rule);
    }

    public static TheoryData<string, string?, string, string, string> ParameterViolations() => new()
    {
        // url, X-Request-Id, location, pointer, rule
        { "/payments?limit=500", "r-1", "query", "limit", "maximum" },        // a parameter out of range
        { "/payments?limit=0", "r-1", "query", "limit", "minimum" },
        { "/payments?limit=ten", "r-1", "query", "limit", "type" },
        { "/payments?limit=1.5", "r-1", "query", "limit", "type" },             // integer, not number
        { "/payments", null, "header", "X-Request-Id", "required" },           // a required header
        { "/payments/abc", "r-1", "path", "paymentId", "type" },              // a path-item parameter
    };

    [Theory]
    [MemberData(nameof(ParameterViolations))]
    public async Task A_parameter_that_breaks_the_contract_is_answered_400_naming_it(string url, string? requestId, string location, string pointer, string rule)
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Spec30);

        using var response = await Send(client, HttpMethod.Get, url, header: requestId is null ? null : ("X-Request-Id", requestId));
        var violations = await ViolationsAsync(response);

        Assert.Contains(violations, v => v.Location == location && v.Pointer == pointer && v.Rule == rule);
    }

    [Fact]
    public async Task A_nullable_field_takes_null_and_a_header_is_found_whatever_its_case()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Spec30);

        using var created = await client.PostAsync("/payments", Json("""{"payer":"a@example.com","money":{"amount":1,"currency":"EUR"},"note":null}"""));
        using var listed = await Send(client, HttpMethod.Get, "/payments", header: ("x-request-id", "r-1"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
    }

    /// <summary>
    /// The pinned reader (Microsoft.OpenApi 1.6) does not read 3.1, so a 3.1 document is refused at import
    /// rather than imported unvalidated. Pinned here so the day the reader learns 3.1 this goes red and the
    /// 3.1 half of #448 gets its tests (deferred-edges: OpenAPI 3.1 documents).
    /// </summary>
    [Fact]
    public async Task An_OpenApi_31_document_is_refused_at_import_rather_than_imported_unchecked()
    {
        const string spec31 =
            """
            {
              "openapi": "3.1.0",
              "info": { "title": "notes", "version": "1.0" },
              "paths": { "/notes": { "post": {
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object", "required": ["text"], "properties": { "tag": { "type": ["string", "null"] } } } } } },
                "responses": { "201": { "description": "created" } } } } }
            }
            """;
        await using var app = await StartAsync();
        using var client = Client(app);

        using var response = await client.PostAsync("/__admin/openapi/import?validate=true", Json(spec31));

        Assert.Equal(HttpStatusCode.UnprocessableContent, response.StatusCode);
        Assert.Contains("3.1.0", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_write_does_not_reach_sandbox_state()
    {
        const string crud =
            """
            {
              "openapi": "3.0.3", "info": { "title": "c", "version": "1" },
              "paths": {
                "/customers": {
                  "get": { "responses": { "200": { "description": "ok" } } },
                  "post": { "requestBody": { "required": true, "content": { "application/json": { "schema": {
                    "type": "object", "required": ["name"], "properties": { "name": { "type": "string" }, "id": { "type": "string" } } } } } },
                    "responses": { "201": { "description": "created" } } } },
                "/customers/{id}": { "get": { "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
                  "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, crud, query: "stateful=true&validate=true");

        using var refused = await client.PostAsync("/customers", Json("""{"id":"c-1"}"""));
        using var accepted = await client.PostAsync("/customers", Json("""{"id":"c-2","name":"Ada"}"""));
        using var list = await client.GetAsync("/__admin/resources/customers");
        var stored = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.DoesNotContain("c-1", stored, StringComparison.Ordinal);
        Assert.Contains("c-2", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_request_fires_no_post_serve_action_and_an_accepted_one_does()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        var hook = new Uri(client.BaseAddress!, "/hook").ToString();
        var stub = "{\"request\":{\"method\":\"POST\",\"urlPath\":\"/x\",\"validate\":{\"path\":\"/x\",\"body\":{\"required\":true,\"schema\":{\"type\":\"object\",\"required\":[\"a\"]}}}},"
            + "\"response\":{\"status\":200},\"serveEventListeners\":[{\"name\":\"webhook\",\"parameters\":{\"method\":\"POST\",\"url\":\"" + hook + "\"}}]}";
        using (var created = await client.PostAsync("/__admin/mappings", Json(stub)))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var refused = await client.PostAsync("/x", Json("""{"b":1}"""));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await Task.Delay(500);
        Assert.Equal(0, await HookCallsAsync(client));

        using var accepted = await client.PostAsync("/x", Json("""{"a":1}"""));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (await HookCallsAsync(client) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(1, await HookCallsAsync(client));
    }

    private static async Task<int> HookCallsAsync(HttpClient client)
    {
        using var list = JsonDocument.Parse(await client.GetStringAsync("/__admin/requests"));
        return list.RootElement.GetProperty("requests").EnumerateArray().Count(r => r.GetProperty("url").GetString() == "/hook");
    }

    [Fact]
    public async Task The_journal_records_the_violations_of_a_rejected_request()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Spec30);

        using (await client.PostAsync("/payments", Json("""{"money":{"amount":1,"currency":"EUR"}}"""))) { }

        using var list = JsonDocument.Parse(await client.GetStringAsync("/__admin/requests"));
        var id = list.RootElement.GetProperty("requests")[0].GetProperty("id").GetString();
        using var detail = JsonDocument.Parse(await client.GetStringAsync($"/__admin/requests/{id}"));

        Assert.True(detail.RootElement.GetProperty("wasMatched").GetBoolean());
        Assert.Equal(400, detail.RootElement.GetProperty("response").GetProperty("status").GetInt32());
        Assert.Contains(detail.RootElement.GetProperty("requestViolations").EnumerateArray(),
            v => v.GetProperty("rule").GetString() == "required");
    }

    [Fact]
    public async Task The_contract_travels_with_the_stub_through_an_export_and_a_re_import()
    {
        await using var source = await StartAsync();
        using var from = Client(source);
        await ImportAsync(from, Spec30);
        using var exported = JsonDocument.Parse(await from.GetStringAsync("/__admin/mappings"));
        var post = exported.RootElement.GetProperty("mappings").EnumerateArray()
            .Single(m => m.GetProperty("request").GetProperty("method").GetString() == "POST");
        Assert.True(post.GetProperty("request").TryGetProperty("validate", out _));

        await using var target = await StartAsync();
        using var to = Client(target);
        using (var created = await to.PostAsync("/__admin/mappings", Json(post.GetRawText())))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var refused = await to.PostAsync("/payments", Json("""{"payer":"a@example.com"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Theory]
    [InlineData(""" "validate": "yes" """)]
    [InlineData(""" "validate": { "parameters": [] } """)]
    [InlineData(""" "validate": { "path": "/x", "parameters": [ { "in": "query" } ] } """)]
    [InlineData(""" "validate": { "path": "/x", "parameters": [ { "name": "q", "in": "cookie" } ] } """)]
    [InlineData(""" "validate": { "path": "/x", "body": { "required": true } } """)]
    [InlineData(""" "validate": { "path": "/x", "body": { "schema": { "type": 5 } } } """)]
    public async Task A_contract_that_cannot_be_read_refuses_rather_than_serving_unchecked(string validate)
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        using (var created = await client.PostAsync("/__admin/mappings", Json(
            "{\"request\":{\"method\":\"POST\",\"urlPath\":\"/x\"," + validate + "},\"response\":{\"status\":200,\"body\":\"served\"}}")))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using var response = await client.PostAsync("/x", Json("""{"a":1}"""));

        Assert.Contains(await ViolationsAsync(response), v => v.Rule == "contract");
    }

    [Fact]
    public async Task A_number_parameter_reads_the_same_under_a_turkish_culture()
    {
        const string spec =
            """
            { "openapi": "3.0.3", "info": { "title": "r", "version": "1" },
              "paths": { "/rates": { "get": {
                "parameters": [ { "name": "min", "in": "query", "required": true, "schema": { "type": "number", "maximum": 2 } } ],
                "responses": { "200": { "description": "ok" } } } } } }
            """;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            await using var app = await StartAsync();
            using var client = Client(app);
            await ImportAsync(client, spec);

            using var ok = await client.GetAsync("/rates?min=1.5");
            using var comma = await client.GetAsync("/rates?min=1,5");

            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains(await ViolationsAsync(comma), v => v.Pointer == "min" && v.Rule == "type");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task A_hostile_body_gets_a_bounded_answer()
    {
        var properties = string.Join(",", Enumerable.Range(0, 500).Select(i => $"\"p{i}\":{{\"type\":\"integer\"}}"));
        var spec = $$$"""
            { "openapi": "3.0.3", "info": { "title": "h", "version": "1" },
              "paths": { "/h": { "post": {
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object", "properties": { {{{properties}}} } } } } },
                "responses": { "200": { "description": "ok" } } } } } }
            """;
        var body = "{" + string.Join(",", Enumerable.Range(0, 500).Select(i => $"\"p{i}\":\"x\"")) + "}";
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, spec);

        using var response = await client.PostAsync("/h", Json(body));

        Assert.Equal(Mockifyr.Core.RequestValidationGate.MaxViolations, (await ViolationsAsync(response)).Count);
    }

    private sealed record Violation(string Location, string Pointer, string Rule);

    private static async Task<List<Violation>> ViolationsAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(text);
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        return
        [
            .. problem.RootElement.GetProperty("violations").EnumerateArray().Select(v => new Violation(
                v.GetProperty("location").GetString()!, v.GetProperty("pointer").GetString()!, v.GetProperty("rule").GetString()!)),
        ];
    }

    private static async Task ImportAsync(HttpClient client, string spec, bool validate = true, string? query = null)
    {
        var url = "/__admin/openapi/import" + (query is not null ? "?" + query : validate ? "?validate=true" : "");
        using var response = await client.PostAsync(url, Json(spec));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, (string Name, string Value)? header)
    {
        using var request = new HttpRequestMessage(method, url);
        if (header is { } h)
        {
            request.Headers.Add(h.Name, h.Value);
        }

        return await client.SendAsync(request);
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
