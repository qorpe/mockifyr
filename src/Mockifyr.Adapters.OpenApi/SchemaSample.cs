using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;

namespace Mockifyr.Adapters.OpenApi;

/// <summary>
/// Synthesizes a JSON sample from an example-less schema (G19c): deterministic primitives with
/// Faker-backed template expressions where a string format maps onto an existing helper
/// (<c>uuid</c>, <c>email</c>, <c>uri</c>). Declared examples always win over synthesis. Recursion
/// is depth-guarded (ADR 0011 addendum): a cyclic or absurdly nested schema is a typed refusal,
/// never a hang.
/// </summary>
public static class SchemaSample
{
    // Helper expressions carry apostrophes ({{random 'Internet.url'}}); the default encoder would
    // escape them to \u0027 and break the helper arguments.
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The schema-recursion bound; past it the import refuses with <see cref="OpenApiImportError.TooDeep"/>.</summary>
    public const int MaxDepth = 32;

    /// <summary>Serializes a declared OpenAPI example to JSON text.</summary>
    public static string WriteExample(IOpenApiAny example)
    {
        var builder = new StringBuilder();
        var writer = new OpenApiJsonWriter(new StringWriter(builder, CultureInfo.InvariantCulture));
        example.Write(writer, Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0);
        return builder.ToString();
    }

    /// <summary>Writes a sample JSON value for the schema (or <c>null</c> JSON when it is absent).</summary>
    public static string Write(OpenApiSchema? schema) => Write(schema, depth: 0);

    private static string Write(OpenApiSchema? schema, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new OpenApiImportException(
                OpenApiImportError.TooDeep,
                $"Schema nesting exceeds the depth guard ({MaxDepth}) — cyclic or absurdly nested schemas cannot be imported.");
        }

        if (schema is null)
        {
            return "null";
        }

        if (schema.Example is { } example)
        {
            return WriteExample(example);
        }

        if (schema.Enum is { Count: > 0 } choices)
        {
            return WriteExample(choices[0]);
        }

        // allOf composes objects; the sample merges every branch's properties in order.
        if (schema.AllOf is { Count: > 0 } allOf)
        {
            var merged = new List<KeyValuePair<string, OpenApiSchema>>();
            foreach (var part in allOf)
            {
                if (part.Properties is { Count: > 0 } parts)
                {
                    merged.AddRange(parts);
                }
            }

            return WriteObject(merged, depth);
        }

        var type = schema.Type?.ToLowerInvariant();
        if (type == "object" || schema.Properties is { Count: > 0 })
        {
            return WriteObject(schema.Properties ?? new Dictionary<string, OpenApiSchema>(), depth);
        }

        if (type == "array")
        {
            return schema.Items is null ? "[]" : "[" + Write(schema.Items, depth + 1) + "]";
        }

        return type switch
        {
            "integer" => "1",
            "number" => "1.5",
            "boolean" => "true",
            _ => JsonSerializer.Serialize(StringSample(schema.Format), JsonOptions),
        };
    }

    private static string WriteObject(IEnumerable<KeyValuePair<string, OpenApiSchema>> properties, int depth)
    {
        var parts = properties
            .Select(pair => $"{JsonSerializer.Serialize(pair.Key, JsonOptions)}:{Write(pair.Value, depth + 1)}");
        return "{" + string.Join(",", parts) + "}";
    }

    /// <summary>
    /// A string sample: formats with an existing Faker/random helper synthesize live values (the
    /// stub opts into templating); date-likes stay deterministic ISO stamps; anything else is the
    /// literal <c>"string"</c>.
    /// </summary>
    private static string StringSample(string? format) => format?.ToLowerInvariant() switch
    {
        "uuid" => "{{randomValue type='UUID'}}",
        "email" => "{{random 'Internet.emailAddress'}}",
        "uri" or "url" => "{{random 'Internet.url'}}",
        "date-time" => "2026-01-01T12:00:00Z",
        "date" => "2026-01-01",
        _ => "string",
    };
}
