using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Mockifyr.Core;

namespace Mockifyr.Adapters.OpenApi;

/// <summary>
/// OpenAPI 3.0/3.1 (JSON or YAML) in, ordinary Mockifyr mapping JSON out (G19c, ADR 0011): paths
/// become <c>urlPathTemplate</c> matchers, operations become method matchers, declared examples
/// become response bodies, and example-less schemas synthesize samples via
/// <see cref="SchemaSample"/>. With <c>stateful</c> requested, resource-shaped path pairs
/// (<c>/things</c> + <c>/things/{id}</c>) emit a CRUD set wired to the G19b <c>state</c>
/// directive — spec in, working sandbox out. The generated stubs are plain mapping JSON, so the
/// import path feeds them through the same reader as any bundle: dialect compliance by
/// construction.
/// </summary>
public static partial class OpenApiStubGenerator
{
    /// <summary>The spec-size guard (ADR 0011 addendum): larger inputs are refused before parsing.</summary>
    public const int MaxSpecBytes = 5 * 1024 * 1024;

    // Template expressions carry apostrophes ({{random 'Internet.url'}}); the default encoder would
    // escape them to ' and break the helper arguments — same rationale as the mapping reader.
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex("""\$ref["']?\s*:\s*["']?(?<target>[^"'\s,}]+)""")]
    private static partial Regex RefPattern();

    /// <summary>Generates one mapping JSON per operation, or throws a typed <see cref="OpenApiImportException"/>.</summary>
    /// <summary>
    /// The size and external-<c>$ref</c> guards every path that accepts a specification must apply
    /// (G19c). Shared with conformance verification (#287): a document handed to a verify run comes
    /// from exactly the same untrusted place as one handed to an import.
    /// </summary>
    public static void GuardSpec(string specText)
    {
        if (Encoding.UTF8.GetByteCount(specText) > MaxSpecBytes)
        {
            throw new OpenApiImportException(
                OpenApiImportError.TooLarge, $"The spec exceeds the {MaxSpecBytes}-byte guard.");
        }

        // SSRF stays impossible by construction: external $refs are refused before parsing and the
        // readers only ever resolve local (#/…) references — nothing is fetched, ever.
        foreach (Match match in RefPattern().Matches(specText))
        {
            var target = match.Groups["target"].Value;
            if (!target.StartsWith('#'))
            {
                throw new OpenApiImportException(
                    OpenApiImportError.ExternalRef,
                    $"External $ref '{target}' is not imported — inline it or remove it (remote references are never fetched).",
                    pointer: target);
            }
        }
    }

    /// <summary>
    /// What an import produces: the mappings, and the relations the path shapes declared (ADR 0015).
    /// </summary>
    /// <remarks>
    /// The relations are read from the specification rather than asked for, because
    /// <c>/customers/{customerId}/orders</c> already <em>is</em> the sentence "orders belong to
    /// customers, keyed by customerId". Making the user restate it would be asking them to repeat
    /// something they have already written down.
    /// </remarks>
    public sealed record OpenApiImport(IReadOnlyList<string> Mappings, IReadOnlyList<ResourceSchema> Relations);

    /// <summary>The mappings alone, for callers that do not wire sandbox state.</summary>
    public static IReadOnlyList<string> Generate(string specText, bool stateful = false) =>
        GenerateWithRelations(specText, stateful).Mappings;

    public static OpenApiImport GenerateWithRelations(string specText, bool stateful = false)
    {
        GuardSpec(specText);

        var reader = new OpenApiStringReader(new OpenApiReaderSettings
        {
            ReferenceResolution = ReferenceResolutionSetting.ResolveLocalReferences,
        });
        OpenApiDocument document;
        try
        {
            document = reader.Read(specText, out var diagnostic);
            if (diagnostic.Errors.Count > 0)
            {
                throw new OpenApiImportException(
                    OpenApiImportError.Invalid,
                    "The document does not parse as OpenAPI 3.x: " + diagnostic.Errors[0]);
            }
        }
        catch (Exception exception) when (exception is not OpenApiImportException)
        {
            // The reader throws (rather than reporting a diagnostic) for e.g. a missing/unsupported
            // openapi version or hopeless YAML — one typed refusal either way.
            throw new OpenApiImportException(
                OpenApiImportError.Invalid, "The document does not parse as OpenAPI 3.x: " + exception.Message);
        }

        if (document.Paths is not { Count: > 0 })
        {
            throw new OpenApiImportException(OpenApiImportError.Empty, "The document declares no paths to import.");
        }

        var (stateWired, relations) = stateful
            ? DetectResourcePairs(document)
            : ([], []);
        var mappings = new List<string>();
        foreach (var (path, item) in document.Paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var (operationType, operation) in item.Operations)
            {
                var method = operationType.ToString().ToUpperInvariant();
                mappings.Add(stateWired.TryGetValue((path, method), out var wired)
                    ? wired
                    : ExampleMapping(path, method, operation));
            }
        }

        if (mappings.Count == 0)
        {
            throw new OpenApiImportException(OpenApiImportError.Empty, "The document declares no operations to import.");
        }

        return new OpenApiImport(mappings, relations);
    }

    private static string ExampleMapping(string path, string method, OpenApiOperation operation)
    {
        var (status, media, contentType) = PickResponse(operation);
        var body = media is null ? null
            : media.Example is { } example ? SchemaSample.WriteExample(example)
            : media.Examples is { Count: > 0 } examples && examples.First().Value.Value is { } first ? SchemaSample.WriteExample(first)
            : media.Schema is { } schema ? SchemaSample.Write(schema)
            : null;

        var response = new Dictionary<string, object> { ["status"] = status };
        if (body is not null)
        {
            response["body"] = body;
            if (contentType is not null)
            {
                response["headers"] = new Dictionary<string, object> { ["Content-Type"] = contentType };
            }

            if (body.Contains("{{", StringComparison.Ordinal))
            {
                response["transformers"] = new object[] { "response-template" };
            }
        }

        return Serialize(NameFor(operation, method, path), path, method, response);
    }

    /// <summary>Lowest 2xx first, then <c>default</c> (as 200), then the first declared response.</summary>
    private static (int Status, OpenApiMediaType? Media, string? ContentType) PickResponse(OpenApiOperation operation)
    {
        if (operation.Responses is not { Count: > 0 } responses)
        {
            return (200, null, null);
        }

        var chosen = responses
            .Select(pair => (Key: pair.Key, Parsed: int.TryParse(pair.Key, out var code) ? code : (int?)null, pair.Value))
            .OrderBy(entry => entry.Parsed is >= 200 and < 300 ? 0 : entry.Key == "default" ? 1 : 2)
            .ThenBy(entry => entry.Parsed ?? int.MaxValue)
            .First();

        var status = chosen.Parsed ?? 200;
        if (chosen.Value.Content is { Count: > 0 } content)
        {
            var media = content.TryGetValue("application/json", out var json)
                ? new KeyValuePair<string, OpenApiMediaType>("application/json", json)
                : content.First();
            return (status, media.Value, media.Key);
        }

        return (status, null, null);
    }

    /// <summary>
    /// Finds resource-shaped pairs — a collection path plus an item path that adds exactly one
    /// trailing template segment — and emits the G19b state-wired CRUD set for their operations.
    /// </summary>
    private static (Dictionary<(string Path, string Method), string> Wired, List<ResourceSchema> Relations)
        DetectResourcePairs(OpenApiDocument document)
    {
        var wired = new Dictionary<(string, string), string>();
        var relations = new Dictionary<string, ResourceSchema>(StringComparer.Ordinal);
        foreach (var (itemPath, _) in document.Paths)
        {
            var lastSlash = itemPath.LastIndexOf('/');
            var lastSegment = itemPath[(lastSlash + 1)..];
            if (lastSlash <= 0 || !lastSegment.StartsWith('{') || !lastSegment.EndsWith('}'))
            {
                continue;
            }

            var collectionPath = itemPath[..lastSlash];
            if (!document.Paths.ContainsKey(collectionPath))
            {
                continue;
            }

            var collection = CollectionName(collectionPath);
            var idTemplate = $"{{{{request.pathSegments.[{itemPath.Trim('/').Split('/').Length - 1}]}}}}";
            var owner = OwnerOf(collectionPath);
            if (owner is { } declared)
            {
                relations[collection] = new ResourceSchema(
                    collection,
                    [new ResourceRelation(declared.Collection, declared.Via)]);
            }

            wired[(collectionPath, "POST")] = Serialize($"create {collection}", collectionPath, "POST", new Dictionary<string, object>
            {
                ["status"] = 201,
                // Built from the request's own path, not from the specification's template text: for a
                // nested collection the template still contains "{customerId}", so composing the header
                // from it handed the client a Location it could not follow. The two forms agree for a
                // top-level collection, which is why this went unnoticed until a nested spec was served.
                ["headers"] = new Dictionary<string, object> { ["Location"] = "{{request.path}}/{{state.id}}" },
                ["body"] = "{{state.body}}",
                ["state"] = StateBlock("create", collection, owner: owner),
            });
            wired[(collectionPath, "GET")] = Serialize($"list {collection}", collectionPath, "GET", new Dictionary<string, object>
            {
                ["status"] = 200,
                ["headers"] = new Dictionary<string, object> { ["Content-Type"] = "application/json" },
                ["body"] = "{\"count\":{{state.count}},\"items\":{{state.list}} }",
                ["state"] = StateBlock("list", collection, owner: owner),
            });
            wired[(itemPath, "GET")] = StateItemMapping($"read {collection}", itemPath, "GET", "read", collection, idTemplate, 200, "{{state.body}}", owner);
            wired[(itemPath, "PUT")] = StateItemMapping($"update {collection}", itemPath, "PUT", "update", collection, idTemplate, 200, "{{state.body}}", owner);
            wired[(itemPath, "DELETE")] = StateItemMapping($"delete {collection}", itemPath, "DELETE", "delete", collection, idTemplate, 204, null, owner);
        }

        return (wired, [.. relations.Values.OrderBy(schema => schema.Collection, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The owning collection a nested path declares, or null when the path is top-level:
    /// <c>/customers/{customerId}/orders</c> yields <c>customers</c>, keyed by <c>customerId</c>, whose
    /// value is at path segment 1.
    /// </summary>
    /// <remarks>
    /// The collection keeps its own name — <c>orders</c>, not <c>customers-orders</c> — because a spec
    /// may expose the same resource both ways (<c>/orders</c> and <c>/customers/{id}/orders</c>) and in
    /// a real API those are one collection with one id space. What was missing was never the name; it
    /// was the relation.
    /// </remarks>
    private static (string Collection, string Via, string IdTemplate)? OwnerOf(string collectionPath)
    {
        var segments = collectionPath.Trim('/').Split('/');
        if (segments.Length < 3)
        {
            return null;
        }

        var ownerSegment = segments[^2];
        if (!ownerSegment.StartsWith('{') || !ownerSegment.EndsWith('}') || ownerSegment.Length <= 2)
        {
            return null;
        }

        var ownerCollection = CollectionName("/" + string.Join('/', segments[..^2]));
        return (ownerCollection, ownerSegment[1..^1], $"{{{{request.pathSegments.[{segments.Length - 2}]}}}}");
    }

    private static Dictionary<string, object> StateBlock(
        string operation,
        string collection,
        string? idTemplate = null,
        (string Collection, string Via, string IdTemplate)? owner = null)
    {
        var state = new Dictionary<string, object> { ["operation"] = operation, ["collection"] = collection };
        if (idTemplate is not null)
        {
            state["id"] = idTemplate;
        }

        if (owner is { } parent)
        {
            state["parent"] = new Dictionary<string, object>
            {
                ["collection"] = parent.Collection,
                ["id"] = parent.IdTemplate,
            };
        }

        return state;
    }

    private static string StateItemMapping(
        string name,
        string path,
        string method,
        string operation,
        string collection,
        string idTemplate,
        int status,
        string? body,
        (string Collection, string Via, string IdTemplate)? owner)
    {
        var response = new Dictionary<string, object>
        {
            ["status"] = status,
            ["state"] = StateBlock(operation, collection, idTemplate, owner),
        };
        if (body is not null)
        {
            response["headers"] = new Dictionary<string, object> { ["Content-Type"] = "application/json" };
            response["body"] = body;
        }

        return Serialize(name, path, method, response);
    }

    /// <summary>The G19b collection name for a collection path: its last segment, identifier-shaped.</summary>
    public static string CollectionName(string collectionPath)
    {
        var segment = collectionPath.Trim('/').Split('/')[^1];
        var builder = new StringBuilder(segment.Length + 1);
        foreach (var c in segment)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-');
        }

        var name = builder.ToString().Trim('-');
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            name = "c-" + name;
        }

        return name.Length > 64 ? name[..64] : name;
    }

    private static string Serialize(string name, string path, string method, Dictionary<string, object> response)
    {
        var request = new Dictionary<string, object>
        {
            ["method"] = method,
            [path.Contains('{', StringComparison.Ordinal) ? "urlPathTemplate" : "urlPath"] = path,
        };

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name,
            ["request"] = request,
            ["response"] = response,
        }, JsonOptions);
    }

    private static string NameFor(OpenApiOperation operation, string method, string path) =>
        string.IsNullOrWhiteSpace(operation.OperationId) ? $"{method} {path}" : operation.OperationId;
}
