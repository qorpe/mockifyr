using System.Text.Json;
using Mockifyr.Core;

namespace Mockifyr.Adapters.MappingJson;

/// <summary>
/// Reads the optional <c>environments</c> section of an export bundle (issue #198) into the
/// domain model. The section is a Mockifyr extension of the mapping-bundle format — a sibling of
/// <c>mappings</c> carrying each key's values and active selection, in exactly the shape the admin
/// API serves (<c>{"key":…,"activeValue":…,"values":[{"name":…,"value":…}]}</c>) — so an export
/// re-imports as-is and restores the environments the stubs' <c>{{key}}</c> references depend on.
/// </summary>
public static class EnvironmentJsonReader
{
    /// <summary>
    /// Returns the environment keys of a bundle, or an empty list when the JSON is a bare mapping
    /// array, a single mapping, or a wrapper without an <c>environments</c> section (older exports —
    /// they keep importing unchanged). Entries missing a key name or a usable values array are
    /// dropped; a missing <c>activeValue</c> falls back to the first value, mirroring the admin PUT.
    /// </summary>
    public static IReadOnlyList<EnvironmentKey> Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // TryGetProperty stays out of compound conditions/ternaries throughout: a mutation-testing
        // run rewrites conditions wholesale, and an out-var bound inside one leaves the mutant
        // uncompilable — which voids the whole method's mutation score (Stryker "safe mode").
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var hasSection = root.TryGetProperty("environments", out var section);
        if (!hasSection || section.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var keys = new List<EnvironmentKey>();
        foreach (var entry in section.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var hasKey = entry.TryGetProperty("key", out var k);
            if (!hasKey || k.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var values = new List<EnvironmentValue>();
            var hasValues = entry.TryGetProperty("values", out var array);
            if (hasValues && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var hasName = item.TryGetProperty("name", out var n);
                    var hasValue = item.TryGetProperty("value", out var v);
                    var name = hasName ? n.GetString() : null;
                    var value = hasValue ? v.GetString() : null;
                    if (name is not null && value is not null)
                    {
                        values.Add(new EnvironmentValue(name, value));
                    }
                }
            }

            if (values.Count == 0)
            {
                continue;
            }

            var hasActive = entry.TryGetProperty("activeValue", out var a);
            var active = hasActive ? a.GetString() : null;
            keys.Add(new EnvironmentKey(k.GetString()!, active ?? values[0].Name, values));
        }

        return keys;
    }
}
