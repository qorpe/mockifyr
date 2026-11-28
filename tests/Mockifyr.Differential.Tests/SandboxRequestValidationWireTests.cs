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
    [InlineData(""" "validate": { "path": "/x", "body": { "schemas": {} } } """)]
    [InlineData(""" "validate": { "path": "/x", "body": { "schemas": [] } } """)]
    [InlineData(""" "validate": { "path": "/x", "body": { "schemas": { "application/json": 5 } } } """)]
    [InlineData(""" "validate": { "path": "/x", "body": { "schemas": { " ": {} } } } """)]
    [InlineData(""" "validate": { "path": "/x", "body": { "schema": 5, "schemas": { "application/json": {} } } } """)]
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

    // ---- Verifier findings on #486 ----

    /// <summary>
    /// BLOCKER: a violation's message is built from the contract alone. For the numeric keywords the
    /// evaluator's own text quotes the request's value, so a masked header or body field reached the 400
    /// body and the journal through it. Every keyword family is driven here with a value that is masked,
    /// and the value must appear nowhere: not in the answer, not in the journal entry.
    /// </summary>
    [Fact]
    public async Task A_masked_value_appears_neither_in_the_answer_nor_in_the_journal_whatever_rule_it_breaks()
    {
        const string spec =
            """
            {
              "openapi": "3.0.3", "info": { "title": "cards", "version": "1.0" },
              "paths": { "/cards": { "post": {
                "parameters": [
                  { "name": "X-Card", "in": "header", "required": true, "schema": { "type": "integer", "maximum": 100 } },
                  { "name": "X-Pin", "in": "header", "schema": { "type": "string", "pattern": "^[0-9]{4}$", "maxLength": 4, "enum": ["1234"] } },
                  { "name": "X-Step", "in": "header", "schema": { "type": "number", "multipleOf": 7, "exclusiveMinimum": true, "minimum": 9000000 } } ],
                "requestBody": { "required": true, "content": { "application/json": { "schema": {
                  "type": "object", "additionalProperties": false, "properties": {
                    "amount": { "type": "integer", "maximum": 1 },
                    "pan": { "type": "string", "format": "email", "maxLength": 3, "enum": ["x"] },
                    "cvv": { "type": "integer", "minimum": 99999999, "multipleOf": 1000 } } } } } },
                "responses": { "201": { "description": "ok" } } } } }
            }
            """;
        await using var app = await StartAsync("--mask-headers", "X-Card,X-Pin,X-Step", "--mask-body-fields", "pan,cvv,amount");
        using var client = Client(app);
        await ImportAsync(client, spec);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/cards")
        {
            Content = Json("""{"amount":555777,"pan":"4111111111111111","cvv":271828}"""),
        };
        request.Headers.Add("X-Card", "4111111111111111");
        request.Headers.Add("X-Pin", "98765");
        request.Headers.Add("X-Step", "31415926");
        using var response = await client.SendAsync(request);
        var answer = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var journal = await client.GetAsync("/__admin/requests");
        var entries = await journal.Content.ReadAsStringAsync();
        using var listed = JsonDocument.Parse(entries);
        var id = listed.RootElement.GetProperty("requests")[0].GetProperty("id").GetString();
        var entry = await client.GetStringAsync($"/__admin/requests/{id}");

        Assert.Contains("requestViolations", entry);
        foreach (var secret in new[] { "4111111111111111", "98765", "31415926", "555777", "271828" })
        {
            Assert.DoesNotContain(secret, answer, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, entry, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, entries, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_body_pointer_built_from_the_callers_keys_is_bounded()
    {
        const string spec =
            """
            { "openapi": "3.0.3", "info": { "title": "m", "version": "1" },
              "paths": { "/m": { "post": {
                "requestBody": { "content": { "application/json": { "schema": { "type": "object", "additionalProperties": { "type": "integer" } } } } },
                "responses": { "201": { "description": "ok" } } } } } }
            """;
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, spec);

        var key = new string('k', 5000);
        using var response = await client.PostAsync("/m", Json($$"""{"{{key}}":"not an integer"}"""));

        var violation = Assert.Single(await ViolationsAsync(response), v => v.Rule == "type");
        Assert.True(violation.Pointer.Length <= 257, $"pointer of {violation.Pointer.Length} characters");
        Assert.DoesNotContain(new string('k', 300), await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private const string Serializations =
        """
        {
          "openapi": "3.0.3", "info": { "title": "serializations", "version": "1.0" },
          "paths": {
            "/h": { "get": {
              "parameters": [ { "name": "X-Ids", "in": "header", "required": true, "schema": { "type": "array", "items": { "type": "integer" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/q": { "get": {
              "parameters": [ { "name": "ids", "in": "query", "required": true, "explode": false, "schema": { "type": "array", "items": { "type": "integer" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/e": { "get": {
              "parameters": [ { "name": "ids", "in": "query", "required": true, "schema": { "type": "array", "items": { "type": "integer" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/p/{ids}": { "get": {
              "parameters": [ { "name": "ids", "in": "path", "required": true, "schema": { "type": "array", "items": { "type": "integer" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/pipe": { "get": {
              "parameters": [ { "name": "ids", "in": "query", "required": true, "style": "pipeDelimited", "schema": { "type": "array", "items": { "type": "integer" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/deep": { "get": {
              "parameters": [ { "name": "f", "in": "query", "required": true, "style": "deepObject", "schema": { "type": "object", "properties": { "a": { "type": "integer" } } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/obj": { "get": {
              "parameters": [ { "name": "f", "in": "query", "required": true, "schema": { "type": "object", "properties": { "a": { "type": "integer" } } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/any": { "get": {
              "parameters": [ { "name": "n", "in": "query", "required": true, "schema": { "anyOf": [ { "type": "integer" }, { "type": "boolean" } ] } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/one": { "get": {
              "parameters": [ { "name": "n", "in": "query", "required": true, "schema": { "oneOf": [ { "type": "integer" }, { "type": "boolean" } ] } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/all": { "get": {
              "parameters": [ { "name": "n", "in": "query", "required": true, "schema": { "allOf": [ { "type": "integer" }, { "minimum": 1 } ] } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/token": { "post": {
              "requestBody": { "required": true, "content": {
                "application/json": { "schema": { "type": "object", "required": ["grant"], "properties": { "grant": { "type": "string" } } } },
                "application/x-www-form-urlencoded": { "schema": { "type": "object" } } } },
              "responses": { "201": { "description": "ok" } } } }
          }
        }
        """;

    /// <summary>
    /// MAJOR 2: a request the contract allows is never refused because the validator would read it in a
    /// serialization, shape or media type it does not implement. Members: a header array (simple), a query
    /// array with explode false, an exploded query array, a path array (simple), pipeDelimited, deepObject,
    /// an object in form style, a parameter whose type comes from anyOf, oneOf or allOf, and a form body on
    /// an operation that also declares JSON.
    /// </summary>
    [Theory]
    [InlineData("GET", "/h", "X-Ids", "1, 2,3", null)]
    [InlineData("GET", "/q?ids=1,2", null, null, null)]
    [InlineData("GET", "/e?ids=1&ids=2", null, null, null)]
    [InlineData("GET", "/p/1,2", null, null, null)]
    [InlineData("GET", "/pipe?ids=1|2", null, null, null)]
    [InlineData("GET", "/deep?f[a]=1", null, null, null)]
    [InlineData("GET", "/obj?a=1", null, null, null)]
    [InlineData("GET", "/any?n=7", null, null, null)]
    [InlineData("GET", "/one?n=true", null, null, null)]
    [InlineData("GET", "/all?n=7", null, null, null)]
    [InlineData("POST", "/token", null, null, "application/x-www-form-urlencoded")]
    [InlineData("POST", "/token", null, null, "application/json")]
    public async Task A_request_the_contract_allows_is_served_in_every_serialization(string method, string url, string? header, string? value, string? mediaType)
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Serializations);

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (header is not null)
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }

        if (mediaType is not null)
        {
            request.Content = new StringContent(mediaType == "application/json" ? """{"grant":"client_credentials"}""" : "grant=client_credentials", Encoding.UTF8, mediaType);
        }

        using var response = await client.SendAsync(request);

        Assert.True((int)response.StatusCode is 200 or 201, $"{method} {url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>The same rules refuse what the contract does not allow, so none of them passes vacuously.</summary>
    [Theory]
    [InlineData("GET", "/h", "X-Ids", "1,two", null, "header", "X-Ids", "type")]
    [InlineData("GET", "/q?ids=1,two", null, null, null, "query", "ids", "type")]
    [InlineData("GET", "/e?ids=1&ids=two", null, null, null, "query", "ids", "type")]
    [InlineData("GET", "/p/1,two", null, null, null, "path", "ids", "type")]
    [InlineData("GET", "/pipe", null, null, null, "query", "ids", "required")]
    [InlineData("GET", "/any", null, null, null, "query", "n", "required")]
    [InlineData("POST", "/token", null, null, "text/plain", "body", "", "mediaType")]
    [InlineData("POST", "/token", null, null, "application/json", "body", "", "required")]
    public async Task What_the_contract_does_not_allow_is_still_refused(
        string method, string url, string? header, string? value, string? mediaType, string location, string pointer, string rule)
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Serializations);

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (header is not null)
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }

        if (mediaType is not null)
        {
            request.Content = new StringContent(mediaType == "application/json" ? "{}" : "grant", Encoding.UTF8, mediaType);
        }

        using var response = await client.SendAsync(request);

        Assert.Contains(await ViolationsAsync(response), v => v.Location == location && v.Pointer == pointer && v.Rule == rule);
    }

    private const string Shapes =
        """
        {
          "openapi": "3.0.3", "info": { "title": "shapes", "version": "1.0" },
          "paths": {
            "/q": { "get": {
              "parameters": [ { "name": "ids", "in": "query", "required": true, "explode": false, "schema": { "type": "array", "items": { "type": "integer" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/qmin": { "get": {
              "parameters": [ { "name": "ids", "in": "query", "required": true, "explode": false, "schema": { "type": "array", "minItems": 1, "items": { "type": "integer" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/h": { "get": {
              "parameters": [ { "name": "X-Ids", "in": "header", "schema": { "type": "array", "items": { "type": "integer" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/hmin": { "get": {
              "parameters": [ { "name": "X-Ids", "in": "header", "required": true, "schema": { "type": "array", "minItems": 1, "items": { "type": "integer" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/tags": { "get": {
              "parameters": [ { "name": "tags", "in": "query", "required": true, "explode": false, "schema": { "type": "array", "maxItems": 1, "items": { "type": "string" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/ptags/{tags}": { "get": {
              "parameters": [ { "name": "tags", "in": "path", "required": true, "schema": { "type": "array", "maxItems": 1, "items": { "type": "string" } } } ],
              "responses": { "200": { "description": "ok" } } } },
            "/m": { "patch": {
              "requestBody": { "required": true, "content": {
                "application/json": { "schema": { "type": "object", "required": ["a"], "properties": { "a": { "type": "integer" } } } },
                "application/merge-patch+json": { "schema": { "type": "object", "properties": { "a": { "type": "integer" } } } },
                "application/vnd.shapes.v2+json": { "example": {} } } },
              "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

    /// <summary>
    /// Verifier round 2 on #486, the class "a value or body read in a shape that is not the one the contract
    /// declares": an empty array in non-exploded form and in a header, a comma that was percent-encoded inside a
    /// query or path item, a body judged by another JSON media type's schema, and a body sent with no type.
    /// </summary>
    [Theory]
    [InlineData("GET", "/q?ids=", null, null, null, null)]
    [InlineData("GET", "/h", "X-Ids", "", null, null)]
    [InlineData("GET", "/h", "X-Ids", " ", null, null)]
    [InlineData("GET", "/tags?tags=a%2Cb", null, null, null, null)]
    [InlineData("GET", "/ptags/a%2Cb", null, null, null, null)]
    [InlineData("PATCH", "/m", null, null, "application/merge-patch+json", "{}")]
    [InlineData("PATCH", "/m", null, null, "application/merge-patch+json; charset=utf-8", "{}")]
    [InlineData("PATCH", "/m", null, null, "application/vnd.shapes.v2+json", "{}")]
    [InlineData("PATCH", "/m", null, null, "application/json", """{"a":1}""")]
    public async Task A_value_is_read_in_the_shape_the_contract_declares(
        string method, string url, string? header, string? value, string? mediaType, string? body)
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Shapes);

        using var response = await client.SendAsync(Request(method, url, header, value, mediaType, body));

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>The same reads still refuse what the contract does not allow, so none of them passes vacuously.</summary>
    [Theory]
    [InlineData("GET", "/qmin?ids=", null, null, null, null, "query", "ids", "minItems")]
    [InlineData("GET", "/hmin", "X-Ids", "", null, null, "header", "X-Ids", "minItems")]
    [InlineData("GET", "/q?ids=1,,2", null, null, null, null, "query", "ids", "type")]
    [InlineData("GET", "/tags?tags=a,b", null, null, null, null, "query", "tags", "maxItems")]
    [InlineData("PATCH", "/m", null, null, "application/json", "{}", "body", "", "required")]
    [InlineData("PATCH", "/m", null, null, "APPLICATION/JSON", "{}", "body", "", "required")]
    [InlineData("PATCH", "/m", null, null, "application/merge-patch+json", """{"a":"x"}""", "body", "/a", "type")]
    [InlineData("PATCH", "/m", null, null, "application/problem+json", "{}", "body", "", "mediaType")]
    public async Task A_value_read_in_its_declared_shape_is_still_judged(
        string method, string url, string? header, string? value, string? mediaType, string? body, string location, string pointer, string rule)
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Shapes);

        using var response = await client.SendAsync(Request(method, url, header, value, mediaType, body));

        Assert.Contains(await ViolationsAsync(response), v => v.Location == location && v.Pointer == pointer && v.Rule == rule);
    }

    /// <summary>
    /// A body with no Content-Type is judged as before: by the schema of the first JSON media type the operation
    /// declares. A hand-written contract with a single <c>schema</c> and no media types keeps reading and judging
    /// every JSON body by it.
    /// </summary>
    [Fact]
    public async Task An_untyped_body_and_a_contract_with_one_schema_are_judged_as_before()
    {
        await using var app = await StartAsync();
        using var client = Client(app);
        await ImportAsync(client, Shapes);

        using (var untyped = new HttpRequestMessage(HttpMethod.Patch, "/m") { Content = new ByteArrayContent("{}"u8.ToArray()) })
        using (var response = await client.SendAsync(untyped))
        {
            Assert.Contains(await ViolationsAsync(response), v => v.Location == "body" && v.Rule == "required");
        }

        using (var created = await client.PostAsync("/__admin/mappings", Json(
            """{"request":{"method":"POST","urlPath":"/old","validate":{"path":"/old","body":{"required":true,"schema":{"type":"object","required":["a"]}}}},"response":{"status":200}}""")))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var ok = await client.SendAsync(Request("POST", "/old", null, null, "application/merge-patch+json", """{"a":1}""")))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var refused = await client.SendAsync(Request("POST", "/old", null, null, "application/merge-patch+json", "{}"));
        Assert.Contains(await ViolationsAsync(refused), v => v.Location == "body" && v.Rule == "required");
    }

    /// <summary>
    /// The in-process facade hands a header value over untrimmed (HTTP trims it on the wire), so a blank value of
    /// a header array is the empty array there too, and still refused under <c>minItems: 1</c>.
    /// </summary>
    [Theory]
    [InlineData(" ", null, 0)]
    [InlineData(" ", 1, 1)]
    [InlineData(" 1 , 2 ", null, 0)]
    public void A_blank_header_array_is_the_empty_array_in_process(string value, int? minItems, int expected)
    {
        var schema = minItems is { } min
            ? """{"type":"array","minItems":""" + min.ToString(CultureInfo.InvariantCulture) + ""","items":{"type":"integer"}}"""
            : """{"type":"array","items":{"type":"integer"}}""";
        var contract = new Mockifyr.Core.RequestValidationDirective(
            "/h", [new Mockifyr.Core.ContractParameter("X-Ids", Mockifyr.Core.ContractParameterLocation.Header, true, schema)], null);
        var request = Mockifyr.Core.CanonicalRequestBuilder.Build("GET", "/h", [new KeyValuePair<string, string>("X-Ids", value)]);

        var violations = new Mockifyr.Adapters.OpenApi.OpenApiRequestValidator().Validate(request, contract);

        Assert.Equal(expected, violations.Count);
        Assert.All(violations, v => Assert.Equal("minItems", v.Rule));
    }

    private static HttpRequestMessage Request(string method, string url, string? header, string? value, string? mediaType, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (header is not null)
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }

        if (mediaType is not null)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body ?? ""));
            request.Content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
        }

        return request;
    }

    /// <summary>
    /// MINOR 3: a request the host answered without serving its stub is still counted, with the status it was
    /// given: a contract rejection (400) and an idempotent replay. Observers are told; post-serve actions are not
    /// (the webhook test above holds that half).
    /// </summary>
    [Fact]
    public async Task A_contract_rejection_and_an_idempotent_replay_are_counted()
    {
        await using var app = await StartAsync("--metrics", "true", "--idempotency", "true");
        using var client = Client(app);
        await ImportAsync(client, Spec30);

        using var rejected = await client.PostAsync("/payments", Json("""{"money":{"amount":1,"currency":"EUR"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal((1, 400.0), await ServedAsync(client, "POST", 1));

        var replayed = false;
        for (var i = 0; i < 2; i++)
        {
            using var post = new HttpRequestMessage(HttpMethod.Post, "/payments") { Content = Json(Valid) };
            post.Headers.Add("Idempotency-Key", "k-1");
            using var answered = await client.SendAsync(post);
            Assert.Equal(HttpStatusCode.Created, answered.StatusCode);
            replayed = answered.Headers.Contains("Idempotency-Replayed");
        }

        Assert.True(replayed, "the second request was not a replay");
        Assert.Equal((3, 400.0 + 201 + 201), await ServedAsync(client, "POST", 3));
    }

    /// <summary>
    /// The served count once it reaches <paramref name="expected"/> (or after five seconds): the exporter caches a
    /// scrape for a moment, so one read straight after a request can be the previous scrape.
    /// </summary>
    private static async Task<(long Count, double StatusSum)> ServedAsync(HttpClient client, string method, long expected)
    {
        var served = (Count: 0L, StatusSum: 0.0);
        for (var attempt = 0; attempt < 50 && served.Count < expected; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(100);
            }

            served = Served(await client.GetStringAsync("/__admin/metrics"), method);
        }

        return served;
    }

    /// <summary>The served counter for one method, and the sum of the statuses recorded for it.</summary>
    private static (long Count, double StatusSum) Served(string scrape, string method)
    {
        long count = 0;
        double sum = 0;
        foreach (var line in scrape.Split('\n'))
        {
            if (!line.Contains($"method=\"{method}\"", StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[(line.LastIndexOf(' ') + 1)..];
            if (line.Contains("requests_served", StringComparison.Ordinal) && !line.StartsWith('#'))
            {
                count += (long)double.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (line.Contains("response_status_sum", StringComparison.Ordinal))
            {
                sum += double.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        return (count, sum);
    }

    /// <summary>
    /// NIT on #486: the 3.0 to 2020-12 translation rewrites schemas only. A literal under example, default,
    /// enum, const or examples is data, and its "nullable" or "exclusiveMinimum" key stays as written; a schema
    /// under properties, items, allOf or additionalProperties is still translated.
    /// </summary>
    [Fact]
    public void The_translation_rewrites_schemas_and_never_a_literal()
    {
        var translated = JsonDocument.Parse(Mockifyr.Adapters.OpenApi.OpenApiRequestValidator.ToJsonSchema(
            """
            { "type": "object",
              "example": { "nullable": true, "exclusiveMinimum": true, "minimum": 1 },
              "default": { "nullable": false },
              "enum": [ { "nullable": true } ],
              "properties": { "n": { "type": "string", "nullable": true } },
              "additionalProperties": { "type": "number", "minimum": 0, "exclusiveMinimum": true },
              "allOf": [ { "type": "integer", "nullable": true } ],
              "items": { "type": "boolean", "nullable": true } }
            """)).RootElement;

        Assert.Equal("""{"nullable":true,"exclusiveMinimum":true,"minimum":1}""", translated.GetProperty("example").GetRawText());
        Assert.Equal("""{"nullable":false}""", translated.GetProperty("default").GetRawText());
        Assert.Equal("""[{"nullable":true}]""", translated.GetProperty("enum").GetRawText());
        Assert.Equal("""["string","null"]""", translated.GetProperty("properties").GetProperty("n").GetProperty("type").GetRawText());
        Assert.Equal(0, translated.GetProperty("additionalProperties").GetProperty("exclusiveMinimum").GetInt32());
        Assert.Equal("""["integer","null"]""", translated.GetProperty("allOf")[0].GetProperty("type").GetRawText());
        Assert.Equal("""["boolean","null"]""", translated.GetProperty("items").GetProperty("type").GetRawText());
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

    private static async Task<WebApplication> StartAsync(params string[] args)
    {
        var app = MockifyrHost.Build(["--port", "0", .. args]);
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
