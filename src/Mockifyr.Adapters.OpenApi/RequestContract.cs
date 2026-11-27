using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;

namespace Mockifyr.Adapters.OpenApi;

/// <summary>
/// Writes an operation's request contract into a generated mapping as <c>request.validate</c> (#448,
/// ADR 0018). The contract travels with the stub — schemas inlined, nothing pointing back at the
/// document — so a stub validates identically after an export, a restore or a reload on any backend.
/// </summary>
public static class RequestContract
{
    /// <summary>
    /// Headers OpenAPI 3 says a header parameter may not describe (§4.8.12.1: <c>Accept</c>,
    /// <c>Content-Type</c> and <c>Authorization</c> are defined elsewhere), so they are not checked as parameters.
    /// </summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase) { "Accept", "Content-Type", "Authorization" };

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The mapping with the operation's contract added under <c>request.validate</c>.</summary>
    public static string Attach(string mappingJson, string path, OpenApiPathItem item, OpenApiOperation operation)
    {
        var mapping = JsonNode.Parse(mappingJson)!.AsObject();
        var request = mapping["request"]!.AsObject();
        request["validate"] = Build(path, item, operation);
        return mapping.ToJsonString(JsonOptions);
    }

    /// <summary>The <c>request.validate</c> object for one operation.</summary>
    public static JsonObject Build(string path, OpenApiPathItem item, OpenApiOperation operation)
    {
        var contract = new JsonObject { ["path"] = path };

        // An operation's parameter overrides the path item's of the same name and location (OpenAPI 3 §4.8.9).
        var merged = new Dictionary<(string, ParameterLocation?), OpenApiParameter>();
        foreach (var parameter in item.Parameters.Concat(operation.Parameters))
        {
            merged[(parameter.Name, parameter.In)] = parameter;
        }

        var parameters = new JsonArray();
        foreach (var parameter in merged.Values)
        {
            var location = parameter.In switch
            {
                ParameterLocation.Path => "path",
                ParameterLocation.Query => "query",
                ParameterLocation.Header when !ReservedHeaders.Contains(parameter.Name) => "header",
                _ => null,   // a cookie parameter, or a reserved header: not checked here (deferred-edges)
            };
            if (location is null)
            {
                continue;
            }

            var entry = new JsonObject
            {
                ["name"] = parameter.Name,
                ["in"] = location,
                ["required"] = parameter.Required || parameter.In == ParameterLocation.Path,
            };
            if (parameter.Schema is { } schema && Inline(schema) is { } inlined)
            {
                entry["schema"] = inlined;
            }

            // The serialization travels with the contract, so the validator never judges a value it read
            // in a different serialization than the document declares (verifier on #486).
            if (parameter.Style is { } style)
            {
                entry["style"] = style switch
                {
                    ParameterStyle.Form => "form",
                    ParameterStyle.Simple => "simple",
                    ParameterStyle.Matrix => "matrix",
                    ParameterStyle.Label => "label",
                    ParameterStyle.SpaceDelimited => "spaceDelimited",
                    ParameterStyle.PipeDelimited => "pipeDelimited",
                    ParameterStyle.DeepObject => "deepObject",
                    _ => "unknown",
                };
            }

            entry["explode"] = parameter.Explode;

            parameters.Add(entry);
        }

        contract["parameters"] = parameters;

        if (operation.RequestBody is { } body
            && body.Content.FirstOrDefault(c => IsJson(c.Key)).Value is { Schema: { } bodySchema }
            && Inline(bodySchema) is { } inlinedBody)
        {
            contract["body"] = new JsonObject
            {
                ["required"] = body.Required,
                ["schema"] = inlinedBody,
                ["mediaTypes"] = new JsonArray([.. body.Content.Keys.Select(type => (JsonNode?)JsonValue.Create(type))]),
            };
        }

        return contract;
    }

    private static bool IsJson(string mediaType)
    {
        var type = mediaType.Split(';')[0].Trim();
        return string.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The schema with every local reference written in place, in JSON Schema 2020-12 form; null for a
    /// schema that still refers to itself after inlining (a recursive model), which a standalone contract
    /// cannot carry — that schema is not checked, and the gap is in the deferred-edge register.
    /// </summary>
    private static JsonNode? Inline(OpenApiSchema schema)
    {
        using var writer = new StringWriter();
        var json = new OpenApiJsonWriter(writer, new OpenApiJsonWriterSettings { InlineLocalReferences = true });
        schema.SerializeAsV3WithoutReference(json);
        json.Flush();
        var text = OpenApiRequestValidator.ToJsonSchema(writer.ToString());
        return text.Contains("\"$ref\"", StringComparison.Ordinal) ? null : JsonNode.Parse(text);
    }
}
