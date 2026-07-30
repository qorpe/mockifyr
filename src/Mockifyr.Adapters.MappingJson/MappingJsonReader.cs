using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mockifyr.Core;
using Mockifyr.Matching;

namespace Mockifyr.Adapters.MappingJson;

/// <summary>
/// Reads stub mapping JSON (the canonical format the differential harness loads into
/// both the reference oracle and Mockifyr) into the internal domain model. This adapter is itself
/// verified by the differential suite. See docs/decisions/0004.
/// </summary>
/// <remarks>
/// G0 scope: <c>request.method</c>, <c>request.url</c>/<c>request.urlPath</c>, and
/// <c>response.status</c>/<c>body</c>/<c>headers</c>/<c>priority</c>. The surface grows with the
/// matching (G1) and response (G2) roadmap items.
/// </remarks>
public static class MappingJsonReader
{
    // jsonBody is serialized to the response body verbatim — like the reference oracle (Jackson), which
    // does NOT escape ', <, >, & or non-ASCII. The default System.Text.Json encoder escapes them to
    // \uXXXX, which both diverges from the oracle and, critically, breaks Handlebars template expressions
    // inside a jsonBody (e.g. {{jsonPath request.body '$.x'}} — the ' becomes ' and the helper
    // arg no longer parses). UnsafeRelaxedJsonEscaping keeps them literal, so templated bodies work.
    private static readonly JsonSerializerOptions JsonBodyOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Reads one or more mappings (a single object or a <c>{"mappings":[...]}</c> wrapper). A matcher
    /// registry, when supplied, resolves <c>customMatcher</c> references to extension matchers (G10).
    /// </summary>
    public static IReadOnlyList<StubMapping> Read(string json, TenantId tenant, IMatcherRegistry? matchers = null) =>
        [.. ReadWithSource(json, tenant, matchers).Select(pair => pair.Stub)];

    /// <summary>
    /// Like <see cref="Read"/> but also returns each mapping's own source JSON (a single mapping, even
    /// when read from a <c>{"mappings":[…]}</c> bundle). Persistence (G16) uses the source to write the
    /// stub back to disk faithfully.
    /// </summary>
    public static IReadOnlyList<(StubMapping Stub, string Source)> ReadWithSource(
        string json, TenantId tenant, IMatcherRegistry? matchers = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // A bundle is either a {"mappings":[…]} wrapper or a bare top-level array of mappings — both are
        // accepted so exports pasted without the wrapper still import.
        var bundle = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object &&
              root.TryGetProperty("mappings", out var mappings) &&
              mappings.ValueKind == JsonValueKind.Array
                ? mappings
                : (JsonElement?)null;

        if (bundle is { } arr)
        {
            return [.. arr.EnumerateArray().Select(m => { var src = m.GetRawText(); return (ReadOne(m, tenant, matchers) with { Source = src }, src); })];
        }

        var source = root.GetRawText();
        return [(ReadOne(root, tenant, matchers) with { Source = source }, source)];
    }

    /// <summary>
    /// Reads a bare request pattern (the body of <c>/__admin/requests/count</c> and
    /// <c>find</c>) into a <see cref="RequestPattern"/>, reusing the same matcher parsing as stubs.
    /// An empty object <c>{}</c> matches every request.
    /// </summary>
    public static RequestPattern ReadRequestPattern(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadRequest(doc.RootElement);
    }

    private static StubMapping ReadOne(JsonElement mapping, TenantId tenant, IMatcherRegistry? matchers)
    {
        var request = mapping.TryGetProperty("request", out var r) ? r : default;
        var response = mapping.TryGetProperty("response", out var rsp) ? rsp : default;

        return new StubMapping
        {
            Id = ReadId(mapping),
            TenantId = tenant,
            Priority = mapping.TryGetProperty("priority", out var p) && p.TryGetInt32(out var pri) ? pri : 5,
            Request = ReadRequest(request, matchers),
            Response = ReadResponse(response),
            Webhooks = ReadWebhooks(mapping),
            Scenario = ReadScenario(mapping),
            Metadata = ReadMetadata(mapping),
        };
    }

