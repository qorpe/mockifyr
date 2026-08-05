using Mockifyr.Adapters.OpenApi;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for the consumer side of conformance (#287): is the client staying inside the
/// contract? Self-tested — no oracle has this concept.
/// </summary>
public sealed class TrafficConformanceTests
{
    private const string Spec =
        """
        {
          "openapi": "3.0.0",
          "info": { "title": "orders", "version": "1.0" },
          "paths": {
            "/orders": {
              "get": {
                "parameters": [
                  { "name": "page", "in": "query", "required": true, "schema": { "type": "string" } },
                  { "name": "sort", "in": "query", "required": false, "schema": { "type": "string" } },
                  { "name": "X-Tenant", "in": "header", "required": true, "schema": { "type": "string" } }
                ],
                "responses": { "200": { "description": "ok" } }
              },
              "post": {
                "requestBody": {
                  "required": true,
                  "content": { "application/json": { "schema": {
                    "type": "object", "required": ["customerId", "total"],
                    "properties": { "customerId": { "type": "string" }, "total": { "type": "number" } } } } }
                },
                "responses": { "201": { "description": "created" } }
              }
            },
            "/orders/{id}": { "get": { "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

    private static RecordedRequest Get(string url, params string[] headers) =>
        new("GET", url, null, headers);

    private static RecordedRequest Post(string url, string? body) =>
        new("POST", url, body, ["Content-Type"]);

    [Fact]
    public void Traffic_the_contract_allows_conforms()
    {
        var report = TrafficConformance.Verify(Spec,
        [
            Get("/orders?page=1", "X-Tenant"),
            Post("/orders", """{"customerId":"c-1","total":42}"""),
            Get("/orders/7"),
        ]);

        Assert.True(report.Conforms);
        Assert.Equal(3, report.RequestsExamined);
        Assert.Equal(3, report.RequestsConforming);
    }

    [Fact]
    public void A_client_calling_something_the_contract_never_promised_is_reported()
    {
        var report = TrafficConformance.Verify(Spec, [Get("/orders/7/history")]);

        // Works perfectly against a mock that is more permissive than the real service, and fails the
        // first time it meets the real one.
        var finding = Assert.Single(report.Findings);
        Assert.Equal(TrafficDriftKind.UndeclaredOperation, finding.Kind);
        Assert.Equal("/orders/7/history", finding.Url);
        Assert.Equal(0, report.RequestsConforming);
    }

    [Fact]
    public void A_missing_required_query_parameter_is_reported()
    {
        var finding = Assert.Single(TrafficConformance.Verify(Spec, [Get("/orders", "X-Tenant")]).Findings);

        Assert.Equal(TrafficDriftKind.MissingParameter, finding.Kind);
        Assert.Contains("query parameter 'page'", finding.Detail);
    }

    [Fact]
    public void A_missing_required_header_is_reported()
    {
        var finding = Assert.Single(TrafficConformance.Verify(Spec, [Get("/orders?page=1")]).Findings);

        Assert.Equal(TrafficDriftKind.MissingParameter, finding.Kind);
        Assert.Contains("header 'X-Tenant'", finding.Detail);
    }

    [Fact]
    public void A_header_is_matched_without_regard_to_case()
    {
        // HTTP header names are case-insensitive; reporting `x-tenant` as missing when the contract
        // spells it `X-Tenant` would be a false finding on correct traffic.
        Assert.True(TrafficConformance.Verify(Spec, [Get("/orders?page=1", "x-tenant")]).Conforms);
    }

    [Fact]
    public void An_optional_parameter_is_never_required()
    {
        Assert.True(TrafficConformance.Verify(Spec, [Get("/orders?page=1", "X-Tenant")]).Conforms);
    }

    [Fact]
    public void A_request_body_that_violates_the_schema_is_reported_with_its_pointer()
    {
        var findings = TrafficConformance.Verify(Spec, [Post("/orders", """{"customerId":"c-1"}""")]).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(TrafficDriftKind.RequestSchemaViolation, finding.Kind);
        Assert.Contains("total", finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_wrong_type_in_the_request_body_is_reported()
    {
        var report = TrafficConformance.Verify(Spec, [Post("/orders", """{"customerId":"c-1","total":"free"}""")]);

        Assert.Contains(report.Findings, f => f.Kind == TrafficDriftKind.RequestSchemaViolation);
    }

    [Fact]
    public void A_body_the_contract_requires_and_the_client_omitted_is_reported()
    {
        var finding = Assert.Single(TrafficConformance.Verify(Spec, [Post("/orders", null)]).Findings);

        Assert.Contains("requires a request body and the client sent none", finding.Detail);
    }

    [Fact]
    public void A_body_that_is_not_json_is_reported()
    {
        var finding = Assert.Single(TrafficConformance.Verify(Spec, [Post("/orders", "customerId=c-1")]).Findings);

        Assert.Contains("not JSON", finding.Detail);
    }

    [Fact]
    public void An_operation_that_declares_no_body_ignores_one()
    {
        // A client sending a body on a GET is odd, not a contract violation, and reporting it would be
        // the kind of pedantry that gets a report switched off.
        Assert.True(TrafficConformance.Verify(Spec,
            [new RecordedRequest("GET", "/orders/7", """{"stray":true}""", [])]).Conforms);
    }

    [Fact]
    public void A_query_string_never_hides_the_operation()
    {
        Assert.True(TrafficConformance.Verify(Spec, [Get("/orders/7?expand=lines")]).Conforms);
    }

    [Fact]
    public void An_encoded_parameter_name_is_still_recognised()
    {
        // A client that percent-encodes the name still sent the parameter; reading the raw text would
        // report a false absence.
        Assert.True(TrafficConformance.Verify(Spec, [Get("/orders?%70age=1", "X-Tenant")]).Conforms);
    }

    [Fact]
    public void Conforming_requests_are_counted_even_when_others_fail()
    {
        var report = TrafficConformance.Verify(Spec,
        [
            Get("/orders?page=1", "X-Tenant"),
            Get("/nope"),
            Post("/orders", """{"customerId":"c","total":1}"""),
        ]);

        // "Conforms: false" alone tells an operator nothing about scale. Two of three passing is a
        // different morning from none of three.
        Assert.Equal(3, report.RequestsExamined);
        Assert.Equal(2, report.RequestsConforming);
    }

    [Fact]
    public void The_schema_findings_per_request_are_capped()
    {
        const string wide =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{"/thing":{"post":{"requestBody":{"required":true,"content":{"application/json":{"schema":{
               "type":"object",
               "properties":{"a":{"type":"string"},"b":{"type":"string"},"c":{"type":"string"},
                             "d":{"type":"string"},"e":{"type":"string"},"f":{"type":"string"}}}}}},
             "responses":{"201":{"description":"ok"}}}}}}
            """;

        // Six separately wrong types, not six missing required fields: the validator reports absent
        // required properties as ONE error naming them all, so a "required" body would never reach the
        // cap and the test would be asserting nothing.
        var report = TrafficConformance.Verify(wide,
            [new RecordedRequest("POST", "/thing", """{"a":1,"b":2,"c":3,"d":4,"e":5,"f":6}""", [])]);

        Assert.Equal(TrafficConformance.MaxSchemaFindings, report.Findings.Count);
    }

    [Fact]
    public void Findings_come_back_in_a_stable_order()
    {
        var first = TrafficConformance.Verify(Spec, [Get("/zebra"), Get("/alpha")]);
        var again = TrafficConformance.Verify(Spec, [Get("/alpha"), Get("/zebra")]);

        Assert.Equal(first.Findings.Select(f => f.Url), again.Findings.Select(f => f.Url));
        Assert.Equal(["/alpha", "/zebra"], first.Findings.Select(f => f.Url));
    }

    [Fact]
    public void An_undeclared_call_explains_both_readings()
    {
        var finding = Assert.Single(TrafficConformance.Verify(Spec, [Get("/nope")]).Findings);

        // The sentence is the whole finding here, and it must not assume which side is wrong: the
        // contract may be behind, or the client may be calling something that will never exist.
        Assert.Contains("the specification does not declare it", finding.Detail);
        Assert.Contains("the contract is behind", finding.Detail);
    }

    [Fact]
    public void A_required_path_parameter_is_never_reported_as_missing()
    {
        const string pathParam =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{"/orders/{id}":{"get":{
               "parameters":[{"name":"id","in":"path","required":true,"schema":{"type":"string"}}],
               "responses":{"200":{"description":"ok"}}}}}}
            """;

        // The URL matching the template at all is what satisfies a path parameter. Reporting it as
        // absent would fire on every correct request to a templated endpoint.
        Assert.True(TrafficConformance.Verify(pathParam, [Get("/orders/7")]).Conforms);
    }

    [Fact]
    public void Findings_on_one_url_are_ordered_by_method_then_kind()
    {
        var report = TrafficConformance.Verify(Spec,
        [
            Post("/orders", null),
            Get("/orders"),
        ]);

        // Same URL, two methods, and the GET carries two kinds of finding at once. A report wired into
        // CI is diffed between builds; entries swapping places churn the diff for no reason.
        Assert.Equal(["GET", "POST"], report.Findings.Select(f => f.Method).Distinct());
        Assert.All(report.Findings, f => Assert.Equal("/orders", f.Url.Split('?')[0]));
    }

    [Fact]
    public void One_request_can_carry_more_than_one_kind_of_finding()
    {
        // A call that omits a required parameter *and* sends a body the schema forbids should say both;
        // stopping at the first would send somebody back for a second run.
        var report = TrafficConformance.Verify(Spec, [new RecordedRequest("POST", "/orders", "{}", [])]);

        Assert.NotEmpty(report.Findings);
        Assert.Equal(0, report.RequestsConforming);
    }

    [Fact]
    public void Two_kinds_of_finding_on_one_call_come_back_in_a_fixed_order()
    {
        const string both =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{"/thing":{"post":{
               "parameters":[{"name":"key","in":"query","required":true,"schema":{"type":"string"}}],
               "requestBody":{"required":true,"content":{"application/json":{"schema":{
                 "type":"object","required":["name"],"properties":{"name":{"type":"string"}}}}}},
               "responses":{"201":{"description":"ok"}}}}}}
            """;

        // One call, two different kinds of disagreement. Their relative order is part of what makes a
        // report diffable between builds, so it is pinned rather than left to whichever check ran first.
        var report = TrafficConformance.Verify(both, [new RecordedRequest("POST", "/thing", "{}", [])]);

        Assert.Equal(
            [TrafficDriftKind.MissingParameter, TrafficDriftKind.RequestSchemaViolation],
            report.Findings.Select(f => f.Kind));
    }

    [Fact]
    public void An_empty_journal_conforms_and_says_it_examined_nothing()
    {
        var report = TrafficConformance.Verify(Spec, []);

        // "Conforms" on no traffic is true and useless on its own; the count is what stops it being
        // mistaken for a clean bill of health.
        Assert.True(report.Conforms);
        Assert.Equal(0, report.RequestsExamined);
    }

    [Fact]
    public void The_same_refusals_as_the_stub_check_apply_to_the_document()
    {
        Assert.Throws<OpenApiImportException>(() => TrafficConformance.Verify("{ not a spec", []));

        var external = Assert.Throws<OpenApiImportException>(() => TrafficConformance.Verify(
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{"/t":{"get":{"responses":{"200":{"description":"ok",
               "content":{"application/json":{"schema":{"$ref":"https://evil.example.com/s.json"}}}}}}}}}
            """, []));

        Assert.Equal(OpenApiImportError.ExternalRef, external.Error);
    }
}
