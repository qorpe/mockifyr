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
        Assert.Equal("/api/orders/{{state.id}}",
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
}