    /// <summary>Reads the stub's <c>id</c>/<c>uuid</c> (both keys are accepted), or mints a new one.</summary>
    private static Guid ReadId(JsonElement mapping)
    {
        if (mapping.ValueKind == JsonValueKind.Object &&
            (mapping.TryGetProperty("id", out var id) || mapping.TryGetProperty("uuid", out id)) &&
            id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed))
        {
            return parsed;
        }

        return Guid.NewGuid();
    }

    /// <summary>Reads the arbitrary <c>metadata</c> object attached to a stub.</summary>
    private static StubMetadata? ReadMetadata(JsonElement mapping)
    {
        if (mapping.ValueKind != JsonValueKind.Object ||
            !mapping.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var values = new Dictionary<string, object?>();
        foreach (var property in metadata.EnumerateObject())
        {
            values[property.Name] = ConvertJson(property.Value);
        }

        return new StubMetadata(values);
    }

    private static object? ConvertJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJson(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJson).ToList(),
        _ => null,
    };

    /// <summary>
    /// Reads webhook actions — <c>[{ "name": "webhook", "parameters": { "method", "url", "headers",
    /// "body" } }]</c> — from <c>postServeActions</c> (the 2.x form) <em>and</em>
    /// <c>serveEventListeners</c> (the 3.x form); the upstream accepts both, so a mapping exported
    /// from either generation keeps its callbacks (#147). Non-webhook entries are ignored.
    /// </summary>
    private static IReadOnlyList<WebhookDefinition> ReadWebhooks(JsonElement mapping)
    {
        if (mapping.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var webhooks = new List<WebhookDefinition>();
        foreach (var key in (string[])["postServeActions", "serveEventListeners"])
        {
            if (mapping.TryGetProperty(key, out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                ReadWebhookActions(actions, webhooks);
            }
        }

        return webhooks;
    }

    private static void ReadWebhookActions(JsonElement actions, List<WebhookDefinition> webhooks)
    {
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object ||
                !action.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !string.Equals(name.GetString(), "webhook", StringComparison.OrdinalIgnoreCase) ||
                !action.TryGetProperty("parameters", out var parameters) ||
                parameters.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var method = parameters.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()!
                : "GET";
            var url = parameters.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()!
                : string.Empty;

            var headers = new List<KeyValuePair<string, string>>();
            if (parameters.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in h.EnumerateObject())
                {
                    if (header.Value.ValueKind == JsonValueKind.Array)
                    {
                        headers.AddRange(header.Value.EnumerateArray()
                            .Select(v => new KeyValuePair<string, string>(header.Name, v.GetString() ?? string.Empty)));
                    }
                    else
                    {
                        headers.Add(new(header.Name, header.Value.GetString() ?? string.Empty));
                    }
                }
            }

            // The webhook payload accepts `body` (string) and `base64Body` (bytes).
            byte[]? body = null;
            if (parameters.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String)
            {
                body = Encoding.UTF8.GetBytes(b.GetString()!);
            }
            else if (parameters.TryGetProperty("base64Body", out var b64) && b64.ValueKind == JsonValueKind.String &&
                     TryFromBase64(b64.GetString()!, out var decoded))
            {
                body = decoded;
            }

            // Webhook delay shape: { "delay": { "type": "fixed", "milliseconds": N } }.
            var delayMs = 0;
            if (parameters.TryGetProperty("delay", out var delay) && delay.ValueKind == JsonValueKind.Object &&
                delay.TryGetProperty("milliseconds", out var ms) && ms.ValueKind == JsonValueKind.Number)
            {
                delayMs = ms.GetInt32();
            }

            webhooks.Add(new WebhookDefinition { Method = method, Url = url, Headers = headers, Body = body, DelayMilliseconds = delayMs });
        }
    }

    /// <summary>
    /// Reads a stub's scenario binding: <c>scenarioName</c> plus the optional
    /// <c>requiredScenarioState</c> (eligibility) and <c>newScenarioState</c> (transition on serve).
    /// The engine reads/writes the state; the default start state is <c>Started</c>.
    /// </summary>
    private static ScenarioBinding? ReadScenario(JsonElement mapping)
    {
        if (mapping.ValueKind != JsonValueKind.Object ||
            !mapping.TryGetProperty("scenarioName", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return new ScenarioBinding
        {
            ScenarioName = name.GetString()!,
            RequiredState = mapping.TryGetProperty("requiredScenarioState", out var rs) && rs.ValueKind == JsonValueKind.String
                ? rs.GetString()
                : null,
            NewState = mapping.TryGetProperty("newScenarioState", out var ns) && ns.ValueKind == JsonValueKind.String
                ? ns.GetString()
                : null,
        };
    }

    private static RequestPattern ReadRequest(JsonElement request, IMatcherRegistry? matchers = null)
    {
        IMatcher? url = null;
        string? urlPathTemplate = null;
        if (request.ValueKind == JsonValueKind.Object)
        {
            if (request.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            {
                url = new UrlEqualToMatcher(u.GetString()!);
            }
            else if (request.TryGetProperty("urlPattern", out var upat) && upat.ValueKind == JsonValueKind.String)
            {
                url = new UrlPatternMatcher(upat.GetString()!);
            }
            else if (request.TryGetProperty("urlPath", out var up) && up.ValueKind == JsonValueKind.String)
            {
                url = new UrlPathEqualToMatcher(up.GetString()!);
            }
            else if (request.TryGetProperty("urlPathPattern", out var uppat) && uppat.ValueKind == JsonValueKind.String)
            {
                url = new UrlPathPatternMatcher(uppat.GetString()!);
            }
            else if (request.TryGetProperty("urlPathTemplate", out var upt) && upt.ValueKind == JsonValueKind.String)
            {
                urlPathTemplate = upt.GetString()!;
                url = new UrlPathTemplateMatcher(urlPathTemplate);
            }
        }

        var method = request.ValueKind == JsonValueKind.Object &&
                     request.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : "ANY";

        // Multi-domain matching (G15c): `scheme` is a plain string, `host` a StringValuePattern, and
        // `port` an integer. One instance can thus serve many domains. See docs/parity/g15-extras.md.
        IMatcher? scheme = null;
        IMatcher? host = null;
        IMatcher? port = null;
        if (request.ValueKind == JsonValueKind.Object)
        {
            if (request.TryGetProperty("scheme", out var sch) && sch.ValueKind == JsonValueKind.String)
            {
                scheme = new SchemeMatcher(sch.GetString()!);
            }

            if (request.TryGetProperty("host", out var hst) && BuildValueMatcher(hst) is { } hostValue)
            {
                host = new HostMatcher(hostValue);
            }

            if (request.TryGetProperty("port", out var prt) &&
                prt.ValueKind == JsonValueKind.Number && prt.TryGetInt32(out var portNum))
            {
                port = new PortMatcher(portNum);
            }
        }

        var headers = new List<IMatcher>(
            ReadNamedMatchers(request, "headers", static (name, vm) => new HeaderMatcher(name, vm)));
        if (ReadBasicAuth(request) is { } basicAuth)
        {
            headers.Add(basicAuth);
        }

        return new RequestPattern
        {
            Url = url,
            UrlPathTemplate = urlPathTemplate,
            Method = new MethodMatcher(method),
            Scheme = scheme,
            Host = host,
            Port = port,
            Headers = headers,
            Query = ReadNamedMatchers(request, "queryParameters", static (name, vm) => new QueryMatcher(name, vm)),
            FormParameters = ReadNamedMatchers(request, "formParameters", static (name, vm) => new FormParameterMatcher(name, vm)),
            Cookies = ReadNamedMatchers(request, "cookies", static (name, vm) => new CookieMatcher(name, vm)),
            Body = ReadBodyMatchers(request),
            Decrypt = ReadDecryptDirective(request),
            Signature = ReadSignatureRequirement(request),
            Custom = ReadCustomMatchers(request, matchers),
        };
    }

    /// <summary>
    /// Resolves a <c>customMatcher</c> reference. The built-in <c>graphql-body-matcher</c> (G14) is
    /// parameterized per stub (<c>parameters.query</c>) and built directly; any other name resolves to
    /// a user extension matcher via the registry (G10). An unknown name contributes no matcher.
    /// </summary>
    private static IReadOnlyList<IMatcher> ReadCustomMatchers(JsonElement request, IMatcherRegistry? matchers)
    {
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty("customMatcher", out var custom) ||
            custom.ValueKind != JsonValueKind.Object ||
            !custom.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        var matcherName = name.GetString()!;

        // GraphQL matcher: customMatcher name "graphql-body-matcher". parameters.query is the expected
        // query (whitespace/field-order-insensitive); optional parameters.variables (JSON, semantic
        // equal) and parameters.operationName (string) are matched too. See docs/parity/g14-graphql.md.
        if (matcherName == "graphql-body-matcher" &&
            custom.TryGetProperty("parameters", out var parameters) &&
            parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String)
        {
            var variables = parameters.TryGetProperty("variables", out var v) && v.ValueKind is not JsonValueKind.Null
                ? v.GetRawText()
                : null;
            var operationName = parameters.TryGetProperty("operationName", out var o) && o.ValueKind == JsonValueKind.String
                ? o.GetString()
                : null;
            return [new GraphqlQueryMatcher(query.GetString()!, variables, operationName)];
        }

        return matchers?.Resolve(matcherName) is { } matcher ? [matcher] : [];
    }

    /// <summary>
    /// Reads the opt-in request-signature requirement (G20c, ADR 0012):
    /// <c>"signature": { "scheme": "hmac-sha256", "header": "X-JWS-Signature", "digestHeader": "Digest" }</c>.
    /// The header names default to the PSD2 / Berlin Group ones, so the common case is just a scheme.
    /// </summary>
    private static SignatureRequirement? ReadSignatureRequirement(JsonElement request)
    {
        if (!request.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scheme = ReadText(signature, "scheme");
        return string.IsNullOrWhiteSpace(scheme)
            ? null
            : new SignatureRequirement(
                scheme,
                ReadText(signature, "header") ?? "X-JWS-Signature",
                ReadText(signature, "digestHeader") ?? "Digest");
    }

    /// <summary>
    /// Reads the opt-in response-signing declaration (G20c, ADR 0012):
    /// <c>"sign": { "scheme": "hmac-sha256", "header": "X-JWS-Signature", "digestHeader": "Digest" }</c>.
    /// </summary>
    private static ResponseSignature? ReadResponseSignature(JsonElement response)
    {
        if (!response.TryGetProperty("sign", out var sign) || sign.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scheme = ReadText(sign, "scheme");
        return string.IsNullOrWhiteSpace(scheme)
            ? null
            : new ResponseSignature(
                scheme,
                ReadText(sign, "header") ?? "X-JWS-Signature",
                ReadText(sign, "digestHeader") ?? "Digest");
    }

    private static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads the opt-in response-protection declaration (G20b, ADR 0012):
    /// <c>"protect": { "scheme": "jwe-dir-a256gcm", "fields": ["encData"] }</c>. An absent or empty
    /// <c>fields</c> array means whole-body protection, which is a valid shape — unlike the decrypt
    /// side, where naming no field would be meaningless.
    /// </summary>
    private static PayloadProtectDirective? ReadProtectDirective(JsonElement response)
    {
        if (!response.TryGetProperty("protect", out var protect) || protect.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scheme = protect.TryGetProperty("scheme", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return null;
        }

        var names = protect.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray()
                .Where(f => f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
                .Select(f => f.GetString()!)
                .ToArray()
            : [];

        return new PayloadProtectDirective(scheme, names);
    }

    /// <summary>
    /// Reads the opt-in payload-decryption declaration (G20a, ADR 0012):
    /// <c>"decrypt": { "scheme": "jwe-dir-a256gcm", "fields": ["encData"] }</c>. Absent, malformed
    /// or empty-field declarations read as null — a stub never becomes half-configured.
    /// </summary>
    private static PayloadDecryptDirective? ReadDecryptDirective(JsonElement request)
    {
        if (!request.TryGetProperty("decrypt", out var decrypt) || decrypt.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scheme = decrypt.TryGetProperty("scheme", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(scheme) ||
            !decrypt.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var names = fields.EnumerateArray()
            .Where(f => f.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.GetString()))
            .Select(f => f.GetString()!)
            .ToArray();

        return names.Length == 0 ? null : new PayloadDecryptDirective(scheme, names);
    }

    private static IReadOnlyList<IMatcher> ReadBodyMatchers(JsonElement request)
    {
        var matchers = new List<IMatcher>(ReadBodyPatterns(request));
        matchers.AddRange(ReadMultipartMatchers(request));
        return matchers;
    }

    private static IEnumerable<IMatcher> ReadMultipartMatchers(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty("multipartPatterns", out var patterns) ||
            patterns.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var pattern in patterns.EnumerateArray())
        {
            if (pattern.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var bodyPatterns = new List<IValueMatcher>();
            if (pattern.TryGetProperty("bodyPatterns", out var bps) && bps.ValueKind == JsonValueKind.Array)
            {
                bodyPatterns.AddRange(BuildValueMatchers(bps));
            }

            // The default matchingType is ANY; the per-pattern `name` is a no-op (verified by the
            // differential suite; see parity doc).
            var matchingType =
                pattern.TryGetProperty("matchingType", out var mt) && mt.ValueKind == JsonValueKind.String &&
                string.Equals(mt.GetString(), "ALL", StringComparison.OrdinalIgnoreCase)
                    ? MultipartMatchingType.All
                    : MultipartMatchingType.Any;

            if (bodyPatterns.Count > 0)
            {
                yield return new MultipartMatcher(bodyPatterns, matchingType);
            }
        }
    }

    /// <summary>
    /// Reads <c>basicAuthCredentials</c> into an <c>Authorization: Basic &lt;base64(user:pass)&gt;</c>
    /// header matcher. Verified against the oracle: it is exact-equality on the computed token.
    /// </summary>
    private static HeaderMatcher? ReadBasicAuth(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty("basicAuthCredentials", out var creds) ||
            creds.ValueKind != JsonValueKind.Object ||
            !creds.TryGetProperty("username", out var u) || u.ValueKind != JsonValueKind.String ||
            !creds.TryGetProperty("password", out var p) || p.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var token = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{u.GetString()}:{p.GetString()}"));
        return new HeaderMatcher("Authorization", new EqualToValueMatcher(token));
    }

    private static IReadOnlyList<IMatcher> ReadNamedMatchers(
        JsonElement request,
        string property,
        Func<string, IValueMatcher, IMatcher> factory)
    {
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty(property, out var container) ||
            container.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var matchers = new List<IMatcher>();
        foreach (var entry in container.EnumerateObject())
        {
            if (BuildValueMatcher(entry.Value) is { } value)
            {
                matchers.Add(factory(entry.Name, value));
            }
        }

        return matchers;
    }

    private static IReadOnlyList<IMatcher> ReadBodyPatterns(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object ||
            !request.TryGetProperty("bodyPatterns", out var patterns) ||
            patterns.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var matchers = new List<IMatcher>();
        foreach (var pattern in patterns.EnumerateArray())
        {
            // binaryEqualTo is a byte-level comparison, not a text value matcher.
            if (pattern.ValueKind == JsonValueKind.Object &&
                pattern.TryGetProperty("binaryEqualTo", out var bin) && bin.ValueKind == JsonValueKind.String &&
                TryFromBase64(bin.GetString()!, out var expected))
            {
                matchers.Add(new BinaryEqualToBodyMatcher(expected));
                continue;
            }

            if (BuildValueMatcher(pattern) is { } value)
            {
                matchers.Add(new BodyMatcher(value));
            }
        }

        return matchers;
    }

    private static bool TryFromBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static IValueMatcher? BuildValueMatcher(JsonElement spec)
    {
        if (spec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var caseInsensitive = spec.TryGetProperty("caseInsensitive", out var ci) &&
                              ci.ValueKind == JsonValueKind.True;

        // Logical combinators (and/or/not) wrap other content patterns on the same target value.
        if (spec.TryGetProperty("and", out var andArr) && andArr.ValueKind == JsonValueKind.Array)
        {
            var subs = BuildValueMatchers(andArr);
            return subs.Count > 0 ? new AndValueMatcher(subs) : null;
        }

        if (spec.TryGetProperty("or", out var orArr) && orArr.ValueKind == JsonValueKind.Array)
        {
            var subs = BuildValueMatchers(orArr);
            return subs.Count > 0 ? new OrValueMatcher(subs) : null;
        }

        if (spec.TryGetProperty("not", out var notSpec) && notSpec.ValueKind == JsonValueKind.Object)
        {
            return BuildValueMatcher(notSpec) is { } inner ? new NotValueMatcher(inner) : null;
        }

        // Multi-value matchers (hasExactly/includes) apply a list of sub-matchers to a multi-valued target.
        if (spec.TryGetProperty("hasExactly", out var hasExactly) && hasExactly.ValueKind == JsonValueKind.Array)
        {
            var subs = BuildValueMatchers(hasExactly);
            return subs.Count > 0 ? new HasExactlyValueMatcher(subs) : null;
        }

        if (spec.TryGetProperty("includes", out var includes) && includes.ValueKind == JsonValueKind.Array)
        {
            var subs = BuildValueMatchers(includes);
            return subs.Count > 0 ? new IncludesValueMatcher(subs) : null;
        }

        if (spec.TryGetProperty("equalToJson", out var ej))
        {
            // equalToJson accepts either a JSON string or inline JSON.
            var expectedJson = ej.ValueKind == JsonValueKind.String ? ej.GetString()! : ej.GetRawText();
            var ignoreArrayOrder = spec.TryGetProperty("ignoreArrayOrder", out var iao) && iao.ValueKind == JsonValueKind.True;
            var ignoreExtraElements = spec.TryGetProperty("ignoreExtraElements", out var iee) && iee.ValueKind == JsonValueKind.True;
            return new EqualToJsonValueMatcher(expectedJson, ignoreArrayOrder, ignoreExtraElements);
        }

        if (spec.TryGetProperty("matchesJsonPath", out var jsonPath))
        {
            // String form is presence; object form is `{ "expression": "...", <sub-matcher> }`.
            if (jsonPath.ValueKind == JsonValueKind.String)
            {
                return new MatchesJsonPathValueMatcher(jsonPath.GetString()!);
            }

            if (jsonPath.ValueKind == JsonValueKind.Object &&
                jsonPath.TryGetProperty("expression", out var expression) &&
                expression.ValueKind == JsonValueKind.String)
            {
                return new MatchesJsonPathValueMatcher(expression.GetString()!, BuildValueMatcher(jsonPath));
            }

            return null;
        }

        if (spec.TryGetProperty("matchesJsonSchema", out var schema) &&
            schema.ValueKind is JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array)
        {
            // The schema is either an escaped JSON string or inline JSON (object/array).
            var schemaJson = schema.ValueKind == JsonValueKind.String ? schema.GetString()! : schema.GetRawText();
            var schemaVersion = spec.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.String
                ? sv.GetString()
                : null;
            return new MatchesJsonSchemaValueMatcher(schemaJson, schemaVersion);
        }

        if (spec.TryGetProperty("equalToXml", out var equalToXml) && equalToXml.ValueKind == JsonValueKind.String)
        {
            var enablePlaceholders = spec.TryGetProperty("enablePlaceholders", out var ep) && ep.ValueKind == JsonValueKind.True;
            var exempted = new List<string>();
            if (spec.TryGetProperty("exemptedComparisons", out var ex) && ex.ValueKind == JsonValueKind.Array)
            {
                exempted.AddRange(ex.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!));
            }

            return new EqualToXmlValueMatcher(
                equalToXml.GetString()!,
                enablePlaceholders,
                ReadString(spec, "placeholderOpeningDelimiterRegex"),
                ReadString(spec, "placeholderClosingDelimiterRegex"),
                exempted);
        }

        if (spec.TryGetProperty("matchesXPath", out var xPath))
        {
            if (xPath.ValueKind == JsonValueKind.String)
            {
                return new MatchesXPathValueMatcher(xPath.GetString()!);
            }

            if (xPath.ValueKind == JsonValueKind.Object &&
                xPath.TryGetProperty("expression", out var xPathExpression) &&
                xPathExpression.ValueKind == JsonValueKind.String)
            {
                return new MatchesXPathValueMatcher(
                    xPathExpression.GetString()!, BuildValueMatcher(xPath), ReadStringMap(xPath, "xPathNamespaces"));
            }

            return null;
        }

        if (spec.TryGetProperty("before", out var before) && before.ValueKind == JsonValueKind.String)
        {
            return new DateTimeValueMatcher(DateTimeComparison.Before, before.GetString()!, ActualFormat(spec));
        }

        if (spec.TryGetProperty("after", out var after) && after.ValueKind == JsonValueKind.String)
        {
            return new DateTimeValueMatcher(DateTimeComparison.After, after.GetString()!, ActualFormat(spec));
        }

        if (spec.TryGetProperty("equalToDateTime", out var edt) && edt.ValueKind == JsonValueKind.String)
        {
            return new DateTimeValueMatcher(DateTimeComparison.Equal, edt.GetString()!, ActualFormat(spec));
        }

        if (spec.TryGetProperty("equalTo", out var eq) && eq.ValueKind == JsonValueKind.String)
        {
            return new EqualToValueMatcher(eq.GetString()!, caseInsensitive);
        }

        // Note: the stub JSON dialect has no `equalToIgnoreCase` key; case-insensitive equality is
        // `equalTo` with `caseInsensitive: true` (handled above). Verified by the differential suite.

        if (spec.TryGetProperty("contains", out var c) && c.ValueKind == JsonValueKind.String)
        {
            return new ContainsValueMatcher(c.GetString()!);
        }

        if (spec.TryGetProperty("doesNotContain", out var dnc) && dnc.ValueKind == JsonValueKind.String)
        {
            return new DoesNotContainValueMatcher(dnc.GetString()!);
        }

        if (spec.TryGetProperty("matches", out var mt) && mt.ValueKind == JsonValueKind.String)
        {
            return new MatchesValueMatcher(mt.GetString()!);
        }

        if (spec.TryGetProperty("doesNotMatch", out var dnm) && dnm.ValueKind == JsonValueKind.String)
        {
            return new DoesNotMatchValueMatcher(dnm.GetString()!);
        }

        if (spec.TryGetProperty("absent", out var absent) && absent.ValueKind == JsonValueKind.True)
        {
            return new AbsentValueMatcher();
        }

        return null;
    }

    /// <summary>Reads a string property, or null if absent/non-string.</summary>
    private static string? ReadString(JsonElement spec, string property) =>
        spec.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Reads a string-to-string map property (e.g. <c>xPathNamespaces</c>), or null if absent/empty.</summary>
    private static IReadOnlyDictionary<string, string>? ReadStringMap(JsonElement spec, string property)
    {
        if (!spec.TryGetProperty(property, out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
            {
                result[entry.Name] = entry.Value.GetString()!;
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>Builds the value matchers from an array of matcher specs, skipping unreadable entries.</summary>
    private static List<IValueMatcher> BuildValueMatchers(JsonElement array)
    {
        var matchers = new List<IValueMatcher>();
        foreach (var element in array.EnumerateArray())
        {
            if (BuildValueMatcher(element) is { } matcher)
            {
                matchers.Add(matcher);
            }
        }

        return matchers;
    }

    /// <summary>Reads the optional <c>actualFormat</c> parse pattern shared by the date/time matchers.</summary>
    private static string? ActualFormat(JsonElement spec) =>
        spec.TryGetProperty("actualFormat", out var af) && af.ValueKind == JsonValueKind.String
            ? af.GetString()
            : null;

    private static ResponseDefinition ReadResponse(JsonElement response)
    {
        var status = response.ValueKind == JsonValueKind.Object &&
                     response.TryGetProperty("status", out var s) && s.TryGetInt32(out var code)
            ? code
            : 200;

        var statusMessage = response.ValueKind == JsonValueKind.Object &&
                            response.TryGetProperty("statusMessage", out var sm) && sm.ValueKind == JsonValueKind.String
            ? sm.GetString()
            : null;

        // Body forms, in precedence order: a literal string body, inline JSON (re-serialized
        // compact, matching the reference oracle's output), or base64-decoded bytes.
        byte[]? body = null;
        if (response.ValueKind == JsonValueKind.Object)
        {
            if (response.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String)
            {
                body = Encoding.UTF8.GetBytes(b.GetString()!);
            }
            else if (response.TryGetProperty("jsonBody", out var jb) &&
                     jb.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(jb, JsonBodyOptions));
            }
            else if (response.TryGetProperty("base64Body", out var b64) && b64.ValueKind == JsonValueKind.String &&
                     TryFromBase64(b64.GetString()!, out var decoded))
            {
                body = decoded;
            }
        }

        var headerPairs = new List<KeyValuePair<string, string>>();
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in h.EnumerateObject())
            {
                if (header.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in header.Value.EnumerateArray())
                    {
                        headerPairs.Add(new(header.Name, v.GetString() ?? string.Empty));
                    }
                }
                else
                {
                    headerPairs.Add(new(header.Name, header.Value.GetString() ?? string.Empty));
                }
            }
        }

        var transformers = new List<string>();
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("transformers", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            transformers.AddRange(t.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!));
        }

        return new ResponseDefinition
        {
            Status = status,
            StatusMessage = statusMessage,
            Headers = headerPairs.ToLookup(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
            Body = body,
            Transformers = transformers,
            Delay = ReadDelay(response),
            DelayDistribution = ReadDelayDistribution(response),
            Fault = ReadFault(response),
            Proxy = ReadProxy(response),
            State = ReadState(response),
            Protect = ReadProtectDirective(response),
            Sign = ReadResponseSignature(response),
        };
    }

    /// <summary>
    /// Reads the Mockifyr-only <c>state</c> directive (G19b, ADR 0011): a sandbox CRUD operation on
    /// a named collection. <c>id</c>/<c>document</c> stay raw template text — they render at serve
    /// time against the request. The field does not exist in the reference dialect, so its absence
    /// leaves the parity surface untouched.
    /// </summary>
    private static StateDirective? ReadState(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object ||
            !state.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String ||
            !state.TryGetProperty("collection", out var collection) || collection.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return new StateDirective(
            operation.GetString()!,
            collection.GetString()!,
            Id: state.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
            Document: state.TryGetProperty("document", out var doc) && doc.ValueKind == JsonValueKind.String ? doc.GetString() : null,
            MissStatus: state.TryGetProperty("missStatus", out var miss) && miss.TryGetInt32(out var missStatus) ? missStatus : 404);
    }

    /// <summary>Reads a <c>uniform</c> <c>delayDistribution</c>; lognormal is deferred (racy to test).</summary>
    private static DelayDistribution? ReadDelayDistribution(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("delayDistribution", out var dist) || dist.ValueKind != JsonValueKind.Object ||
            !dist.TryGetProperty("type", out var type) || type.GetString() != "uniform" ||
            !dist.TryGetProperty("lower", out var lower) || !lower.TryGetInt32(out var lo) ||
            !dist.TryGetProperty("upper", out var upper) || !upper.TryGetInt32(out var hi))
        {
            return null;
        }

        return new DelayDistribution(lo, hi);
    }

    /// <summary>Reads the <c>proxyBaseUrl</c> directive (G8) plus <c>additionalProxyRequestHeaders</c>.</summary>
    private static ProxyDirective? ReadProxy(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("proxyBaseUrl", out var url) || url.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var additionalHeaders = new List<KeyValuePair<string, string>>();
        if (response.TryGetProperty("additionalProxyRequestHeaders", out var headers) &&
            headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.EnumerateObject())
            {
                if (header.Value.ValueKind == JsonValueKind.String)
                {
                    additionalHeaders.Add(new(header.Name, header.Value.GetString()!));
                }
            }
        }

        var prefix = response.TryGetProperty("proxyUrlPrefixToRemove", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

        return new ProxyDirective(url.GetString()!) { AdditionalHeaders = additionalHeaders, UrlPrefixToRemove = prefix };
    }

    /// <summary>Reads the <c>fixedDelayMilliseconds</c> response delay (delayDistribution → G4 follow-up).</summary>
    private static DelayDirective? ReadDelay(JsonElement response) =>
        response.ValueKind == JsonValueKind.Object &&
        response.TryGetProperty("fixedDelayMilliseconds", out var d) && d.TryGetInt32(out var ms) && ms > 0
            ? new DelayDirective(ms)
            : null;

    /// <summary>Reads the <c>fault</c> directive; the transport facade (G12) emits the socket behavior.</summary>
    private static FaultDirective? ReadFault(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("fault", out var fault) || fault.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return fault.GetString() switch
        {
            "EMPTY_RESPONSE" => new FaultDirective(FaultKind.EmptyResponse),
            "MALFORMED_RESPONSE_CHUNK" => new FaultDirective(FaultKind.MalformedResponseChunk),
            "RANDOM_DATA_THEN_CLOSE" => new FaultDirective(FaultKind.RandomDataThenClose),
            "CONNECTION_RESET_BY_PEER" => new FaultDirective(FaultKind.ConnectionResetByPeer),
            _ => null,
        };
    }
}
