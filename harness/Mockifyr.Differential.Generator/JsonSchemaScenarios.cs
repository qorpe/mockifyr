using System.Text;
using System.Text.Json;

namespace Mockifyr.Differential.Generator;

/// <summary>
/// Fuzzes <c>matchesJsonSchema</c> (body) over the common JSON Schema subset (type / required /
/// properties / numeric bounds / enum / array items). Covers both the inline-object and string
/// schema forms and an explicit <c>schemaVersion</c>. Draft-specific edges (Draft 4, format
/// assertions, $ref resolution) are validated later; see docs/parity/g1-matching.md.
/// </summary>
public static class JsonSchemaScenarios
{
    private const string ObjectSchema =
        """{"type":"object","required":["n"],"properties":{"n":{"type":"integer","minimum":5,"maximum":100}}}""";

    private const string EnumSchema =
        """{"type":"object","required":["color"],"properties":{"color":{"enum":["red","green","blue"]}}}""";

    private const string ArraySchema =
        """{"type":"array","items":{"type":"number"},"minItems":2}""";

    /// <summary>Inline-object schema, default schema version.</summary>
    public static IEnumerable<MatcherScenario> InlineObject()
    {
        yield return Build("object/required/bounds", Inline(ObjectSchema),
            ("""{"n":7}""", true),
            ("""{"n":5}""", true),           // minimum is inclusive
            ("""{"n":100}""", true),         // maximum is inclusive
            ("""{"n":7,"extra":1}""", true), // additionalProperties allowed by default
            ("""{"n":3}""", false),          // below minimum
            ("""{"n":101}""", false),        // above maximum
            ("""{"n":"7"}""", false),        // wrong type
            ("""{"x":1}""", false),          // missing required
            ("not json", false));

        yield return Build("enum", Inline(EnumSchema),
            ("""{"color":"red"}""", true),
            ("""{"color":"blue"}""", true),
            ("""{"color":"purple"}""", false),
            ("""{}""", false));

        yield return Build("array/items/minItems", Inline(ArraySchema),
            ("[1,2]", true),
            ("[1,2,3]", true),
            ("[1]", false),          // below minItems
            ("""[1,"x"]""", false),  // wrong item type
            ("[]", false));
    }

    /// <summary>String-form schema and an explicit <c>schemaVersion</c> — both must agree with the oracle.</summary>
    public static IEnumerable<MatcherScenario> StringFormAndVersion()
    {
        // Schema supplied as an escaped JSON string rather than an inline object.
        yield return Build("string-form schema", ObjectSchema,
            ("""{"n":7}""", true),
            ("""{"n":3}""", false),
            ("""{"x":1}""", false));

        // Draft-07 schema selected explicitly via schemaVersion.
        const string draft7 =
            """{"$schema":"http://json-schema.org/draft-07/schema#","type":"object","required":["id"],"properties":{"id":{"type":"string"}}}""";
        yield return BuildVersioned("schemaVersion V7", Inline(draft7), "V7",
            ("""{"id":"abc"}""", true),
            ("""{"id":123}""", false),
            ("""{"other":"x"}""", false));
    }

    /// <summary>
    /// <c>format</c>: WireMock (learned from the oracle) treats it as an <em>annotation-only</em> no-op
    /// on its default draft (2020-12) — a malformed value still matches — but as an <em>assertion</em> on
    /// an explicit Draft-07, where a malformed value fails. These cases pin both. A wrong-type case
    /// guarantees a non-match so the coverage guard holds regardless.
    /// </summary>
    public static IEnumerable<MatcherScenario> Format()
    {
        // Default draft (2020-12): format is annotation-only, so even a malformed value matches.
        // The non-match guard is an *object* body: WireMock's validator is typeLoose for scalars (a
        // number/bool coerces to a string — see parity doc), but object-vs-string stays a real mismatch.
        const string emailDefault = """{"type":"string","format":"email"}""";
        yield return Build("format/email (default draft, annotation-only)", Inline(emailDefault),
            (""" "user@example.com" """.Trim(), true),
            (""" "not-an-email" """.Trim(), true),   // annotation-only → still matches
            ("""{"x":1}""", false));                  // object vs string → a real non-match

        const string dateTimeDefault = """{"type":"string","format":"date-time"}""";
        yield return Build("format/date-time (default draft, annotation-only)", Inline(dateTimeDefault),
            (""" "2020-01-02T03:04:05Z" """.Trim(), true),
            (""" "not-a-date" """.Trim(), true));

        const string uuidDefault = """{"type":"string","format":"uuid"}""";
        yield return Build("format/uuid (default draft, annotation-only)", Inline(uuidDefault),
            (""" "3fa85f64-5717-4562-b3fc-2c963f66afa6" """.Trim(), true),
            (""" "not-a-uuid" """.Trim(), true));

        // Explicit Draft-07: format is an assertion, so a malformed value fails.
        const string emailDraft7 =
            """{"$schema":"http://json-schema.org/draft-07/schema#","type":"string","format":"email"}""";
        yield return Build("format/email (draft-07, asserted)", Inline(emailDraft7),
            (""" "user@example.com" """.Trim(), true),
            (""" "not-an-email" """.Trim(), false)); // asserted → malformed fails

        // Explicit Draft-07 via schemaVersion token (schema omits $schema).
        const string dateTime = """{"type":"string","format":"date-time"}""";
        yield return BuildVersioned("format/date-time (V7 token, asserted)", Inline(dateTime), "V7",
            (""" "2020-01-02T03:04:05Z" """.Trim(), true),
            (""" "not-a-date" """.Trim(), false));
    }

