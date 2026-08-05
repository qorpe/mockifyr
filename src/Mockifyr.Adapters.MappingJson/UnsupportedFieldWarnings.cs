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
/// One gap here is not about the dialect at all: a <c>publish</c> action on a host started without a
/// broker. Nothing in the mapping is wrong, and the stub is honoured in every other respect — it just
/// emits nothing, which looks exactly like a broker problem and is not one.
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
    /// <param name="mappingJson">The mapping document as it was submitted.</param>
    /// <param name="brokerConfigured">
    /// Whether this host has a broker to publish to. A <c>publish</c> action is honoured or not
    /// depending on how the host was started, not on anything in the mapping — so unlike every other
    /// gap here, whether it is a gap is the caller's to say.
    /// </param>
    public static IReadOnlyList<string> For(string mappingJson, bool brokerConfigured = true)
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
                    Inspect(mapping, brokerConfigured, warnings);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var mapping in root.EnumerateArray())
                {
                    Inspect(mapping, brokerConfigured, warnings);
                }
            }
            else
            {
                Inspect(root, brokerConfigured, warnings);
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

    private static void Inspect(JsonElement mapping, bool brokerConfigured, List<(string Kind, string Message)> warnings)
    {
        if (mapping.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!brokerConfigured && DeclaresPublish(mapping))
        {
            warnings.Add((
                "publish:no-broker",
                "a 'publish' post-serve action was accepted but this host has no broker — "
                + "such a stub serves its response and emits NOTHING. Start with --kafka-bootstrap."));
        }

        if (!mapping.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object)
        {
            return;
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

    /// <summary>Whether any post-serve action on this mapping is a <c>publish</c>.</summary>
    private static bool DeclaresPublish(JsonElement mapping)
    {
        if (!mapping.TryGetProperty("postServeActions", out var actions) ||
            actions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind == JsonValueKind.Object &&
                action.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                string.Equals(name.GetString(), "publish", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
