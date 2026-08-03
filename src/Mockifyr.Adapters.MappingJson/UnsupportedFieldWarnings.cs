using System.Text.Json;

namespace Mockifyr.Adapters.MappingJson;

/// <summary>
/// Finds fields in an imported mapping that Mockifyr accepts but does not act on, so they can be
/// reported instead of quietly doing nothing.
/// </summary>
/// <remarks>
/// <para>
/// The dialect is large and Mockifyr implements a validated subset; the deferred edges are listed in
/// the documentation. The failure mode that matters is not the gap itself — it is a gap you discover
/// from behaviour. A <c>bodyFileName</c> stub matched and returned an empty body, which reads as a
/// matching bug and is not one; a non-uniform <c>delayDistribution</c> produced no delay at all. Both
/// were documented and both were silent.
/// </para>
/// <para>
/// The stub is still imported. Refusing it would break importing a mapping set written for the
/// reference engine, which is the entire point of accepting the dialect — the goal is to be loud, not
/// to be strict.
/// </para>
/// </remarks>
public static class UnsupportedFieldWarnings
{
    /// <summary>
    /// Warnings for one mapping document — a single stub or a <c>{"mappings":[…]}</c> bundle. Empty
    /// when everything in it is honoured. Never throws: malformed JSON is the caller's error to
    /// report, and a warning pass must not be the thing that fails an import.
    /// </summary>
    public static IReadOnlyList<string> For(string mappingJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(mappingJson);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            var warnings = new List<(string Kind, string Message)>();

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("mappings", out var mappings) &&
                mappings.ValueKind == JsonValueKind.Array)
            {
                foreach (var mapping in mappings.EnumerateArray())
                {
                    Inspect(mapping, warnings);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var mapping in root.EnumerateArray())
                {
                    Inspect(mapping, warnings);
                }
            }
            else
            {
                Inspect(root, warnings);
            }

            // One line per KIND of gap, with a count — not one per stub. A 200-stub bundle whose
            // every response names a different file would otherwise produce 200 near-identical lines,
            // which is a wall nobody reads and which buries any other warning in the list. The fix is
            // the same for all of them anyway, so the file name is not what makes the message useful.
            var counted = new Dictionary<string, (string Message, int Stubs)>(StringComparer.Ordinal);
            foreach (var (kind, message) in warnings)
            {
                counted[kind] = counted.TryGetValue(kind, out var seen)
                    ? (seen.Message, seen.Stubs + 1)
                    : (message, 1);
            }

            // The count is appended only when it says something: "(1 stubs)" is noise, and wrong.
            return [.. counted.Values.Select(entry =>
                entry.Stubs > 1 ? $"{entry.Message} ({entry.Stubs} stubs)" : entry.Message)];
        }
    }

    private static void Inspect(JsonElement mapping, List<(string Kind, string Message)> warnings)
    {
        if (mapping.ValueKind != JsonValueKind.Object ||
            !mapping.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (response.TryGetProperty("bodyFileName", out var bodyFile) && bodyFile.ValueKind == JsonValueKind.String)
        {
            warnings.Add((
                "bodyFileName",
                "'bodyFileName' is not implemented — such a stub matches and returns its status with an "
                + "EMPTY body. Inline the body with 'body' or 'jsonBody' instead."));
        }

        if (response.TryGetProperty("delayDistribution", out var distribution) &&
            distribution.ValueKind == JsonValueKind.Object &&
            distribution.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            !string.Equals(type.GetString(), "uniform", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add((
                $"delayDistribution:{type.GetString()}",
                $"delayDistribution type '{type.GetString()}' is not implemented — only 'uniform' is. "
                + "Such a stub responds with NO delay. Use 'uniform' or 'fixedDelayMilliseconds'."));
        }
    }
}
