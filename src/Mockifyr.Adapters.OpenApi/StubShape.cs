using System.Text.Json;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;

namespace Mockifyr.Adapters.OpenApi;

/// <summary>
/// What a conformance check needs to know about a stub, read from its mapping JSON (#287).
/// </summary>
/// <remarks>
/// The <em>declared</em> shape, deliberately: the check is about what the stub says it does, which is
/// what a reader compares against a specification. Reading the compiled matchers instead would mean
/// this adapter depended on the engine, and would answer questions about the implementation rather
/// than the document.
/// </remarks>
/// <param name="Method">The method the stub matches, uppercased.</param>
/// <param name="PathPattern">The URL or path template the stub matches.</param>
/// <param name="Status">The status the stub answers with.</param>
/// <param name="Body">The response body as written, or null when the stub answers with none.</param>
internal sealed record StubShape(string Method, string PathPattern, int Status, string? Body)
{
    /// <summary>
    /// Reads the shape, or null when the stub does not describe an HTTP operation at all — a gRPC or
    /// message stub sharing the tenant, or one matching by something a specification cannot express.
    /// </summary>
    public static StubShape? Read(StubUnderTest stub)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stub.MappingJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("request", out var request))
            {
                return null;
            }

            var method = String(request, "method");
            // The engine's own precedence (MappingJsonReader): url wins, then urlPath, then
            // urlPathTemplate. Reading them in a different order would describe an operation the stub is
            // not actually serving whenever a mapping carries more than one — a report that is confidently
            // about the wrong endpoint.
            var path = String(request, "url") ?? String(request, "urlPath") ?? String(request, "urlPathTemplate");
            if (method is null || path is null)
            {
                // urlPattern/urlPathPattern are regular expressions: a specification path cannot be
                // compared against one without guessing, and a wrong guess is a false finding — the
                // thing that makes people stop reading reports.
                return null;
            }

            var status = 200;
            string? body = null;
            if (root.TryGetProperty("response", out var response))
            {
                status = response.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetInt32()
                    : 200;
                body = String(response, "body");
            }

            return new StubShape(method.ToUpperInvariant(), path, status, body);
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Renders an OpenAPI schema as JSON Schema text, so the same validator that backs
/// <c>matchesJsonSchema</c> can judge a response body against it (#287).
/// </summary>
/// <remarks>
/// OpenAPI 3.0's schema dialect is JSON Schema Draft 4 with edits (<c>nullable</c>, boolean
/// <c>exclusiveMinimum</c>), so this is a close reading rather than an exact one. It is faithful for
/// what a conformance report is asked about in practice — types, required properties, enums, nested
/// objects and arrays — and the limitation is recorded rather than hidden.
/// </remarks>
internal static class SchemaJson
{
    public static string Write(OpenApiSchema schema)
    {
        using var writer = new StringWriter();
        var json = new OpenApiJsonWriter(writer);
        schema.SerializeAsV3WithoutReference(json);
        json.Flush();
        return writer.ToString();
    }
}
