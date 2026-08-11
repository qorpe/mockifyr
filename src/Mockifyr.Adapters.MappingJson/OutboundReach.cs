using System.Text.Json;

namespace Mockifyr.Adapters.MappingJson;

/// <summary>
/// The fields through which a stub definition makes the <em>host</em> act on the network (#346):
/// <c>proxyBaseUrl</c> forwards the request to a target the stub names, and a post-serve action
/// (<c>postServeActions</c> / <c>serveEventListeners</c>) calls out after serving.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "may reach the network from this host" is not a property of a route set.
/// Refusing a principal on <c>/__admin/recordings</c> while the stub dialect still expresses the same
/// capability would give an operator a control that looks like it holds and does not — worse than no
/// control, because they would stop looking.
/// </para>
/// <para>
/// A detector rather than a policy: it reports what a definition declares and says nothing about who
/// may declare it. The host edge decides that, the same way it decides every other authorization
/// question, and Core never learns the concept exists.
/// </para>
/// </remarks>
public static class OutboundReach
{
    /// <summary>
    /// The distinct outward-reaching fields the payload declares, in a stable order; empty when it
    /// declares none. Accepts a single mapping or a <c>{"mappings":[…]}</c> bundle, because both
    /// shapes arrive on the same admin routes.
    /// </summary>
    /// <remarks>
    /// A payload that does not parse declares nothing. It is refused moments later by the reader that
    /// owns the dialect, with a better message than this could produce — and answering "denied" to
    /// malformed JSON would tell a caller their permissions are wrong when their syntax is.
    /// </remarks>
    public static IReadOnlyList<string> DeclaredBy(string mappingJson)
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var found = new SortedSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("mappings", out var bundle) && bundle.ValueKind == JsonValueKind.Array)
            {
                foreach (var mapping in bundle.EnumerateArray())
                {
                    Inspect(mapping, found);
                }
            }
            else
            {
                Inspect(root, found);
            }

            return [.. found];
        }
    }

    private static void Inspect(JsonElement mapping, SortedSet<string> found)
    {
        if (mapping.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Both spellings of the same capability — the adapter accepts either, so a check that knew
        // only one would be a check with a documented way around it.
        foreach (var key in (string[])["postServeActions", "serveEventListeners"])
        {
            if (mapping.TryGetProperty(key, out var actions)
                && actions.ValueKind == JsonValueKind.Array
                && actions.EnumerateArray().Any())
            {
                found.Add(key);
            }
        }

        if (mapping.TryGetProperty("response", out var response)
            && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("proxyBaseUrl", out var proxy)
            && proxy.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(proxy.GetString()))
        {
            found.Add("proxyBaseUrl");
        }
    }
}
