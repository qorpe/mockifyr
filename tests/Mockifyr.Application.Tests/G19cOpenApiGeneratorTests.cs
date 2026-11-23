using System.Text.Json;
using Mockifyr.Adapters.OpenApi;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Golden coverage for the OpenAPI stub generator (G19c, ADR 0011) against curated specs — the
/// petstore (JSON) and a real-world-shaped orders API (YAML). Serving the generated stubs is
/// proven separately over the wire; these tests pin the generation table: path/method/status
/// selection, example-over-synthesis precedence, format-aware synthesis, stateful pair wiring,
/// and every typed refusal of the ADR addendum (size, external ref, invalid, empty, depth).
/// </summary>
public sealed class G19cOpenApiGeneratorTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine("Fixtures", name));

    private static List<JsonDocument> Generate(string spec, bool stateful = false) =>
        [.. OpenApiStubGenerator.Generate(spec, stateful).Select(m => JsonDocument.Parse(m))];

    private static (string? Method, string? Path, string? Template) RequestOf(JsonDocument mapping)
    {
        var request = mapping.RootElement.GetProperty("request");
        return (
            request.GetProperty("method").GetString(),
            request.TryGetProperty("urlPath", out var p) ? p.GetString() : null,
            request.TryGetProperty("urlPathTemplate", out var t) ? t.GetString() : null);
    }

    [Fact]
    public void Petstore_generates_one_mapping_per_operation_with_the_right_shapes()
    {
        var mappings = Generate(Fixture("petstore.json"));

        Assert.Equal(3, mappings.Count);

        var list = Assert.Single(mappings, m => RequestOf(m).Path == "/pets" && RequestOf(m).Method == "GET");
        Assert.Equal(200, list.RootElement.GetProperty("response").GetProperty("status").GetInt32());
        // The array-of-Pet schema synthesizes one sample item; email/uuid formats become helper
        // expressions, which opt the stub into templating.
        var body = list.RootElement.GetProperty("response").GetProperty("body").GetString()!;
        Assert.Contains("\"name\":\"string\"", body);
        Assert.Contains("{{random 'Internet.emailAddress'}}", body);
        Assert.Contains("{{randomValue type='UUID'}}", body);
        Assert.Equal("response-template",
            list.RootElement.GetProperty("response").GetProperty("transformers")[0].GetString());

        var create = Assert.Single(mappings, m => RequestOf(m).Method == "POST");
        Assert.Equal(201, create.RootElement.GetProperty("response").GetProperty("status").GetInt32());
        Assert.False(create.RootElement.GetProperty("response").TryGetProperty("body", out _));

        var show = Assert.Single(mappings, m => RequestOf(m).Template == "/pets/{petId}");
        Assert.Equal("showPetById", show.RootElement.GetProperty("name").GetString());
        // A declared example always wins over synthesis.
        Assert.Contains("\"Odie\"", show.RootElement.GetProperty("response").GetProperty("body").GetString());
    }

    [Fact]
    public void The_orders_yaml_spec_generates_enum_first_and_format_aware_samples()
    {
        var mappings = Generate(Fixture("orders-api.yaml"));

        Assert.Equal(7, mappings.Count);

        var get = Assert.Single(mappings, m => m.RootElement.GetProperty("name").GetString() == "getOrder");
        var body = get.RootElement.GetProperty("response").GetProperty("body").GetString()!;
        Assert.Contains("\"status\":\"created\"", body);
        Assert.Contains("\"total\":1.5", body);
        Assert.Contains("\"paid\":true", body);

        var invoice = Assert.Single(mappings, m => m.RootElement.GetProperty("name").GetString() == "getInvoice");
        var invoiceBody = invoice.RootElement.GetProperty("response").GetProperty("body").GetString()!;
        Assert.Contains("{{random 'Internet.url'}}", invoiceBody);
        Assert.Contains("\"issued\":\"2026-01-01\"", invoiceBody);

        var remove = Assert.Single(mappings, m => m.RootElement.GetProperty("name").GetString() == "deleteOrder");
        Assert.Equal(204, remove.RootElement.GetProperty("response").GetProperty("status").GetInt32());
    }

    [Fact]
    public void Stateful_wires_the_resource_pair_and_leaves_other_operations_as_examples()
    {
        var mappings = Generate(Fixture("orders-api.yaml"), stateful: true);

        Assert.Equal(7, mappings.Count);

        string? StateOp(JsonDocument m) =>
            m.RootElement.GetProperty("response").TryGetProperty("state", out var s)
                ? s.GetProperty("operation").GetString()
                : null;

        Assert.Equal("create", StateOp(Assert.Single(mappings, m => RequestOf(m) is { Method: "POST", Path: "/api/orders" })));
        Assert.Equal("list", StateOp(Assert.Single(mappings, m => RequestOf(m) is { Method: "GET", Path: "/api/orders" })));
        var read = Assert.Single(mappings, m => RequestOf(m) is { Method: "GET", Template: "/api/orders/{orderId}" });
        Assert.Equal("read", StateOp(read));
        Assert.Equal("{{request.pathSegments.[2]}}",
            read.RootElement.GetProperty("response").GetProperty("state").GetProperty("id").GetString());
        Assert.Equal("update", StateOp(Assert.Single(mappings, m => RequestOf(m).Method == "PUT")));
        Assert.Equal("delete", StateOp(Assert.Single(mappings, m => RequestOf(m).Method == "DELETE")));

        // The nested /invoice path and /api/health are NOT resource-shaped: they stay example stubs.
        Assert.Null(StateOp(Assert.Single(mappings, m => m.RootElement.GetProperty("name").GetString() == "getInvoice")));
        Assert.Null(StateOp(Assert.Single(mappings, m => m.RootElement.GetProperty("name").GetString() == "health")));

        var create = Assert.Single(mappings, m => RequestOf(m) is { Method: "POST", Path: "/api/orders" });
        // Composed from the request's own path rather than the specification's template text. For this
        // top-level collection both forms render to the same "/api/orders/<id>"; for a nested one the
        // template still carries "{customerId}", and building the header from it handed the client a
        // Location it could not follow (found by serving a nested spec, ADR 0015).
        Assert.Equal("{{request.path}}/{{state.id}}",
            create.RootElement.GetProperty("response").GetProperty("headers").GetProperty("Location").GetString());
    }

    [Fact]
    public void Collection_names_are_sanitized_into_the_identifier_shape()
    {
        Assert.Equal("orders", OpenApiStubGenerator.CollectionName("/api/orders"));
        Assert.Equal("order-items", OpenApiStubGenerator.CollectionName("/api/order.items"));
        Assert.Equal("c-9lives", OpenApiStubGenerator.CollectionName("/9lives"));
        Assert.Equal(64, OpenApiStubGenerator.CollectionName("/" + new string('x', 80)).Length);
    }

    [Fact]
    public void The_generated_output_matches_the_committed_goldens_byte_for_byte()
    {
        // Golden equality kills what spot-checks cannot: every literal in the generated mapping
        // (names, statuses, headers, state wiring, templating opt-in) is part of the contract.
        Assert.Equal(
            File.ReadAllLines(Path.Combine("Fixtures", "petstore.golden.jsonl")),
            OpenApiStubGenerator.Generate(Fixture("petstore.json")));
        Assert.Equal(
            File.ReadAllLines(Path.Combine("Fixtures", "orders-stateful.golden.jsonl")),
            OpenApiStubGenerator.Generate(Fixture("orders-api.yaml"), stateful: true));
    }

    [Fact]
    public void Response_selection_prefers_2xx_then_default_then_the_lowest_declared()
    {
        static string Spec(string responses) =>
            """{"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{"/a":{"get":{"responses":{""" + responses + "}}}}}";

        static int StatusOf(string spec)
        {
            using var doc = JsonDocument.Parse(OpenApiStubGenerator.Generate(spec)[0]);
            return doc.RootElement.GetProperty("response").GetProperty("status").GetInt32();
        }

        Assert.Equal(200, StatusOf(Spec("\"default\":{\"description\":\"d\"}")));
        Assert.Equal(404, StatusOf(Spec("\"500\":{\"description\":\"e\"},\"404\":{\"description\":\"n\"}")));
        Assert.Equal(200, StatusOf(Spec("\"404\":{\"description\":\"n\"},\"default\":{\"description\":\"d\"}")));
        Assert.Equal(201, StatusOf(Spec("\"404\":{\"description\":\"n\"},\"201\":{\"description\":\"c\"},\"202\":{\"description\":\"a\"}")));
    }

    [Fact]
    public void A_non_json_content_type_is_carried_into_the_stub()
    {
        var spec = """
            {"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{"/txt":{"get":{
              "responses":{"200":{"description":"ok","content":{"text/plain":{"schema":{"type":"string"}}}}}}}}}
            """;

        using var doc = JsonDocument.Parse(OpenApiStubGenerator.Generate(spec)[0]);
        var response = doc.RootElement.GetProperty("response");
        Assert.Equal("text/plain", response.GetProperty("headers").GetProperty("Content-Type").GetString());
        Assert.Equal("\"string\"", response.GetProperty("body").GetString());
    }

    [Fact]
    public void Item_paths_without_their_collection_do_not_wire_state()
    {
        var spec = """
            {"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{
              "/solo/{id}":{"get":{"responses":{"200":{"description":"ok"}}}},
              "/{id}":{"get":{"responses":{"200":{"description":"ok"}}}}}}
            """;

        foreach (var mapping in OpenApiStubGenerator.Generate(spec, stateful: true))
        {
            using var doc = JsonDocument.Parse(mapping);
            Assert.False(doc.RootElement.GetProperty("response").TryGetProperty("state", out _));
        }
    }

    [Fact]
    public void A_3xx_only_choice_still_prefers_default_and_json_wins_among_content_types()
    {
        // 300 is NOT a success: default (as 200) outranks it.
        var redirecting = """{"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{"/a":{"get":{"responses":{"300":{"description":"r"},"default":{"description":"d"}}}}}}""";
        using (var doc = JsonDocument.Parse(OpenApiStubGenerator.Generate(redirecting)[0]))
        {
            Assert.Equal(200, doc.RootElement.GetProperty("response").GetProperty("status").GetInt32());
        }

        // With several content types, application/json wins regardless of declaration order.
        var multi = """{"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{"/a":{"get":{"responses":{"200":{"description":"ok","content":{"text/plain":{"schema":{"type":"string"}},"application/json":{"schema":{"type":"boolean"}}}}}}}}}""";
        using (var doc = JsonDocument.Parse(OpenApiStubGenerator.Generate(multi)[0]))
        {
            var response = doc.RootElement.GetProperty("response");
            Assert.Equal("application/json", response.GetProperty("headers").GetProperty("Content-Type").GetString());
            Assert.Equal("true", response.GetProperty("body").GetString());
        }
    }

    [Fact]
    public void A_half_templated_last_segment_is_not_resource_shaped()
    {
        var spec = """{"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{"/p":{"get":{"responses":{"200":{"description":"ok"}}}},"/p/{bad":{"get":{"responses":{"200":{"description":"ok"}}}}}}""";

        foreach (var mapping in OpenApiStubGenerator.Generate(spec, stateful: true))
        {
            using var doc = JsonDocument.Parse(mapping);
            Assert.False(doc.RootElement.GetProperty("response").TryGetProperty("state", out _));
        }
    }

    [Fact]
    public void An_operation_without_an_id_is_named_method_and_path()
    {
        var spec = """{"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{"/a":{"get":{"responses":{"200":{"description":"ok"}}}}}}""";

        using var doc = JsonDocument.Parse(OpenApiStubGenerator.Generate(spec)[0]);
        Assert.Equal("GET /a", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void External_refs_are_refused_with_the_offending_pointer_and_never_fetched()
    {
        var spec = """
            {"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{"/a":{"get":{
              "responses":{"200":{"description":"ok","content":{"application/json":{
                "schema":{"$ref":"https://evil.example.com/steal.json#/S"}}}}}}}}}
            """;

        var refusal = Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.Generate(spec));
        Assert.Equal(OpenApiImportError.ExternalRef, refusal.Error);
        Assert.StartsWith("https://evil.example.com/", refusal.Pointer);
    }

    [Fact]
    public void The_remaining_typed_refusals_cover_size_parse_empty_and_depth()
    {
        Assert.Equal(OpenApiImportError.TooLarge,
            Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.Generate(new string('x', OpenApiStubGenerator.MaxSpecBytes + 1))).Error);

        Assert.Equal(OpenApiImportError.Invalid,
            Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.Generate("{\"not\":\"openapi\"}")).Error);

        Assert.Equal(OpenApiImportError.Empty,
            Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.Generate(
                """{"openapi":"3.0.3","info":{"title":"x","version":"1"},"paths":{}}""")).Error);

        // A self-referencing schema recurses past the depth guard — refused, never a hang.
        var cyclic = """
            {"openapi":"3.0.3","info":{"title":"x","version":"1"},
             "paths":{"/a":{"get":{"responses":{"200":{"description":"ok","content":{"application/json":{
               "schema":{"$ref":"#/components/schemas/Node"}}}}}}}},
             "components":{"schemas":{"Node":{"type":"object","properties":{"next":{"$ref":"#/components/schemas/Node"}}}}}}
            """;
        Assert.Equal(OpenApiImportError.TooDeep,
            Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.Generate(cyclic)).Error);
    }

    // ---- named examples as scenario states (#449) ----

    private const string EdgeSpec = """
        openapi: 3.0.3
        info: { title: Edges, version: "1" }
        paths:
          /pay:
            post:
              operationId: pay
              responses:
                "201":
                  description: ok
                  content:
                    application/json:
                      examples:
                        approved: { value: { s: A } }
                        Started: { value: { s: S } }
                        "late": { externalValue: "https://example.test/late.json" }
                        twice: { value: { s: T201 } }
                "409":
                  description: conflict
                  content:
                    application/json:
                      examples:
                        twice: { value: { s: T409 } }
                        conflict: { value: { s: C } }
          /items:
            get:
              responses:
                "200":
                  description: list
                  content:
                    application/json:
                      examples:
                        empty: { value: [] }
            post:
              responses:
                "201": { description: created }
          /items/{id}:
            get:
              responses:
                "200":
                  description: one
                  content:
                    application/json:
                      examples:
                        gone: { value: { id: x } }
        """;

    [Fact]
    public void Every_named_example_that_cannot_be_a_state_is_reported_with_its_reason_and_binds_nothing()
    {
        var import = OpenApiStubGenerator.GenerateWithRelations(EdgeSpec, stateful: true, scenarios: true, scenarioName: "edges");
        var report = Assert.IsType<OpenApiScenarioReport>(import.Scenarios);

        Assert.Equal("edges", report.Scenario);
        Assert.Equal(["pay approved 201", "pay twice 201", "pay conflict 409"], report.States.Select(s => $"{s.Operation} {s.State} {s.Status}"));
        var ignored = report.Ignored.ToDictionary(i => $"{i.Operation} {i.State}", i => i.Reason);
        Assert.Equal(5, ignored.Count);
        Assert.StartsWith("'Started' is the state every scenario starts in", ignored["pay Started"], StringComparison.Ordinal);
        Assert.StartsWith("the example has no inline value", ignored["pay late"], StringComparison.Ordinal);
        Assert.StartsWith("the 201 response already binds this state", ignored["pay twice"], StringComparison.Ordinal);
        Assert.StartsWith("the operation is wired to sandbox state", ignored["GET /items empty"], StringComparison.Ordinal);
        Assert.StartsWith("the operation is wired to sandbox state", ignored["GET /items/{id} gone"], StringComparison.Ordinal);

        // Exactly one stub per bound state, at priority 1, bound to the one scenario; the 201 "twice" wins.
        var bound = import.Mappings.Select(m => JsonDocument.Parse(m).RootElement).Where(m => m.TryGetProperty("scenarioName", out _)).ToList();
        Assert.Equal(3, bound.Count);
        Assert.All(bound, m => Assert.Equal((1, "edges"), (m.GetProperty("priority").GetInt32(), m.GetProperty("scenarioName").GetString())));
        var twice = Assert.Single(bound, m => m.GetProperty("requiredScenarioState").GetString() == "twice");
        Assert.Equal(201, twice.GetProperty("response").GetProperty("status").GetInt32());
        Assert.Contains("T201", twice.GetProperty("response").GetProperty("body").GetString(), StringComparison.Ordinal);
        var conflict = Assert.Single(bound, m => m.GetProperty("requiredScenarioState").GetString() == "conflict");
        Assert.Equal(409, conflict.GetProperty("response").GetProperty("status").GetInt32());
    }

    [Fact]
    public void Without_scenarios_the_mappings_are_exactly_the_ones_an_import_generated_before()
    {
        var plain = OpenApiStubGenerator.GenerateWithRelations(EdgeSpec);
        var withScenarios = OpenApiStubGenerator.GenerateWithRelations(EdgeSpec, scenarios: true);

        Assert.Null(plain.Scenarios);
        Assert.Equal(OpenApiStubGenerator.Generate(EdgeSpec), plain.Mappings);
        // The default stubs stay byte-identical with scenarios on: the example stubs are added beside them.
        Assert.Equal(plain.Mappings, withScenarios.Mappings.Where(m => !JsonDocument.Parse(m).RootElement.TryGetProperty("scenarioName", out _)));
        Assert.All(plain.Mappings, m => Assert.False(JsonDocument.Parse(m).RootElement.TryGetProperty("priority", out _)));
    }

    [Theory]
    [InlineData(null, "Edges")]
    [InlineData("  payments  ", "payments")]
    [InlineData("çekirdek İ", "çekirdek İ")]
    public void The_scenario_name_is_the_one_asked_for_trimmed_or_the_document_title(string? asked, string expected)
    {
        var import = OpenApiStubGenerator.GenerateWithRelations(EdgeSpec, scenarios: true, scenarioName: asked);
        Assert.Equal(expected, import.Scenarios!.Scenario);
    }

    [Fact]
    public void A_scenario_name_at_the_bound_is_accepted_and_one_over_it_is_refused()
    {
        var atBound = new string('n', OpenApiStubGenerator.MaxScenarioNameLength);
        Assert.Equal(atBound, OpenApiStubGenerator.GenerateWithRelations(EdgeSpec, scenarios: true, scenarioName: atBound).Scenarios!.Scenario);

        var refused = Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.GenerateWithRelations(
            EdgeSpec, scenarios: true, scenarioName: atBound + "n"));
        Assert.Equal(OpenApiImportError.Invalid, refused.Error);
        Assert.StartsWith("The scenario name is 129 characters long; a scenario name is at most 128", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "The scenario name is empty")]
    [InlineData("   ", "The scenario name is empty")]
    [InlineData("a\nb", "The scenario name carries a control character")]
    [InlineData("a\u0000b", "The scenario name carries a control character")]
    [InlineData("v1/payments", "The scenario name contains '/'")]
    [InlineData(" .. ", "The scenario name is only dots")]
    public void An_unusable_scenario_name_is_refused_naming_the_fix(string asked, string expected)
    {
        var refused = Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.GenerateWithRelations(
            EdgeSpec, scenarios: true, scenarioName: asked));
        Assert.StartsWith(expected, refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("pass ?scenario=<name>.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_the_state_route_cannot_select_needs_an_explicit_name()
    {
        var slashed = EdgeSpec.Replace("info: { title: Edges, version: \"1\" }", "info: { title: \"Payments / v1\", version: \"1\" }", StringComparison.Ordinal);
        Assert.NotEqual(EdgeSpec, slashed);
        var refused = Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.GenerateWithRelations(slashed, scenarios: true));
        Assert.StartsWith("The document's info.title contains '/'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_without_a_usable_title_needs_an_explicit_name()
    {
        var untitled = EdgeSpec.Replace("info: { title: Edges, version: \"1\" }", "info: { title: \"  \", version: \"1\" }", StringComparison.Ordinal);
        var refused = Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.GenerateWithRelations(untitled, scenarios: true));
        Assert.StartsWith("The document's info.title is empty", refused.Message, StringComparison.Ordinal);
        Assert.Equal("named", OpenApiStubGenerator.GenerateWithRelations(untitled, scenarios: true, scenarioName: "named").Scenarios!.Scenario);
    }

    [Fact]
    public void A_state_name_with_a_control_character_is_reported_shown_safely_and_not_bound()
    {
        var spec = EdgeSpec.Replace("conflict: { value: { s: C } }", "\"con\\tflict\": { value: { s: C } }", StringComparison.Ordinal);
        var report = OpenApiStubGenerator.GenerateWithRelations(spec, scenarios: true, scenarioName: "edges").Scenarios!;
        var ignored = Assert.Single(report.Ignored, i => i.State == "con?flict");
        Assert.StartsWith("a state name must be non-empty and carry no control character", ignored.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(report.States, s => s.State.Contains('\t', StringComparison.Ordinal));
    }

    [Fact]
    public void A_bound_stub_is_named_for_its_state_and_carries_its_content_type_and_templating_only_when_needed()
    {
        const string spec = """
            openapi: 3.0.3
            info: { title: Kinds, version: "1" }
            paths:
              /echo:
                get:
                  operationId: echo
                  responses:
                    "200":
                      description: text only
                      content:
                        text/plain:
                          examples:
                            plain: { value: "hello" }
                            templated: { value: "you asked for {{request.path}}" }
            """;
        var bound = OpenApiStubGenerator.GenerateWithRelations(spec, scenarios: true).Mappings
            .Select(m => JsonDocument.Parse(m).RootElement)
            .Where(m => m.TryGetProperty("scenarioName", out _))
            .ToDictionary(m => m.GetProperty("requiredScenarioState").GetString()!);

        Assert.Equal("echo [plain]", bound["plain"].GetProperty("name").GetString());
        foreach (var stub in bound.Values)
        {
            // No application/json is declared, so the first content type answers, and it rides into the stub.
            Assert.Equal("text/plain", stub.GetProperty("response").GetProperty("headers").GetProperty("Content-Type").GetString());
        }

        Assert.False(bound["plain"].GetProperty("response").TryGetProperty("transformers", out _));
        Assert.Equal("response-template", bound["templated"].GetProperty("response").GetProperty("transformers")[0].GetString());
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(65, true)]
    public void A_refused_state_name_is_shown_at_most_64_characters_long(int length, bool cut)
    {
        var name = "\t" + new string('s', length - 1);
        var spec = EdgeSpec.Replace("conflict: { value: { s: C } }", $"\"{name.Replace("\t", "\\t", StringComparison.Ordinal)}\": {{ value: {{ s: C }} }}", StringComparison.Ordinal);
        var ignored = Assert.Single(
            OpenApiStubGenerator.GenerateWithRelations(spec, scenarios: true, scenarioName: "edges").Scenarios!.Ignored,
            i => i.Reason.StartsWith("a state name must", StringComparison.Ordinal));

        var shown = "?" + new string('s', Math.Min(length, 64) - 1);
        Assert.Equal(cut ? shown + "…" : shown, ignored.State);
    }

    [Fact]
    public void Among_several_content_types_the_json_examples_are_the_ones_bound()
    {
        const string spec = """
            openapi: 3.0.3
            info: { title: Types, version: "1" }
            paths:
              /doc:
                get:
                  operationId: doc
                  responses:
                    "200":
                      description: two types
                      content:
                        application/xml:
                          examples:
                            only-xml: { value: "<a/>" }
                        application/json:
                          examples:
                            json-one: { value: { a: 1 } }
            """;
        var report = OpenApiStubGenerator.GenerateWithRelations(spec, scenarios: true).Scenarios!;
        var state = Assert.Single(report.States);
        Assert.Equal("json-one", state.State);

        var stub = OpenApiStubGenerator.GenerateWithRelations(spec, scenarios: true).Mappings
            .Select(m => JsonDocument.Parse(m).RootElement)
            .Single(m => m.TryGetProperty("scenarioName", out _));
        Assert.Equal("application/json", stub.GetProperty("response").GetProperty("headers").GetProperty("Content-Type").GetString());
    }

    [Fact]
    public void The_reserved_state_is_the_one_every_scenario_starts_in()
        => Assert.Equal(Mockifyr.Stores.InMemory.InMemoryScenarioStateStore.Started, OpenApiStubGenerator.InitialScenarioState);

    // ---- the status an example is served with: only one its document declares (#449, verifier round 1) ----

    private static string KeyedSpec(string key) => $$"""
        openapi: 3.0.3
        info: { title: Keys, version: "1" }
        paths:
          /pay:
            post:
              operationId: pay
              responses:
                "201":
                  description: ok
                  content:
                    application/json:
                      examples:
                        approved: { value: { s: A } }
                "{{key}}":
                  description: keyed
                  content:
                    application/json:
                      examples:
                        keyed: { value: { s: K } }
        """;

    /// <summary>
    /// The class: a response key that is not a plain three-digit code. Each shape OpenAPI 3 defines answers the status
    /// it declares (a range its lowest code), and every other key is reported, never served with a fallback 200.
    /// </summary>
    [Theory]
    [InlineData("402", 402)]
    [InlineData("4XX", 400)]
    [InlineData("5XX", 500)]
    [InlineData("1XX", 100)]
    [InlineData("3XX", 300)]
    [InlineData("599", 599)]
    [InlineData("100", 100)]
    public void A_named_example_answers_the_status_its_response_key_declares(string key, int expected)
    {
        var report = OpenApiStubGenerator.GenerateWithRelations(KeyedSpec(key), scenarios: true).Scenarios!;

        var keyed = Assert.Single(report.States, s => s.State == "keyed");
        Assert.Equal((key, expected), (keyed.Response, keyed.Status));
        Assert.Empty(report.Ignored);
        var stub = OpenApiStubGenerator.GenerateWithRelations(KeyedSpec(key), scenarios: true).Mappings
            .Select(m => JsonDocument.Parse(m).RootElement)
            .Single(m => m.TryGetProperty("requiredScenarioState", out var state) && state.GetString() == "keyed");
        Assert.Equal(expected, stub.GetProperty("response").GetProperty("status").GetInt32());
    }

    [Fact]
    public void A_named_example_under_default_is_reported_and_never_served()
    {
        var import = OpenApiStubGenerator.GenerateWithRelations(KeyedSpec("default"), scenarios: true);
        var report = import.Scenarios!;

        Assert.Equal(["approved"], report.States.Select(s => s.State));
        var ignored = Assert.Single(report.Ignored);
        Assert.Equal("keyed", ignored.State);
        Assert.StartsWith("the 'default' response declares no status", ignored.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(import.Mappings, m => m.Contains("\"requiredScenarioState\":\"keyed\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// The rest of the class never reaches the generator: every key that is not a code, a range or <c>default</c> is
    /// refused at parse with a typed 422, so no example can be served under a status nobody declared.
    /// </summary>
    [Theory]
    [InlineData("600")]
    [InlineData("099")]
    [InlineData("6XX")]
    [InlineData("4xx")]
    [InlineData("40X")]
    [InlineData("4X0")]
    [InlineData("+20")]
    [InlineData("4020")]
    [InlineData("٤٠٢")]
    [InlineData("Default")]
    public void A_response_key_that_is_not_a_code_a_range_or_default_refuses_the_import(string key)
    {
        var refused = Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.GenerateWithRelations(KeyedSpec(key), scenarios: true));
        Assert.Equal(OpenApiImportError.Invalid, refused.Error);
        Assert.Contains("Responses key must be 'default', an HTTP status code", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_bound_by_a_range_names_the_range_when_another_response_declares_it_too()
    {
        const string spec = """
            openapi: 3.0.3
            info: { title: Dup, version: "1" }
            paths:
              /pay:
                post:
                  responses:
                    "4XX":
                      description: range
                      content: { application/json: { examples: { declined: { value: { s: R } } } } }
                    "500":
                      description: exact
                      content: { application/json: { examples: { declined: { value: { s: E } } } } }
            """;
        var report = OpenApiStubGenerator.GenerateWithRelations(spec, scenarios: true).Scenarios!;

        Assert.Equal(("4XX", 400), (Assert.Single(report.States).Response, report.States[0].Status));
        Assert.StartsWith("the 4XX response already binds this state", Assert.Single(report.Ignored).Reason, StringComparison.Ordinal);
    }

    private static JsonElement DefaultStub(string responses) => JsonDocument.Parse(Assert.Single(OpenApiStubGenerator.Generate($$"""
        openapi: 3.0.3
        info: { title: Default, version: "1" }
        paths:
          /pay:
            post:
              responses:
        {{responses}}
        """))).RootElement.GetProperty("response");

    /// <summary>The same class on the default stub (pre-existing since G19c): a range is read as the status it declares.</summary>
    [Fact]
    public void The_default_stub_of_an_operation_whose_only_response_is_a_range_answers_that_range()
    {
        var response = DefaultStub("""
                    "4XX": { description: err, content: { application/json: { example: { e: 1 } } } }
            """);
        Assert.Equal(400, response.GetProperty("status").GetInt32());
    }

    [Fact]
    public void A_2XX_range_outranks_an_error_for_the_default_stub_and_an_exact_200_outranks_the_range()
    {
        var range = DefaultStub("""
                    "404": { description: nf, content: { application/json: { example: { e: NF } } } }
                    "2XX": { description: ok, content: { application/json: { example: { e: OK } } } }
            """);
        Assert.Equal(200, range.GetProperty("status").GetInt32());
        Assert.Contains("OK", range.GetProperty("body").GetString(), StringComparison.Ordinal);

        var exact = DefaultStub("""
                    "2XX": { description: ok, content: { application/json: { example: { e: RANGE } } } }
                    "200": { description: ok, content: { application/json: { example: { e: EXACT } } } }
            """);
        Assert.Contains("EXACT", exact.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_scenario_name_without_scenarios_is_refused_rather_than_dropped()
    {
        var refused = Assert.Throws<OpenApiImportException>(() => OpenApiStubGenerator.GenerateWithRelations(EdgeSpec, scenarioName: "edges"));
        Assert.Equal(OpenApiImportError.Invalid, refused.Error);
        Assert.StartsWith("?scenario=<name> names the scenario of ?scenarios=true", refused.Message, StringComparison.Ordinal);
    }
}
