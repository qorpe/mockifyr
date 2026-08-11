using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mockifyr.Core;

/// <summary>
/// How a document field is compared in a resource query (#353).
/// </summary>
/// <remarks>
/// Deliberately the vocabulary the mapping dialect already proves — <c>equalTo</c>, <c>contains</c>,
/// <c>matches</c>, <c>absent</c> — rather than a second one invented here. A sandbox that filtered with
/// different words than it matches with would make somebody learn the same idea twice, and the two
/// would drift.
/// </remarks>
public enum ResourceFilterOperator
{
    /// <summary>The field equals the value. The default, and the form with no operator suffix.</summary>
    EqualTo = 0,

    /// <summary>The field's text contains the value.</summary>
    Contains = 1,

    /// <summary>The field's text matches the value as a regular expression.</summary>
    Matches = 2,

    /// <summary>The field is absent (or null) — <c>?field:absent=true</c>; <c>false</c> means present.</summary>
    Absent = 3,
}

/// <summary>One field comparison in a query.</summary>
public sealed record ResourceFilter(string Field, ResourceFilterOperator Operator, string Value);

/// <summary>
/// A parsed list query: which documents, in what order, projected to which fields (#353).
/// </summary>
/// <remarks>
/// <para>
/// Filtering, sorting and field selection — and deliberately nothing else. ADR 0015 put joins,
/// cross-collection transactions and a query language out of scope, and this stays inside that line: a
/// sandbox should behave like the API it stands in for, not become a database that is harder to reason
/// about than the service it replaces.
/// </para>
/// <para>
/// The same parsed query serves both the admin listing and the serve-time <c>list</c> directive. One
/// evaluator, because a sandbox and the screen watching it disagreeing about what a collection contains
/// is worse than neither of them filtering at all.
/// </para>
/// </remarks>
public sealed record ResourceQuery(
    IReadOnlyList<ResourceFilter> Filters,
    string? SortField,
    bool SortDescending,
    IReadOnlyList<string> Fields)
{
    /// <summary>A query that selects everything, unsorted and unprojected.</summary>
    public static ResourceQuery All { get; } = new([], null, false, []);

    /// <summary>Whether this query does anything at all.</summary>
    public bool IsEmpty => Filters.Count == 0 && SortField is null && Fields.Count == 0;

    /// <summary>
    /// The control parameters this query owns. Everything else in a query string is a field filter, so
    /// a document field with one of these names cannot be filtered on — stated in the docs rather than
    /// left to be discovered.
    /// </summary>
    /// <remarks>
    /// <c>limit</c> and <c>offset</c> are unprefixed because they shipped that way and renaming them
    /// would break every existing caller; the ones added here carry the <c>_</c> that marks a control
    /// parameter in the shapes people already know from tools of this class.
    /// </remarks>
    public static IReadOnlyList<string> ControlParameters { get; } = ["limit", "offset", "_sort", "_fields"];

    /// <summary>
    /// Parses a query string. Unknown operator suffixes are read as part of the field name rather than
    /// rejected: <c>?created:at=x</c> is somebody filtering a field called <c>created:at</c>, and
    /// guessing otherwise would refuse a legitimate query.
    /// </summary>
    public static ResourceQuery Parse(IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var filters = new List<ResourceFilter>();
        string? sortField = null;
        var descending = false;
        var fields = new List<string>();

        foreach (var (rawKey, rawValue) in parameters)
        {
            var key = rawKey?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (string.Equals(key, "_sort", StringComparison.Ordinal))
            {
                var value = rawValue?.Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    descending = value[0] == '-';
                    sortField = descending ? value[1..] : value;
                    if (sortField.Length == 0)
                    {
                        sortField = null;
                    }
                }

                continue;
            }

            if (string.Equals(key, "_fields", StringComparison.Ordinal))
            {
                fields.AddRange((rawValue ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                continue;
            }

            if (string.Equals(key, "limit", StringComparison.Ordinal)
                || string.Equals(key, "offset", StringComparison.Ordinal))
            {
                continue;
            }

            var (field, op) = SplitOperator(key);
            filters.Add(new ResourceFilter(field, op, rawValue ?? string.Empty));
        }

        return new ResourceQuery(filters, sortField, descending, fields);
    }

    private static (string Field, ResourceFilterOperator Operator) SplitOperator(string key)
    {
        var separator = key.LastIndexOf(':');
        if (separator <= 0)
        {
            return (key, ResourceFilterOperator.EqualTo);
        }

        return key[(separator + 1)..] switch
        {
            "contains" => (key[..separator], ResourceFilterOperator.Contains),
            "matches" => (key[..separator], ResourceFilterOperator.Matches),
            "absent" => (key[..separator], ResourceFilterOperator.Absent),
            _ => (key, ResourceFilterOperator.EqualTo),
        };
    }

    /// <summary>
    /// The documents this query selects, in its order. Filters combine with AND, which is what a query
    /// string means everywhere else and the only reading that does not need explaining.
    /// </summary>
    public IReadOnlyList<ResourceDocument> Apply(IReadOnlyList<ResourceDocument> documents)
    {
        var matched = Filters.Count == 0
            ? documents
            : [.. documents.Where(document => Filters.All(filter => Satisfies(document, filter)))];

        if (SortField is not { Length: > 0 } sort)
        {
            return matched;
        }

        // Ordered by the field's text, with documents that lack it last in either direction: a missing
        // value is not "smallest", it is absent, and burying it at the end is what a reader expects
        // whichever way they sorted. Numbers compare numerically when both sides are numbers, or
        // "10" would sort before "9".
        var withKey = matched.Select(d => (Document: d, Key: ResourceRelations.ReadKey(d.Body, sort))).ToList();
        var present = withKey.Where(e => e.Key is not null);
        var absent = withKey.Where(e => e.Key is null).Select(e => e.Document);

        var ordered = SortDescending
            ? present.OrderByDescending(e => e.Key, ResourceValueComparer.Instance)
            : present.OrderBy(e => e.Key, ResourceValueComparer.Instance);

        return [.. ordered.Select(e => e.Document), .. absent];
    }

    private static bool Satisfies(ResourceDocument document, ResourceFilter filter)
    {
        var value = ResourceRelations.ReadKey(document.Body, filter.Field);

        return filter.Operator switch
        {
            ResourceFilterOperator.Absent => string.Equals(filter.Value, "true", StringComparison.OrdinalIgnoreCase)
                ? value is null
                : value is not null,
            ResourceFilterOperator.Contains => value is not null
                && value.Contains(filter.Value, StringComparison.Ordinal),
            ResourceFilterOperator.Matches => value is not null && SafeMatch(value, filter.Value),
            _ => string.Equals(value, filter.Value, StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// A regex filter that cannot take the host down. The pattern comes from a query string, so it is
    /// caller-controlled: a timeout bounds catastrophic backtracking, and an invalid pattern matches
    /// nothing rather than throwing into the serving path.
    /// </summary>
    private static bool SafeMatch(string value, string pattern)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The document body reduced to the selected fields, or the body unchanged when none were asked
    /// for. A field the document does not carry is simply absent from the result rather than present
    /// and null — the shape a real API's summary endpoint returns.
    /// </summary>
    public string Project(string body)
    {
        if (Fields.Count == 0)
        {
            return body;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return body;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var field in Fields)
                {
                    if (document.RootElement.TryGetProperty(field, out var value))
                    {
                        writer.WritePropertyName(field);
                        value.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return body;
        }
    }
}

/// <summary>
/// Orders two field values the way a reader expects: numerically when both are numbers, otherwise as
/// text. Without this, <c>"10"</c> sorts before <c>"9"</c> — correct for strings and wrong for a
/// column of totals, which is what people sort by.
/// </summary>
internal sealed class ResourceValueComparer : IComparer<string?>
{
    public static ResourceValueComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (double.TryParse(x, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var left)
            && double.TryParse(y, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var right))
        {
            return left.CompareTo(right);
        }

        return string.CompareOrdinal(x, y);
    }
}