    /// <summary>
    /// Internal <c>$ref</c> resolution: a schema whose properties reference a reusable definition via
    /// <c>#/$defs/…</c> (Draft 2020-12) and <c>#/definitions/…</c> (Draft-07). Both validators resolve
    /// intra-document refs, so the match decision must agree. Remote/URL refs are deferred.
    /// </summary>
    public static IEnumerable<MatcherScenario> Ref()
    {
        const string defsSchema =
            """
            {
              "type": "object",
              "required": ["billing"],
              "properties": {
                "billing": { "$ref": "#/$defs/address" },
                "shipping": { "$ref": "#/$defs/address" }
              },
              "$defs": {
                "address": {
                  "type": "object",
                  "required": ["city"],
                  "properties": { "city": { "type": "string" }, "zip": { "type": "string" } }
                }
              }
            }
            """;
        yield return Build("ref/$defs (2020-12)", Inline(defsSchema),
            ("""{"billing":{"city":"NYC"}}""", true),
            ("""{"billing":{"city":"NYC","zip":"10001"},"shipping":{"city":"LA"}}""", true),
            ("""{"billing":{"zip":"10001"}}""", false),          // referenced required `city` missing
            ("""{"shipping":{"city":"LA"}}""", false),           // top-level required `billing` missing
            ("""{"billing":{"city":5}}""", false));              // referenced `city` wrong type

        // Draft-07 uses `definitions` and an explicit $schema; the ref target is selected the same way.
        const string definitionsSchema =
            """
            {
              "$schema": "http://json-schema.org/draft-07/schema#",
              "type": "object",
              "required": ["item"],
              "properties": { "item": { "$ref": "#/definitions/named" } },
              "definitions": {
                "named": { "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } }
              }
            }
            """;
        yield return Build("ref/definitions (draft-07)", Inline(definitionsSchema),
            ("""{"item":{"name":"x"}}""", true),
            ("""{"item":{}}""", false),                          // referenced required `name` missing
            ("""{"other":1}""", false));                         // top-level required `item` missing
    }

    /// <summary>
    /// networknt <c>typeLoose</c> (learned from the oracle): a <b>non-string scalar</b> top-level body is
    /// validated as its JSON-literal string form too, so it matches <c>type:string</c> / string-shaped
    /// constraints / <c>enum</c> / <c>const</c>. Objects/arrays get no such fallback, nested positions are
    /// not coerced, and the other types stay strict. See docs/parity/g1-matching.md.
    /// </summary>
    public static IEnumerable<MatcherScenario> TypeLoose()
    {
        yield return Build("typeLoose/string-accepts-scalars", Inline("""{"type":"string"}"""),
            ("123", true), ("true", true), ("1.5", true), ("null", true), ("\"abc\"", true),
            ("{}", false), ("[]", false));

        yield return Build("typeLoose/string-with-constraints", Inline("""{"type":"string","minLength":3}"""),
            ("123", true),        // literal "123" has length 3
            ("5", false),         // literal "5" has length 1
            ("\"abc\"", true),
            ("\"ab\"", false));

        yield return Build("typeLoose/integer-stays-strict", Inline("""{"type":"integer"}"""),
            ("123", true),
            ("\"123\"", false),   // a string is NOT coerced to integer
            ("1.5", false));

        yield return Build("typeLoose/enum-scalar-literal", Inline("""{"enum":["123","x"]}"""),
            ("123", true),        // the number literal "123" is in the enum
            ("\"x\"", true),
            ("456", false));

        // The coercion does NOT apply below the top level.
        yield return Build("typeLoose/nested-not-coerced",
            Inline("""{"type":"object","properties":{"n":{"type":"string"}}}"""),
            ("{\"n\":\"ok\"}", true),
            ("{\"n\":123}", false)); // nested number vs type:string → no match
    }

    private static object Inline(string schemaJson) => JsonSerializer.Deserialize<JsonElement>(schemaJson);

    private static MatcherScenario Build(string description, object schema, params (string Body, bool Match)[] bodies) =>
        Compose(description, new Dictionary<string, object> { ["matchesJsonSchema"] = schema }, bodies);

    private static MatcherScenario BuildVersioned(
        string description, object schema, string schemaVersion, params (string Body, bool Match)[] bodies) =>
        Compose(
            description,
            new Dictionary<string, object> { ["matchesJsonSchema"] = schema, ["schemaVersion"] = schemaVersion },
            bodies);

    private static MatcherScenario Compose(string description, Dictionary<string, object> matcher, (string Body, bool Match)[] bodies)
    {
        var mapping = new Dictionary<string, object>
        {
            ["request"] = new Dictionary<string, object>
            {
                ["method"] = "POST",
                ["urlPath"] = "/p",
                ["bodyPatterns"] = new object[] { matcher },
            },
            ["response"] = new Dictionary<string, object> { ["status"] = 200, ["body"] = "ok" },
        };

        var probes = bodies
            .Select(b => new ProbeRequest(
                new RequestSpec { Method = "POST", Url = "/p", Body = Encoding.UTF8.GetBytes(b.Body) },
                b.Match))
            .ToList();

        return new MatcherScenario($"matchesJsonSchema[{description}]", JsonSerializer.Serialize(mapping), probes);
    }
}
