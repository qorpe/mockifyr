using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using Mockifyr.Core;

namespace Mockifyr.Adapters.OpenApi;

/// <summary>
/// Judges a request against the contract an OpenAPI import wrote into its stub (#448, ADR 0018): path,
/// query and header parameters by presence and by their schema, and a JSON body by the schema declared
/// for the media type it was sent as, with <c>format</c> asserted rather than annotated. The engine calls
/// it only for a stub that declares <c>request.validate</c>, after that stub has already won the match.
/// </summary>
/// <remarks>
/// Two rules are fail-closed in the direction each one protects:
/// <list type="bullet">
/// <item>A value is judged by its schema only when the validator implements both its serialization and its
/// shape: a known-good list (<see cref="Judgeable"/>). Anything else (another style, an object, a schema
/// whose type comes from <c>anyOf</c>/<c>oneOf</c>/<c>allOf</c>, a path array of strings, a body in a media
/// type the operation declares with no schema for it) is checked for presence only and is a row in the
/// deferred-edge register, because refusing a request the contract allows is the failure a sandbox exists to avoid.</item>
/// <item>A violation's message is built from the contract alone (where, which parameter or pointer, which
/// rule), never from the evaluator's text or the instance, so a value the journal masks cannot reach the
/// answer or the journal through it.</item>
/// </list>
/// </remarks>
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

    /// <summary>The longest body pointer a violation names; a longer one (instance keys are the caller's) is cut.</summary>
    public const int MaxPointerLength = 256;

    [GeneratedRegex(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JsonNumber();

    [GeneratedRegex(@"^[A-Za-z$][A-Za-z0-9]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Keyword();

    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal) { "string", "integer", "number", "boolean" };

    private static string Bounded(string pointer) => pointer.Length > MaxPointerLength ? pointer[..MaxPointerLength] + "…" : pointer;

    /// <summary>What a judgeable parameter's schema describes: one primitive value, or an array of them.</summary>
    internal enum Shape
    {
        /// <summary>One string, integer, number or boolean.</summary>
        Primitive,

        /// <summary>An array of those.</summary>
        Array,
    }

    /// <summary>
    /// The shape of a parameter whose value this validator reads exactly as the contract serializes it, or null
    /// when it does not (known-good list; null means presence only). Judged: a schema whose own <c>type</c> names
    /// only primitives (plus <c>null</c>), or <c>array</c> of items like that; in the location's default
    /// serialization (query <c>form</c>, path and header <c>simple</c>), with <c>explode</c> either way for
    /// <c>form</c>. Everything else is not judged: a path array of strings, objects, <c>deepObject</c>,
    /// <c>spaceDelimited</c>, <c>pipeDelimited</c>, <c>matrix</c>, <c>label</c>, and a type that only composition
    /// keywords decide.
    /// </summary>
    internal static Shape? Judgeable(ContractParameter parameter, string schemaText)
    {
        var style = parameter.Style ?? (parameter.In == ContractParameterLocation.Query ? "form" : "simple");
        var known = parameter.In switch
        {
            ContractParameterLocation.Query => style == "form",
            _ => style == "simple",
        };
        if (!known || JsonNode.Parse(schemaText) is not JsonObject schema)
        {
            return null;
        }

        var types = Named(schema);
        if (types is null)
        {
            return null;
        }

        if (types.SetEquals(["array"]))
        {
            // A path reaches the engine already percent-decoded, so an encoded comma inside a string item cannot
            // be told from the separator: a path array of strings is not judged. Integer, number and boolean
            // items cannot hold a comma, so their split is exact.
            return schema["items"] is JsonObject items && Named(items) is { } itemTypes && itemTypes.IsSubsetOf(Primitives)
                && !(parameter.In == ContractParameterLocation.Path && itemTypes.Contains("string"))
                ? Shape.Array
                : null;
        }

        return types.IsSubsetOf(Primitives) ? Shape.Primitive : null;
    }

    /// <summary>A schema's own non-null <c>type</c> names, or null when it names none (its type is decided elsewhere).</summary>
    private static HashSet<string>? Named(JsonObject schema)
    {
        var types = TypesOf(schema);
        types.Remove("null");
        return types.Count == 0 ? null : types;
    }

    /// <summary>
    /// The raw values as the schema reads them: an array in <c>simple</c> style, or in <c>form</c> with
    /// <c>explode: false</c>, is one comma-separated value, and an empty (or, in a header, blank) value is the
    /// empty array, as OpenAPI serializes one; an exploded <c>form</c> array is one value per occurrence. A
    /// primitive is its first value. A non-exploded query array is split on the commas of the RAW query
    /// (<paramref name="rawQuery"/>) and each item decoded after, so a percent-encoded comma stays inside its item.
    /// </summary>
    internal static IReadOnlyList<string> Serialized(
        ContractParameter parameter, Shape shape, IReadOnlyList<string> values, IReadOnlyList<string>? rawQuery = null)
    {
        if (shape != Shape.Array)
        {
            return values;
        }

        if (parameter.In == ContractParameterLocation.Query)
        {
            if (parameter.Explode ?? true)
            {
                return values;
            }

            return [.. (rawQuery ?? values).SelectMany(value => value.Length == 0 ? [] : value.Split(',')).Select(Decode)];
        }

        var header = parameter.In == ContractParameterLocation.Header;
        return [.. values.SelectMany(value => (header ? value.Trim() : value) is { Length: > 0 } text ? text.Split(',') : [])
            .Select(item => header ? item.Trim() : item)];
    }

    /// <summary>A query item decoded as the canonical request's own query parser decodes a value.</summary>
    private static string Decode(string raw) => Uri.UnescapeDataString(raw);

    /// <summary>Every raw (undecoded) value of the query parameter <paramref name="name"/>, in order, from the request's URL.</summary>
    internal static List<string> RawQueryValues(string url, string name)
    {
        var start = url.IndexOf('?', StringComparison.Ordinal);
        var raw = new List<string>();
        if (start < 0)
        {
            return raw;
        }

        foreach (var pair in url[(start + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(eq >= 0 ? pair[..eq] : pair);
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                raw.Add(eq >= 0 ? pair[(eq + 1)..] : "");
            }
        }

        return raw;
    }

    /// <summary>
    /// Whether a query parameter's presence can be read when its name is not itself a key. A <c>deepObject</c>
    /// arrives as <c>name[prop]=…</c>, so it is present when such a key is; an object in exploded <c>form</c>
    /// arrives as its properties' own keys, with no trace of its name, so its presence cannot be read and is not
    /// judged (refusing it would refuse every valid request). Anything else: its name is its key.
    /// </summary>
    private static bool PresenceIsReadable(ContractParameter parameter, CanonicalRequest request, ref List<string> values)
    {
        var style = parameter.Style ?? "form";
        if (style == "deepObject")
        {
            var prefix = parameter.Name + "[";
            if (request.Query.Any(group => group.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                values = [""];
            }

            return true;
        }

        var objectTyped = parameter.Schema is { } text && JsonNode.Parse(text) is JsonObject schema
            && (TypesOf(schema).Contains("object") || (schema["properties"] is not null && TypesOf(schema).Count == 0));
        return !(style == "form" && (parameter.Explode ?? true) && objectTyped);
    }

    private static bool IsJson(string mediaType)
        => string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a declared media type range (<c>*/*</c>, <c>text/*</c>, or an exact type) covers what was sent.</summary>
    private static bool Covers(string declared, string sent)
    {
        var range = declared.Split(';')[0].Trim();
        if (range == "*/*" || string.Equals(range, sent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return range.EndsWith("/*", StringComparison.Ordinal)
            && sent.StartsWith(range[..^1], StringComparison.OrdinalIgnoreCase);
    }

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
            if (values.Count == 0 && parameter.In == ContractParameterLocation.Query && !PresenceIsReadable(parameter, request, ref values))
            {
                continue;
            }

            if (values.Count == 0)
            {
                if (parameter.Required)
                {
                    violations.Add(new RequestViolation(
                        location, parameter.Name, "required", $"The {location} parameter '{parameter.Name}' is required and was not sent."));
                }

                continue;
            }

            if (parameter.Schema is { } schemaText && Judgeable(parameter, schemaText) is { } shape)
            {
                var raw = parameter.In == ContractParameterLocation.Query ? RawQueryValues(request.Url, parameter.Name) : null;
                var read = Serialized(parameter, shape, values, raw is { Count: > 0 } ? raw : null);
                violations.AddRange(Judge(location, parameter.Name, Coerce(schemaText, read), schemaText, nested: false));
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

        // A body is judged only by the schema declared for the media type it was SENT as (verifier on #486): with
        // per-type schemas, the entry for that exact type, else one whose range covers it; a declared type with no
        // entry is presence only, and a type the operation does not declare is refused, so no rule passes a body
        // vacuously. A body sent with no type is judged as JSON, by the contract's single schema or its first.
        // A hand-written contract with one schema and no per-type entries judges every JSON type by it.
        var sent = HeaderValues(request, "Content-Type").FirstOrDefault()?.Split(';')[0].Trim();
        string? schemaText;
        if (string.IsNullOrEmpty(sent))
        {
            schemaText = body.Schema ?? body.Schemas?.FirstOrDefault().Value;
        }
        else if (body.Schemas is { Count: > 0 } schemas)
        {
            schemaText = schemas
                .FirstOrDefault(entry => string.Equals(entry.Key.Split(';')[0].Trim(), sent, StringComparison.OrdinalIgnoreCase)).Value
                ?? schemas.FirstOrDefault(entry => Covers(entry.Key, sent)).Value;
        }
        else
        {
            schemaText = IsJson(sent) ? body.Schema : null;
        }

        if (schemaText is null)
        {
            return body.MediaTypes is { } declared && !declared.Any(type => Covers(type, sent!))
                ? [new RequestViolation("body", "", "mediaType", "The request body's media type is not one the operation declares.")]
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

        return Judge("body", "", parsed, schemaText, nested: true);
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
            // The rule is the schema keyword's name, a closed vocabulary; the evaluator's own message is never
            // used, because for the numeric keywords it quotes the request's value (verifier on #486).
            var rule = detail.Errors is { Count: > 0 } errors && Keyword().IsMatch(errors.First().Key) ? errors.First().Key : "schema";
            var pointer = nested ? Bounded(detail.InstanceLocation.ToString()) : name;
            var where = nested ? (pointer.Length == 0 ? "The request body" : $"The body at {pointer}") : $"The {location} parameter '{name}'";
            violations.Add(new RequestViolation(location, pointer, rule, $"{where} does not satisfy the contract's '{rule}'."));
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

                // Only into what is itself a schema (a known-good list): a literal under example, default,
                // enum or const is data, and rewriting its "nullable" or "exclusiveMinimum" key would change it.
                foreach (var (key, child) in schema.ToList())
                {
                    if (SchemaValued.Contains(key))
                    {
                        Translate(child, depth + 1);
                    }
                    else if (SchemaArrays.Contains(key) && child is JsonArray members)
                    {
                        foreach (var member in members)
                        {
                            Translate(member, depth + 1);
                        }
                    }
                    else if (SchemaMaps.Contains(key) && child is JsonObject map)
                    {
                        foreach (var (_, member) in map.ToList())
                        {
                            Translate(member, depth + 1);
                        }
                    }
                }

                break;
        }
    }

    private static readonly HashSet<string> SchemaValued = new(StringComparer.Ordinal)
    {
        "items", "additionalProperties", "not", "if", "then", "else", "contains", "propertyNames",
        "unevaluatedItems", "unevaluatedProperties", "additionalItems",
    };

    private static readonly HashSet<string> SchemaArrays = new(StringComparer.Ordinal) { "allOf", "anyOf", "oneOf", "prefixItems" };

    private static readonly HashSet<string> SchemaMaps = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "$defs", "definitions", "dependentSchemas",
    };

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
