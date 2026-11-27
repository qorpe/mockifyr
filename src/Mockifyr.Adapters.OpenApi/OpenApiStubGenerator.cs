using System.Globalization;
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
    /// <param name="Mappings">The generated mapping JSON, one document per stub.</param>
    /// <param name="Relations">The relations the path shapes declared.</param>
    /// <param name="Scenarios">What the named examples became when scenarios were asked for (#449); null otherwise.</param>
    public sealed record OpenApiImport(
        IReadOnlyList<string> Mappings, IReadOnlyList<ResourceSchema> Relations, OpenApiScenarioReport? Scenarios = null);

    /// <summary>The longest scenario name an import accepts (#449).</summary>
    public const int MaxScenarioNameLength = 128;

    /// <summary>The state every scenario starts in; an example of that name cannot be a selectable state.</summary>
    public const string InitialScenarioState = "Started";

    /// <summary>The mappings alone, for callers that do not wire sandbox state.</summary>
    public static IReadOnlyList<string> Generate(string specText, bool stateful = false) =>
        GenerateWithRelations(specText, stateful).Mappings;

    /// <summary>
    /// Generates the mappings and the relations; with <paramref name="scenarios"/>, every named example of an
    /// operation's responses also becomes a stub bound to one scenario (#449).
    /// </summary>
    /// <param name="specText">The OpenAPI document, JSON or YAML.</param>
    /// <param name="stateful">Wire resource-shaped path pairs to sandbox state (G19b).</param>
    /// <param name="scenarios">Make named examples selectable scenario states (#449).</param>
    /// <param name="scenarioName">
    /// The one scenario every example stub of this import binds to; the document's <c>info.title</c> when null.
    /// Refused without <paramref name="scenarios"/>, rather than dropped.
    /// </param>
    /// <remarks>
    /// One scenario per import, not one per operation: a consumer selects "insufficient-funds" for a product and
    /// every operation that declares an example of that name answers with it, while an operation that declares
    /// none keeps its default answer. The default stub is the one an import without scenarios generates, at the
    /// default priority (5) and bound to no scenario, so it answers in every state; each example stub requires its
    /// state and outranks it at priority 1.
    /// </remarks>
    public static OpenApiImport GenerateWithRelations(
        string specText, bool stateful = false, bool scenarios = false, string? scenarioName = null)
    {
        GuardSpec(specText);
        if (!scenarios && scenarioName is not null)
        {
            // A name for a scenario nobody asked for would otherwise be dropped without a word.
            throw new OpenApiImportException(OpenApiImportError.Invalid,
                "?scenario=<name> names the scenario of ?scenarios=true, which this import does not ask for: add ?scenarios=true or drop ?scenario.");
        }

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
        var scenario = scenarios ? ScenarioNameFor(scenarioName, document) : null;
        var bound = new List<OpenApiScenarioState>();
        var ignored = new List<OpenApiScenarioIgnored>();
        var mappings = new List<string>();
        foreach (var (path, item) in document.Paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var (operationType, operation) in item.Operations)
            {
                var method = operationType.ToString().ToUpperInvariant();
                if (stateWired.TryGetValue((path, method), out var wired))
                {
                    mappings.Add(wired);
                    if (scenario is not null)
                    {
                        // A state-wired stub answers from the sandbox's documents, not from an example: binding an
                        // example to it would replace the state it reads with a fixed body.
                        foreach (var (state, _, _, _) in NamedExamples(operation))
                        {
                            ignored.Add(new OpenApiScenarioIgnored(NameFor(operation, method, path), method, path, state,
                                "the operation is wired to sandbox state (stateful=true), which answers from the stored documents"));
                        }
                    }

                    continue;
                }

                mappings.Add(ExampleMapping(path, method, operation));
                if (scenario is not null)
                {
                    mappings.AddRange(ScenarioMappings(scenario, path, method, operation, bound, ignored));
                }
            }
        }

        if (mappings.Count == 0)
        {
            throw new OpenApiImportException(OpenApiImportError.Empty, "The document declares no operations to import.");
        }

        return new OpenApiImport(mappings, relations, scenario is null ? null : new OpenApiScenarioReport(scenario, bound, ignored));
    }

    /// <summary>
    /// The scenario name: the one asked for, else the document's title, trimmed. It is a path segment of
    /// <c>PUT /__admin/scenarios/{name}/state</c>, so a name that route could not select is refused here, where the fix
    /// is one parameter away: blank, longer than <see cref="MaxScenarioNameLength"/>, a control character, a <c>/</c>,
    /// or only dots.
    /// </summary>
    private static string ScenarioNameFor(string? asked, OpenApiDocument document)
    {
        var name = (asked ?? document.Info?.Title)?.Trim();
        var source = asked is null ? "The document's info.title" : "The scenario name";
        if (string.IsNullOrEmpty(name))
        {
            throw new OpenApiImportException(OpenApiImportError.Invalid,
                $"{source} is empty, and a scenario needs a name: pass ?scenario=<name>.");
        }

        if (name.Length > MaxScenarioNameLength)
        {
            throw new OpenApiImportException(OpenApiImportError.Invalid, string.Create(CultureInfo.InvariantCulture,
                $"{source} is {name.Length} characters long; a scenario name is at most {MaxScenarioNameLength}: pass ?scenario=<name>."));
        }

        if (name.Any(char.IsControl))
        {
            throw new OpenApiImportException(OpenApiImportError.Invalid,
                $"{source} carries a control character, and a scenario name must not: pass ?scenario=<name>.");
        }

        // The two kinds of name the admin route cannot round-trip: an escaped '/' stays escaped in a route value, so
        // the state would be set on another name and nothing would change, and '.' or '..' is a dot-segment a client
        // removes before it sends the request. Everything else a client escapes decodes back (proven per character).
        if (name.Contains('/', StringComparison.Ordinal))
        {
            throw new OpenApiImportException(OpenApiImportError.Invalid,
                $"{source} contains '/', which PUT /__admin/scenarios/{{name}}/state cannot select: pass ?scenario=<name>.");
        }

        if (name.All(c => c == '.'))
        {
            throw new OpenApiImportException(OpenApiImportError.Invalid,
                $"{source} is only dots, a path segment a client removes before it sends the request: pass ?scenario=<name>.");
        }

        return name;
    }

    /// <summary>
    /// Every named example of every response the operation declares, in the order <see cref="PickResponse"/> ranks
    /// the responses (2xx, then <c>default</c>, then the rest; exact codes before ranges in each band), with the response's key and the
    /// status <see cref="StatusOf"/> reads from it (<see langword="null"/> for <c>default</c>, which declares none).
    /// </summary>
    private static IEnumerable<(string State, string Response, int? Status, (OpenApiMediaType Media, string ContentType, Microsoft.OpenApi.Any.IOpenApiAny? Value) Example)> NamedExamples(OpenApiOperation operation)
    {
        // A plain null-and-count check rather than a property pattern: a pattern variable read inside an iterator is
        // what Stryker's mutants fail to compile on (CS0165), which removed this whole method from the mutation run.
        foreach (var entry in RankResponses(operation.Responses ?? new OpenApiResponses()))
        {
            var content = entry.Value.Content;
            if (content is null || content.Count == 0)
            {
                continue;
            }

            var media = content.TryGetValue("application/json", out var json)
                ? new KeyValuePair<string, OpenApiMediaType>("application/json", json)
                : content.First();
            foreach (var (name, example) in media.Value.Examples ?? new Dictionary<string, OpenApiExample>())
            {
                yield return (name, entry.Key, entry.Parsed, (media.Value, media.Key, example?.Value));
            }
        }
    }

    /// <summary>
    /// One stub per named example, bound to <paramref name="scenario"/> in the state of the example's name, with
    /// the status of the response that declares it (a range key <c>nXX</c> answers <c>n00</c>). A name declared by
    /// two responses of one operation binds the higher-ranked one; the other, an example named
    /// <see cref="InitialScenarioState"/>, a name with a control character, an example with no inline value
    /// (<c>externalValue</c> is never fetched) and an example under a key that declares no status (<c>default</c>, or
    /// anything that is not a status code or a range) are reported, not bound. Fail-closed: an example is served only
    /// with a status its document declares, never with a fallback.
    /// </summary>
    private static IEnumerable<string> ScenarioMappings(
        string scenario, string path, string method, OpenApiOperation operation,
        List<OpenApiScenarioState> bound, List<OpenApiScenarioIgnored> ignored)
    {
        var operationName = NameFor(operation, method, path);
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (state, key, status, (_, contentType, value)) in NamedExamples(operation))
        {
            string? reason = null;
            if (taken.TryGetValue(state, out var first))
            {
                reason = $"the {first} response already binds this state; a state selects one answer";
            }
            else if (string.Equals(state, InitialScenarioState, StringComparison.Ordinal))
            {
                reason = $"'{InitialScenarioState}' is the state every scenario starts in, so it cannot select an example";
            }
            else if (state.Length == 0 || state.Any(char.IsControl))
            {
                reason = "a state name must be non-empty and carry no control character";
            }
            else if (value is null)
            {
                reason = "the example has no inline value (externalValue is never fetched)";
            }
            else if (status is null)
            {
                // 'default' is the one key the reader accepts that declares no status; any other is refused at parse.
                reason = $"the '{key}' response declares no status, and an example is served only with one its document declares";
            }

            if (reason is not null)
            {
                ignored.Add(new OpenApiScenarioIgnored(operationName, method, path, Shown(state), reason));
                continue;
            }

            taken[state] = key;
            var code = status!.Value;
            bound.Add(new OpenApiScenarioState(operationName, method, path, state, key, code));
            var body = SchemaSample.WriteExample(value!);
            var response = new Dictionary<string, object>
            {
                ["status"] = code,
                ["body"] = body,
                ["headers"] = new Dictionary<string, object> { ["Content-Type"] = contentType },
            };
            if (body.Contains("{{", StringComparison.Ordinal))
            {
                response["transformers"] = new object[] { "response-template" };
            }

            yield return Serialize($"{operationName} [{state}]", path, method, response,
                new Dictionary<string, object>
                {
                    ["priority"] = 1,
                    ["scenarioName"] = scenario,
                    ["requiredScenarioState"] = state,
                });
        }
    }

    /// <summary>A state name as a report may show it: control characters replaced, at most 64 characters.</summary>
    private static string Shown(string state)
    {
        var clean = new string([.. state.Select(c => char.IsControl(c) ? '?' : c)]);
        return clean.Length > 64 ? clean[..64] + "…" : clean;
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

    /// <summary>
    /// The first response <see cref="RankResponses"/> ranks: 2xx, then <c>default</c> (as 200), then the rest, an exact
    /// code before any range in its band (a range <c>nXX</c> answers <c>n00</c>). Only <c>default</c> and a key that is
    /// not a response key at all answer the fallback 200: the default stub must answer something.
    /// </summary>
    private static (int Status, OpenApiMediaType? Media, string? ContentType) PickResponse(OpenApiOperation operation)
    {
        if (operation.Responses is not { Count: > 0 } responses)
        {
            return (200, null, null);
        }

        var chosen = RankResponses(responses).First();

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
    /// The responses in the order the generator prefers them, in three bands: 2xx, then <c>default</c>, then the rest.
    /// Within a band every exact code outranks every range, whatever their values — OpenAPI 3 gives an explicit code
    /// precedence over a range, and a document pairing <c>201</c> with <c>2XX</c> (or <c>404</c> with <c>4XX</c>) means
    /// the range as the catch-all — and only then the lower status first. So a range is chosen only when its band
    /// declares no exact code: <c>201</c> before <c>2XX</c>, <c>500</c> before <c>4XX</c>, and <c>2XX</c> before
    /// <c>default</c> and any error. <c>Parsed</c> is <see cref="StatusOf"/> the key.
    /// </summary>
    private static IEnumerable<(string Key, int? Parsed, OpenApiResponse Value)> RankResponses(OpenApiResponses responses) =>
        responses
            .Select(pair => (Key: pair.Key, Parsed: StatusOf(pair.Key), pair.Value))
            .OrderBy(entry => entry.Parsed is >= 200 and < 300 ? 0 : entry.Key == "default" ? 1 : 2)
            .ThenBy(entry => IsRange(entry.Key) ? 1 : 0)
            .ThenBy(entry => entry.Parsed ?? int.MaxValue);

    /// <summary>
    /// Whether the key is a status range (<c>1XX</c>-<c>5XX</c>) rather than an exact code or <c>default</c>: a key
    /// <see cref="StatusOf"/> reads is three characters, and its second is <c>X</c> only in a range.
    /// </summary>
    private static bool IsRange(string key) => StatusOf(key) is not null && key[1] == 'X';

    /// <summary>
    /// The status a response key declares, or <see langword="null"/> when it declares none. Only the two shapes
    /// OpenAPI 3 defines are read: three ASCII digits from 100 to 599, and a range <c>1XX</c>-<c>5XX</c>, which
    /// answers its lowest code. <c>default</c> declares none. The reader already refuses every other key at parse
    /// (<c>600</c>, <c>4xx</c>, <c>+20</c>, ...), so this is an allow-list behind an allow-list: a key nobody has thought
    /// of is never read as a status by accident (<c>int.TryParse</c> read nothing from <c>4XX</c>, which is how a
    /// range came to answer 200).
    /// </summary>
    private static int? StatusOf(string key)
    {
        if (key.Length != 3 || key[0] is < '1' or > '5')
        {
            return null;
        }

        var hundreds = (key[0] - '0') * 100;
        if (key[1] == 'X' && key[2] == 'X')
        {
            return hundreds;
        }

        return char.IsAsciiDigit(key[1]) && char.IsAsciiDigit(key[2])
            ? hundreds + ((key[1] - '0') * 10) + (key[2] - '0')
            : null;
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

    private static string Serialize(
        string name, string path, string method, Dictionary<string, object> response, Dictionary<string, object>? binding = null)
    {
        var request = new Dictionary<string, object>
        {
            ["method"] = method,
            [path.Contains('{', StringComparison.Ordinal) ? "urlPathTemplate" : "urlPath"] = path,
        };

        var mapping = new Dictionary<string, object> { ["name"] = name };
        foreach (var (key, value) in binding ?? [])
        {
            mapping[key] = value;
        }

        mapping["request"] = request;
        mapping["response"] = response;
        return JsonSerializer.Serialize(mapping, JsonOptions);
    }

    private static string NameFor(OpenApiOperation operation, string method, string path) =>
        string.IsNullOrWhiteSpace(operation.OperationId) ? $"{method} {path}" : operation.OperationId;
}

/// <summary>What an import's named examples became (#449): the scenario, the states it bound and what it did not bind.</summary>
/// <param name="Scenario">The one scenario every example stub of the import binds to.</param>
/// <param name="States">Every bound state, per operation.</param>
/// <param name="Ignored">Every named example that was not bound, with the reason.</param>
public sealed record OpenApiScenarioReport(
    string Scenario, IReadOnlyList<OpenApiScenarioState> States, IReadOnlyList<OpenApiScenarioIgnored> Ignored);

/// <summary>One named example bound as a scenario state (#449).</summary>
/// <param name="Operation">The operation's id, or <c>METHOD path</c> when it has none.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path template.</param>
/// <param name="State">The state that selects the example: the example's name.</param>
/// <param name="Response">The response key that declares the example, as the document spells it (<c>402</c>, <c>4XX</c>).</param>
/// <param name="Status">The status the stub answers: the key's code, or a range's lowest (<c>4XX</c> answers 400).</param>
public sealed record OpenApiScenarioState(string Operation, string Method, string Path, string State, string Response, int Status);

/// <summary>One named example an import did not bind, and why (#449).</summary>
/// <param name="Operation">The operation's id, or <c>METHOD path</c> when it has none.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path template.</param>
/// <param name="State">The example's name, control characters replaced.</param>
/// <param name="Reason">Why it was not bound.</param>
public sealed record OpenApiScenarioIgnored(string Operation, string Method, string Path, string State, string Reason);
