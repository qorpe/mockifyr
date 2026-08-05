using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

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

    public static IReadOnlyList<string> Generate(string specText, bool stateful = false)
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

        var stateWired = stateful ? DetectResourcePairs(document) : [];
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

        return mappings;
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
    private static Dictionary<(string Path, string Method), string> DetectResourcePairs(OpenApiDocument document)
    {
        var wired = new Dictionary<(string, string), string>();
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

            wired[(collectionPath, "POST")] = Serialize($"create {collection}", collectionPath, "POST", new Dictionary<string, object>
            {
                ["status"] = 201,
                ["headers"] = new Dictionary<string, object> { ["Location"] = collectionPath + "/{{state.id}}" },
                ["body"] = "{{state.body}}",
                ["state"] = new Dictionary<string, object> { ["operation"] = "create", ["collection"] = collection },
            });
            wired[(collectionPath, "GET")] = Serialize($"list {collection}", collectionPath, "GET", new Dictionary<string, object>
            {
                ["status"] = 200,
                ["headers"] = new Dictionary<string, object> { ["Content-Type"] = "application/json" },
                ["body"] = "{\"count\":{{state.count}},\"items\":{{state.list}} }",
                ["state"] = new Dictionary<string, object> { ["operation"] = "list", ["collection"] = collection },
            });
            wired[(itemPath, "GET")] = StateItemMapping($"read {collection}", itemPath, "GET", "read", collection, idTemplate, 200, "{{state.body}}");
            wired[(itemPath, "PUT")] = StateItemMapping($"update {collection}", itemPath, "PUT", "update", collection, idTemplate, 200, "{{state.body}}");
            wired[(itemPath, "DELETE")] = StateItemMapping($"delete {collection}", itemPath, "DELETE", "delete", collection, idTemplate, 204, null);
        }

        return wired;
    }

    private static string StateItemMapping(
        string name, string path, string method, string operation, string collection, string idTemplate, int status, string? body)
    {
        var response = new Dictionary<string, object>
        {
            ["status"] = status,
            ["state"] = new Dictionary<string, object>
            {
                ["operation"] = operation,
                ["collection"] = collection,
                ["id"] = idTemplate,
            },
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
