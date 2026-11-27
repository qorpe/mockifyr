using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Mockifyr.Core;

namespace Mockifyr.Adapters.OpenApi;

/// <summary>
/// Judges a request against the contract an OpenAPI import wrote into its stub (#448, ADR 0018): path,
/// query and header parameters by presence and by their schema, and an <c>application/json</c> body by
/// its schema, with <c>format</c> asserted rather than annotated. The engine calls it only for a stub
/// that declares <c>request.validate</c>, after that stub has already won the match.
/// </summary>
public sealed partial class OpenApiRequestValidator : IRequestValidator
{
    /// <summary>How many compiled schemas are kept before the cache starts over (one per distinct schema text).</summary>
    public const int MaxCachedSchemas = 1024;

    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true,
    };

    private readonly ConcurrentDictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JsonNumber();

    /// <inheritdoc />
    public IReadOnlyList<RequestViolation> Validate(CanonicalRequest request, RequestValidationDirective contract)
    {
        var violations = new List<RequestViolation>();
        var pathValues = PathValues(contract.PathTemplate, request.Path);

        foreach (var parameter in contract.Parameters)
        {
            var values = parameter.In switch
            {
                ContractParameterLocation.Path => pathValues.TryGetValue(parameter.Name, out var segment) ? [segment] : [],
                ContractParameterLocation.Query => [.. request.Query[parameter.Name]],
                _ => HeaderValues(request, parameter.Name),
            };

            var location = parameter.In.ToString().ToLowerInvariant();
            if (values.Count == 0)
            {
                if (parameter.Required)
                {
                    violations.Add(new RequestViolation(
                        location, parameter.Name, "required", $"The {location} parameter '{parameter.Name}' is required and was not sent."));
                }

                continue;
            }

            if (parameter.Schema is { } schemaText)
            {
                violations.AddRange(Judge(location, parameter.Name, Coerce(schemaText, values), schemaText, nested: false));
            }
        }

        if (contract.Body is { } body)
        {
            violations.AddRange(CheckBody(request, body));
        }

        return violations;
    }

    private IEnumerable<RequestViolation> CheckBody(CanonicalRequest request, ContractBody body)
    {
        if (request.Body.Length == 0)
        {
            return body.Required
                ? [new RequestViolation("body", "", "required", "The operation requires a request body and none was sent.")]
                : [];
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(request.Body, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return [new RequestViolation("body", "", "json", "The operation takes a JSON body and what was sent is not JSON.")];
        }

        return Judge("body", "", parsed, body.Schema, nested: true);
    }

    private List<RequestViolation> Judge(string location, string name, JsonNode? instance, string schemaText, bool nested)
    {
        EvaluationResults result;
        try
        {
            result = Compile(schemaText).Evaluate(JsonSerializer.SerializeToElement(instance), Options);
        }
        catch (Exception failure) when (failure is JsonException or JsonSchemaException or RefResolutionException or ArgumentException or InvalidOperationException)
        {
            // A schema the evaluator cannot use is the stub's defect, not the caller's. Fail-closed: the
            // request is refused naming the contract, never served as though it had been checked.
            return [new RequestViolation(location, nested ? "" : name, "contract", "The stub's contract schema cannot be evaluated, so this request cannot be checked against it.")];
        }

        var violations = new List<RequestViolation>();
        if (result.IsValid)
        {
            return violations;
        }

        foreach (var detail in ContractConformance.FailureLeaves(result))
        {
            var (rule, message) = detail.Errors is { Count: > 0 } errors
                ? (errors.First().Key, errors.First().Value)
                : ("schema", "does not satisfy the schema");
            var pointer = nested ? detail.InstanceLocation.ToString() : name;
            var where = nested ? (pointer.Length == 0 ? "The request body" : $"The body at {pointer}") : $"The {location} parameter '{name}'";
            violations.Add(new RequestViolation(location, pointer, rule, $"{where}: {message}"));
        }

        return violations;
    }

    private JsonSchema Compile(string schemaText)
    {
        if (_schemas.TryGetValue(schemaText, out var cached))
        {
            return cached;
        }

        if (_schemas.Count >= MaxCachedSchemas)
        {
            _schemas.Clear();
        }

        return _schemas.GetOrAdd(schemaText, text => JsonSchema.FromText(text));
    }

    /// <summary>
    /// A parameter's raw text as the JSON value its schema describes: a query string carries <c>7</c>
    /// as text, and judging it against <c>type: integer</c> as the string <c>"7"</c> would refuse every
    /// valid request. Text that does not read as the declared type stays a string, so the schema reports
    /// the type mismatch rather than this guessing.
    /// </summary>
    internal static JsonNode? Coerce(string schemaText, IReadOnlyList<string> values)
    {
        var schema = JsonNode.Parse(schemaText) as JsonObject;
        var types = TypesOf(schema);
        if (types.Contains("array"))
        {
            var items = schema?["items"]?.ToJsonString() ?? "{}";
            return new JsonArray([.. values.Select(value => Coerce(items, [value]))]);
        }

        var text = values[0];
        if ((types.Contains("integer") || types.Contains("number")) && JsonNumber().IsMatch(text))
        {
            return JsonNode.Parse(text);
        }

        if (types.Contains("boolean") && text is "true" or "false")
        {
            return JsonValue.Create(text == "true");
        }

        return JsonValue.Create(text);
    }

    private static HashSet<string> TypesOf(JsonObject? schema) => schema?["type"] switch
    {
        JsonValue single when single.TryGetValue<string>(out var name) => [name],
        JsonArray many => [.. many.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var n) ? n : "")],
        _ => [],
    };

    private static List<string> HeaderValues(CanonicalRequest request, string name) =>
        [.. request.Headers.Where(group => string.Equals(group.Key, name, StringComparison.OrdinalIgnoreCase)).SelectMany(group => group)];

    /// <summary>The path template's <c>{name}</c> segments, read from the request's path; segments are percent-decoded.</summary>
    internal static Dictionary<string, string> PathValues(string template, string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = template.Split('/');
        var segments = path.Split('?')[0].Split('/');
        if (names.Length != segments.Length)
        {
            return values;
        }

        for (var i = 0; i < names.Length; i++)
        {
            if (names[i].Length > 2 && names[i][0] == '{' && names[i][^1] == '}')
            {
                values[names[i][1..^1]] = Uri.UnescapeDataString(segments[i]);
            }
        }

        return values;
    }

    /// <summary>
    /// The JSON Schema 2020-12 form of an OpenAPI 3.0 schema object, as the validator reads it. Two 3.0
    /// spellings mean something else to a 2020-12 evaluator, and each would make a valid request fail or
    /// an invalid one pass: <c>nullable: true</c> (ignored there, so <c>null</c> is refused) becomes a
    /// <c>null</c> member of <c>type</c>, and the boolean <c>exclusiveMinimum</c>/<c>exclusiveMaximum</c>
    /// of draft 4 becomes the numeric bound it modifies.
    /// </summary>
    public static string ToJsonSchema(string openApiSchemaJson)
    {
        var node = JsonNode.Parse(openApiSchemaJson);
        Translate(node, depth: 0);
        return node?.ToJsonString() ?? "{}";
    }

    private static void Translate(JsonNode? node, int depth)
    {
        if (depth > 64)
        {
            return;
        }

        switch (node)
        {
            case JsonObject schema:
                if (schema["nullable"] is JsonValue nullable && nullable.TryGetValue<bool>(out var isNullable))
                {
                    schema.Remove("nullable");
                    if (isNullable && schema["type"] is JsonValue type && type.TryGetValue<string>(out var name))
                    {
                        schema["type"] = new JsonArray(name, "null");
                        if (schema["enum"] is JsonArray choices)
                        {
                            choices.Add(null);
                        }
                    }
                }

                Bound(schema, "exclusiveMinimum", "minimum");
                Bound(schema, "exclusiveMaximum", "maximum");
                foreach (var (_, child) in schema.ToList())
                {
                    Translate(child, depth + 1);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Translate(child, depth + 1);
                }

                break;
        }
    }

    private static void Bound(JsonObject schema, string exclusive, string inclusive)
    {
        if (schema[exclusive] is not JsonValue flag || !flag.TryGetValue<bool>(out var on))
        {
            return;
        }

        schema.Remove(exclusive);
        if (on && schema[inclusive] is { } bound)
        {
            schema.Remove(inclusive);
            schema[exclusive] = bound;
        }
    }
}
