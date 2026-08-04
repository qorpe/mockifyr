using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mockifyr.Core;

/// <summary>
/// What the journal must not keep (#227): header names and JSON body field names whose values are
/// replaced before a serve event is stored. Names are matched case-insensitively; an empty set
/// means "mask nothing", which is the default — masking is opt-in because a masked value is also
/// invisible to <c>verify</c> and near-miss diagnostics, which read the same stored request.
/// </summary>
public sealed record JournalMaskingOptions(
    IReadOnlySet<string> Headers,
    IReadOnlySet<string> BodyFields)
{
    /// <summary>The placeholder written in place of a masked value.</summary>
    public const string Placeholder = "***";

    /// <summary>Masks nothing — the default, and a zero-cost path.</summary>
    public static JournalMaskingOptions None { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Builds options from comma-separated names (blank entries ignored).</summary>
    public static JournalMaskingOptions Parse(string? headers, string? bodyFields) => new(
        Split(headers), Split(bodyFields));

    /// <summary>True when nothing is configured, so the journal can skip masking entirely.</summary>
    public bool IsEmpty => Headers.Count == 0 && BodyFields.Count == 0;

    private static HashSet<string> Split(string? value) =>
        new((value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Rewrites a request so configured header and JSON-field values are replaced by a placeholder
/// (#227). Pure and allocation-light: an unconfigured mask returns the request unchanged, and a
/// body that is not JSON (or names no configured field) is left byte-for-byte as it was.
/// </summary>
public static class JournalMasker
{
    /// <summary>Returns the request with configured values masked, or the same instance when nothing applies.</summary>
    public static CanonicalRequest Mask(CanonicalRequest request, JournalMaskingOptions options)
    {
        if (options.IsEmpty)
        {
            return request;
        }

        var headers = MaskHeaders(request.Headers, options.Headers);
        var body = MaskBody(request.Body, options.BodyFields);
        return ReferenceEquals(headers, request.Headers) && ReferenceEquals(body, request.Body)
            ? request
            : request with { Headers = headers, Body = body };
    }

    private static ILookup<string, string> MaskHeaders(ILookup<string, string> headers, IReadOnlySet<string> masked)
    {
        if (masked.Count == 0 || !headers.Any(group => masked.Contains(group.Key)))
        {
            return headers;
        }

        // Rebuilt with the same comparer semantics the builder uses: header lookups stay
        // case-insensitive after masking, and multi-valued headers keep their arity.
        return headers
            .SelectMany(group => group.Select(value => (group.Key, Value: masked.Contains(group.Key)
                ? JournalMaskingOptions.Placeholder
                : value)))
            .ToLookup(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    // The body is masked structurally, not textually: a JSON document is walked and the named
    // fields (at any depth, inside arrays too) get the placeholder. Anything that does not parse as
    // JSON is returned untouched — masking must never corrupt a recorded payload.
    private static byte[] MaskBody(byte[] body, IReadOnlySet<string> fields)
    {
        if (fields.Count == 0 || body.Length == 0)
        {
            return body;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }

        if (root is null || !MaskNode(root, fields))
        {
            return body;
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    /// <summary>Replaces every configured field in place; returns true when anything changed.</summary>
    private static bool MaskNode(JsonNode node, IReadOnlySet<string> fields)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (fields.Contains(name))
                    {
                        obj[name] = JsonMaskingValue;
                        changed = true;
                    }
                    else if (obj[name] is { } child && MaskNode(child, fields))
                    {
                        changed = true;
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null && MaskNode(item, fields))
                    {
                        changed = true;
                    }
                }

                break;
        }

        return changed;
    }

    private static JsonNode JsonMaskingValue => JsonValue.Create(JournalMaskingOptions.Placeholder)!;
}

/// <summary>
/// An <see cref="IRequestJournal"/> decorator that masks configured values before delegating to the
/// real journal (#227). Sitting at the store seam rather than inside the engine keeps
/// <see cref="StubEngine"/> unaware of masking, and covers every serve event through the single
/// <see cref="IRequestJournal.Record"/> choke point — the value is never stored, so it cannot be
/// read back through the admin API or the dashboard.
/// </summary>
public sealed class MaskingRequestJournal(IRequestJournal inner, JournalMaskingOptions options) : IRequestJournal
{
    /// <inheritdoc />
    public void Record(ServeEvent serveEvent)
    {
        var masked = JournalMasker.Mask(serveEvent.Request, options);
        inner.Record(ReferenceEquals(masked, serveEvent.Request)
            ? serveEvent
            : serveEvent with { Request = masked });
    }

    /// <inheritdoc />
    public IReadOnlyList<ServeEvent> Query(TenantId tenant, ServeEventQuery query) => inner.Query(tenant, query);

    /// <inheritdoc />
    public void Clear(TenantId tenant) => inner.Clear(tenant);
}
