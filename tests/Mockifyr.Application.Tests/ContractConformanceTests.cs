using Mockifyr.Adapters.OpenApi;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for contract conformance (#287): does this stub set still tell the truth about
/// the specification? No oracle — the reference engine has no conformance surface — so this is a
/// self-test suite in the G18/G19 tradition.
/// </summary>
public sealed class ContractConformanceTests
{
    private const string Spec =
        """
        {
          "openapi": "3.0.0",
          "info": { "title": "orders", "version": "1.0" },
          "paths": {
            "/orders": {
              "get": {
                "responses": {
                  "200": { "description": "ok", "content": { "application/json": { "schema": {
                    "type": "array", "items": { "type": "object",
                      "required": ["id", "total"],
                      "properties": { "id": { "type": "string" }, "total": { "type": "number" } } } } } } }
                }
              },
              "post": { "responses": { "201": { "description": "created" } } }
            },
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

    private static StubUnderTest Stub(string mappingJson) => new(Guid.NewGuid(), mappingJson);

    private static StubUnderTest Get(string path, int status = 200, string? body = null)
    {
        var response = body is null
            ? $$"""{"status":{{status}}}"""
            : $$"""{"status":{{status}},"body":{{System.Text.Json.JsonSerializer.Serialize(body)}}}""";

        return Stub($$"""{"request":{"method":"GET","urlPath":"{{path}}"},"response":{{response}}}""");
    }

    [Fact]
    public void A_faithful_stub_set_conforms()
    {
        var report = ContractConformance.Verify(Spec,
        [
            Get("/orders", body: """[{"id":"a","total":1}]"""),
            Get("/orders/42", body: """{"id":"a","total":1}"""),
            Stub("""{"request":{"method":"POST","urlPath":"/orders"},"response":{"status":201}}"""),
        ]);

        Assert.True(report.Conforms);
        Assert.Empty(report.Findings);
        Assert.Equal(3, report.OperationsInSpec);
        Assert.Equal(3, report.OperationsCovered);
    }

    [Fact]
    public void A_stub_for_an_operation_the_spec_dropped_is_reported()
    {
        var report = ContractConformance.Verify(Spec, [Get("/legacy/orders")]);

        // The upstream removed an endpoint and the stub set kept answering it: tests stay green while
        // the client's real call would 404. This is the finding that justifies the whole feature.
        var finding = report.Findings.Single(f => f.Kind == DriftKind.UndeclaredOperation);
        Assert.Equal("/legacy/orders", finding.Path);
        Assert.Equal("GET", finding.Method);
        Assert.NotNull(finding.StubId);
    }

    [Fact]
    public void An_operation_no_stub_answers_is_reported()
    {
        var report = ContractConformance.Verify(Spec, [Get("/orders", body: """[{"id":"a","total":1}]""")]);

        var uncovered = report.Findings.Where(f => f.Kind == DriftKind.UncoveredOperation).ToList();

        Assert.Equal(2, uncovered.Count);
        Assert.Null(uncovered[0].StubId);
        Assert.Equal(1, report.OperationsCovered);
        Assert.Equal(3, report.OperationsInSpec);
    }

    [Fact]
    public void A_status_the_spec_does_not_declare_is_reported()
    {
        var report = ContractConformance.Verify(Spec, [Get("/orders", status: 418)]);

        var finding = report.Findings.Single(f => f.Kind == DriftKind.UndeclaredStatus);
        Assert.Contains("418", finding.Detail);
    }

    [Fact]
    public void A_missing_required_field_is_reported_with_its_pointer()
    {
        var report = ContractConformance.Verify(Spec, [Get("/orders/42", body: """{"id":"a"}""")]);

        // The upstream added a required field and the stub did not follow — the drift that produces a
        // NullReferenceException in production and nowhere else.
        var finding = report.Findings.Single(f => f.Kind == DriftKind.SchemaViolation);
        Assert.Contains("total", finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_wrong_type_is_reported()
    {
        var report = ContractConformance.Verify(Spec, [Get("/orders/42", body: """{"id":"a","total":"free"}""")]);

        Assert.Contains(report.Findings, f => f.Kind == DriftKind.SchemaViolation);
    }

    [Fact]
    public void A_body_that_is_not_json_at_all_is_reported()
    {
        var report = ContractConformance.Verify(Spec, [Get("/orders/42", body: "not json")]);

        var finding = report.Findings.Single(f => f.Kind == DriftKind.SchemaViolation);
        Assert.Contains("not JSON", finding.Detail);
    }

    [Fact]
    public void A_templated_body_is_left_alone()
    {
        var report = ContractConformance.Verify(Spec,
        [
            Get("/orders/42", body: """{"id":"{{request.pathSegments.[1]}}","total":1}"""),
            Get("/orders", body: """[{"id":"a","total":1}]"""),
            Stub("""{"request":{"method":"POST","urlPath":"/orders"},"response":{"status":201}}"""),
        ]);

        // A template is not JSON until a request renders it. Reporting drift on every templated stub is
        // the fastest way to make a conformance report something people stop reading.
        Assert.True(report.Conforms);
    }

    [Fact]
    public void A_concrete_path_still_belongs_to_its_templated_operation()
    {
        // Nobody writes stubs against {id}; they write them against /orders/42. If those did not match
        // the operation, every realistic stub set would report as entirely undeclared *and* entirely
        // uncovered — a report that is wrong twice about the same stub.
        var report = ContractConformance.Verify(Spec, [Get("/orders/42", body: """{"id":"a","total":1}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredOperation);
        Assert.Equal(1, report.OperationsCovered);
    }

    [Fact]
    public void A_differently_named_template_variable_is_the_same_operation()
    {
        var report = ContractConformance.Verify(Spec,
            [Stub("""{"request":{"method":"GET","urlPathTemplate":"/orders/{orderId}"},"response":{"status":200,"body":"{\"id\":\"a\",\"total\":1}"}}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredOperation);
    }

    [Fact]
    public void A_stub_that_is_not_an_http_operation_is_ignored()
    {
        var report = ContractConformance.Verify(Spec,
        [
            Get("/orders", body: """[{"id":"a","total":1}]"""),
            Get("/orders/42", body: """{"id":"a","total":1}"""),
            Stub("""{"request":{"method":"POST","urlPath":"/orders"},"response":{"status":201}}"""),
            // A message-channel stub sharing the tenant, and one matching by regular expression: neither
            // can be compared against a specification path without guessing, and a wrong guess is a
            // false finding — the thing that makes people stop reading reports.
            Stub("""{"request":{"urlPattern":"/orders/[0-9]+"},"response":{"status":200}}"""),
            Stub("""{"messageMappings":[{"whenMessage":{"equalTo":"ping"}}]}"""),
        ]);

        Assert.True(report.Conforms);
    }

    [Fact]
    public void A_query_string_on_the_stub_does_not_hide_the_operation()
    {
        var report = ContractConformance.Verify(Spec,
            [Stub("""{"request":{"method":"GET","url":"/orders?page=2"},"response":{"status":200,"body":"[{\"id\":\"a\",\"total\":1}]"}}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredOperation);
    }

    [Fact]
    public void A_default_response_covers_a_status_the_spec_does_not_list()
    {
        const string withDefault =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{"/thing":{"get":{"responses":{"default":{"description":"anything"}}}}}}
            """;

        var report = ContractConformance.Verify(withDefault, [Get("/thing", status: 503)]);

        Assert.True(report.Conforms);
    }

    [Fact]
    public void An_operation_with_no_schema_is_not_second_guessed()
    {
        const string schemaless =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{"/thing":{"get":{"responses":{"200":{"description":"ok",
               "content":{"application/json":{}}}}}}}}
            """;

        // "The document does not say" and "the stub is wrong" are different answers, and only one of
        // them belongs in a report.
        Assert.True(ContractConformance.Verify(schemaless, [Get("/thing", body: """{"anything":true}""")]).Conforms);
    }

    [Fact]
    public void Findings_come_back_in_a_stable_order()
    {
        var first = ContractConformance.Verify(Spec, [Get("/legacy/a"), Get("/legacy/b")]);
        var again = ContractConformance.Verify(Spec, [Get("/legacy/b"), Get("/legacy/a")]);

        // A report that reshuffles between runs cannot be diffed, and a conformance report nobody can
        // diff cannot be wired into CI.
        Assert.Equal(
            first.Findings.Select(f => (f.Kind, f.Method, f.Path)),
            again.Findings.Select(f => (f.Kind, f.Method, f.Path)));
    }

    [Fact]
    public void An_external_ref_is_refused_exactly_as_the_importer_refuses_it()
    {
        const string external =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{"/thing":{"get":{"responses":{"200":{"description":"ok",
               "content":{"application/json":{"schema":{"$ref":"https://evil.example.com/schema.json"}}}}}}}}}
            """;

        // Verification takes a document from exactly the same untrusted place an import does, so it
        // applies the same guard rather than a weaker one.
        var refused = Assert.Throws<OpenApiImportException>(() => ContractConformance.Verify(external, []));
        Assert.Equal(OpenApiImportError.ExternalRef, refused.Error);
    }

    [Fact]
    public void An_unparseable_document_is_refused()
    {
        Assert.Throws<OpenApiImportException>(() => ContractConformance.Verify("{ not a spec", []));
    }

    [Theory]
    [InlineData("urlPath")]
    [InlineData("urlPathTemplate")]
    [InlineData("url")]
    public void Every_spelling_of_the_path_is_read(string field)
    {
        // The dialect has three ways to write the path a stub answers on. Reading only some of them
        // would silently ignore whole stub sets — reported as fully uncovered, which reads as "you have
        // written no stubs" to somebody who has written plenty.
        var report = ContractConformance.Verify(Spec,
            [Stub($$$"""{"request":{"method":"POST","{{{field}}}":"/orders"},"response":{"status":201}}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredOperation);
        Assert.Equal(1, report.OperationsCovered);
    }

    [Fact]
    public void A_stub_with_a_method_but_no_path_is_ignored()
    {
        var report = ContractConformance.Verify(Spec,
            [Stub("""{"request":{"method":"GET","headers":{"X":{"equalTo":"y"}}},"response":{"status":200}}""")]);

        // Both halves are needed to name an operation; having one is not enough to guess at the other.
        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredOperation);
    }

    [Fact]
    public void A_response_with_no_status_is_read_as_200()
    {
        // The dialect's own default. Reading it as 0 would report every such stub as answering a status
        // the specification does not declare — a wall of findings about stubs that are all fine.
        var report = ContractConformance.Verify(Spec,
            [Stub("""{"request":{"method":"GET","urlPath":"/orders"},"response":{"body":"[{\"id\":\"a\",\"total\":1}]"}}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredStatus);
    }

    [Fact]
    public void A_status_written_as_a_string_is_not_read_as_a_number()
    {
        // "200" is not 200 in this dialect, and quietly accepting it would make the report disagree with
        // what the engine actually serves.
        var report = ContractConformance.Verify(Spec,
            [Stub("""{"request":{"method":"GET","urlPath":"/orders"},"response":{"status":"418"}}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredStatus);
    }

    [Fact]
    public void A_non_string_body_is_not_mistaken_for_one()
    {
        // `jsonBody` puts an object where `body` puts a string; treating the two the same would compare
        // the wrong thing and report drift that is not there.
        var report = ContractConformance.Verify(Spec,
            [Stub("""{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"jsonBody":{"id":"a"}}}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.SchemaViolation);
    }

    [Fact]
    public void Findings_are_ordered_by_path_then_method_then_kind()
    {
        var report = ContractConformance.Verify(Spec,
        [
            Get("/zebra"),
            Stub("""{"request":{"method":"POST","urlPath":"/alpha"},"response":{"status":200}}"""),
        ]);

        // Pinned explicitly rather than "two runs agree": a report wired into CI is diffed between
        // builds, and an order that is merely self-consistent still churns the diff when it changes.
        Assert.Equal(
            ["/alpha", "/orders", "/orders", "/orders/{id}", "/zebra"],
            report.Findings.Select(f => f.Path));
    }

    [Fact]
    public void The_two_findings_that_name_no_pointer_still_explain_themselves()
    {
        var report = ContractConformance.Verify(Spec, [Get("/legacy")]);

        // These two carry no schema pointer, so the sentence is all the reader gets; it has to say which
        // side is missing something.
        Assert.Contains("declares no such operation",
            report.Findings.Single(f => f.Kind == DriftKind.UndeclaredOperation).Detail);
        Assert.Contains("no stub answers it",
            report.Findings.First(f => f.Kind == DriftKind.UncoveredOperation).Detail);
    }

    [Fact]
    public void A_schema_that_serializes_to_nothing_useful_still_validates()
    {
        // SchemaJson renders the specification's schema for the validator; if it produced an empty
        // document every body would pass and the whole check would be decorative.
        var report = ContractConformance.Verify(Spec, [Get("/orders/42", body: """{"id":1,"total":"x"}""")]);

        Assert.Contains(report.Findings, f => f.Kind == DriftKind.SchemaViolation);
    }

    [Fact]
    public void The_path_precedence_matches_what_the_engine_serves()
    {
        // A mapping carrying both url and urlPath is served on `url` — the engine's own precedence. If
        // the check read them in a different order it would report confidently about an endpoint the
        // stub is not answering, which is worse than not reporting at all.
        var report = ContractConformance.Verify(Spec,
            [Stub("""{"request":{"method":"POST","url":"/orders","urlPath":"/somewhere-else"},"response":{"status":201}}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredOperation);
    }

    [Fact]
    public void A_literal_path_wins_over_a_templated_one_that_also_agrees()
    {
        const string ambiguous =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{
               "/orders/new":{"get":{"responses":{"418":{"description":"literal"}}}},
               "/orders/{id}":{"get":{"responses":{"200":{"description":"templated"}}}}}}
            """;

        // Both operations agree with a stub for /orders/new. It is answering the literal one, and
        // leaving that to enumeration order would decide it by alphabet — which is the same as luck.
        var report = ContractConformance.Verify(ambiguous, [Get("/orders/new", status: 418)]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredStatus);
        Assert.Contains(report.Findings, f => f.Kind == DriftKind.UncoveredOperation && f.Path == "/orders/{id}");
    }

    [Fact]
    public void A_body_that_is_not_a_string_at_all_is_not_read_as_one()
    {
        // `body` is a string in this dialect. Reading a number there as text would throw rather than
        // report, and a conformance run that crashes on an odd stub is a conformance run nobody trusts.
        var report = ContractConformance.Verify(Spec,
            [Stub("""{"request":{"method":"GET","urlPath":"/orders/1"},"response":{"status":200,"body":42}}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.SchemaViolation);
    }

    [Fact]
    public void Findings_on_one_path_are_ordered_by_method_then_kind()
    {
        const string twoMethods =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{"/thing":{
               "get":{"responses":{"200":{"description":"ok"}}},
               "delete":{"responses":{"204":{"description":"gone"}}}}}}
            """;

        var report = ContractConformance.Verify(twoMethods, []);

        // The tie-breakers matter as much as the primary key: a report wired into CI is diffed between
        // builds, and two findings swapping places churn the diff for no reason.
        Assert.Equal(["DELETE", "GET"], report.Findings.Select(f => f.Method));
    }

    [Fact]
    public void Two_equally_templated_paths_resolve_the_same_way_every_run()
    {
        const string ambiguous =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{
               "/{tenant}/orders":{"get":{"responses":{"418":{"description":"first by name"}}}},
               "/acme/{thing}":{"get":{"responses":{"200":{"description":"second by name"}}}}}}
            """;

        // Both declare one wildcard and both agree with /acme/orders. Preferring fewer wildcards cannot
        // separate them, so the path name does — arbitrary, but *stable*, which is what a report wired
        // into CI needs. Deciding it by enumeration order would make the same stub set report
        // differently on a document whose keys were merely reordered.
        var report = ContractConformance.Verify(ambiguous, [Get("/acme/orders", status: 200)]);

        // Ordinal order puts /acme/{thing} first, so that is the operation the stub is credited with —
        // and the other is reported as uncovered. Which one wins matters less than that it is always the
        // same one; this assertion exists to make a change to that rule visible rather than silent.
        Assert.Equal(
            [(DriftKind.UncoveredOperation, "/{tenant}/orders")],
            report.Findings.Select(f => (f.Kind, f.Path)));
    }

    [Fact]
    public void The_least_templated_path_wins_however_long_the_names_are()
    {
        const string ambiguous =
            """
            {"openapi":"3.0.0","info":{"title":"t","version":"1"},
             "paths":{
               "/orders/summary":{"get":{"responses":{"418":{"description":"literal"}}}},
               "/orders/{extremelyLongParameterName}":{"get":{"responses":{"200":{"description":"templated"}}}}}}
            """;

        // The preference counts wildcards, not characters: a literal path must win even when it is the
        // shorter string, or the rule is really "whichever name happens to sort first".
        var report = ContractConformance.Verify(ambiguous, [Get("/orders/summary", status: 418)]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredStatus);
    }

    [Fact]
    public void urlPath_is_read_before_urlPathTemplate()
    {
        // Mirrors the engine's precedence for the remaining pair, for the same reason as `url` above.
        var report = ContractConformance.Verify(Spec,
            [Stub("""{"request":{"method":"POST","urlPath":"/orders","urlPathTemplate":"/nowhere/{id}"},"response":{"status":201}}""")]);

        Assert.DoesNotContain(report.Findings, f => f.Kind == DriftKind.UndeclaredOperation);
    }

    [Fact]
    public void An_empty_stub_set_reports_every_operation_as_uncovered()
    {
        var report = ContractConformance.Verify(Spec, []);

        Assert.False(report.Conforms);
        Assert.Equal(3, report.Findings.Count);
        Assert.All(report.Findings, f => Assert.Equal(DriftKind.UncoveredOperation, f.Kind));
        Assert.Equal(0, report.OperationsCovered);
    }
}
