using System.Text;

namespace Mockifyr.Core;

/// <summary>
/// One named value of an environment key, e.g. <c>dev</c> → <c>https://dev.example.com</c> (G17).
/// </summary>
/// <param name="Name">The value's name, unique within its key.</param>
/// <param name="Value">The literal substituted into a stub when this value is active.</param>
/// <param name="Secret">
/// Whether the literal is withheld from every surface that reports it (#348). A sandbox handed to
/// partners is exactly where a webhook signing secret or a partner token ends up, and the admin API,
/// the dashboard and export bundles are the artefacts people paste into tickets and commit to
/// repositories. Serve-time resolution is unaffected — a secret nobody can use is not a feature.
/// </param>
public sealed record EnvironmentValue(string Name, string Value, bool Secret = false);

/// <summary>
/// An environment key and its selectable values (G17, issue #165). Each key carries its own active
/// value, independently of every other key — so <c>baseUrl</c> can point at <c>dev</c> while
/// <c>region</c> points at <c>eu-west</c>, without the all-or-nothing "switch the whole environment"
/// model this replaces.
/// </summary>
/// <param name="Key">The identifier referenced from a stub as <c>{{Key}}</c>.</param>
/// <param name="ActiveValue">The name of the value currently in effect.</param>
/// <param name="Values">Every selectable value, in display order.</param>
/// <param name="Constant">
/// Whether this key deliberately does not vary (#352). A constant holds exactly one value and offers
/// no switch, which is the difference between "this is fixed" and "this happens to have one option so
/// far" — a distinction the model could not previously make at all.
/// </param>
public sealed record EnvironmentKey(
    string Key, string ActiveValue, IReadOnlyList<EnvironmentValue> Values, bool Constant = false)
{
    /// <summary>
    /// The literal the key currently resolves to, or <c>null</c> when <see cref="ActiveValue"/> names
    /// no existing value (which a delete of the active value can produce).
    /// </summary>
    public string? Resolve()
    {
        foreach (var value in Values)
        {
            if (string.Equals(value.Name, ActiveValue, StringComparison.Ordinal))
            {
                return value.Value;
            }
        }

        return null;
    }

    /// <summary>Whether the value currently in effect is a secret — so a reporting surface withholds it.</summary>
    public bool ResolvesToSecret()
    {
        foreach (var value in Values)
        {
            if (string.Equals(value.Name, ActiveValue, StringComparison.Ordinal))
            {
                return value.Secret;
            }
        }

        return false;
    }
}

/// <summary>
/// How a secret environment value survives a read-modify-write (#348).
/// </summary>
/// <remarks>
/// Redacting on read creates a hazard that redaction alone does not solve: the dashboard reads a key
/// (secret withheld), the operator renames one value, and the write sends back what it was shown —
/// which no longer contains the secret. Taken literally that stores an empty string, so opening a
/// screen and pressing save would destroy a credential without anyone touching it.
/// <para>
/// A submitted value that is marked secret and carries no literal therefore means "unchanged", and
/// resolves against what is already stored. Sending an explicit literal still replaces it, so a
/// deliberate rotation works; a value that is new and secret with no literal is dropped rather than
/// stored empty, because an empty secret is a stub that silently signs with nothing.
/// </para>
/// </remarks>
public static class EnvironmentSecrets
{
    /// <summary>
    /// Resolves a submitted key against the stored one, carrying forward the literal of every secret
    /// the submission withheld. <paramref name="stored"/> is null when the key is being created.
    /// </summary>
    public static EnvironmentKey Merge(EnvironmentKey submitted, EnvironmentKey? stored)
    {
        var merged = new List<EnvironmentValue>(submitted.Values.Count);
        foreach (var value in submitted.Values)
        {
            if (!value.Secret || value.Value.Length > 0)
            {
                merged.Add(value);
                continue;
            }

            var existing = stored?.Values.FirstOrDefault(
                v => string.Equals(v.Name, value.Name, StringComparison.Ordinal));

            // A withheld secret for a value we have never seen carries nothing to preserve. Storing it
            // as an empty string would leave a stub signing with nothing and reporting success.
            if (existing is { Secret: true, Value.Length: > 0 })
            {
                merged.Add(existing);
            }
        }

        return submitted with { Values = merged };
    }
}

/// <summary>
/// Substitutes <c>{{key}}</c> references with the tenant's currently active environment values (G17).
/// <para>
/// This runs as a pass <em>before</em> Handlebars, and deliberately replaces <b>only</b> names that
/// are defined keys for the tenant. Everything else — <c>{{now}}</c>, <c>{{request.path}}</c>,
/// <c>{{#each}}</c> — is left byte-identical for the template engine to handle. That is what makes a
/// bare <c>{{key}}</c> surface safe to share with the Handlebars namespace: an undefined name is never
/// touched, so it cannot shadow a helper, and a defined one cannot be mistaken for a template
/// expression. Collisions are prevented at the write end instead, where creating a key named after a
/// built-in helper is rejected (see <see cref="ReservedEnvironmentKeys"/>).
/// </para>
/// <para>Pure and allocation-free when the input carries no reference — the engine stays I/O-free.</para>
/// </summary>
public static class EnvironmentSubstitution
{
    /// <summary>
    /// Replaces every <c>{{key}}</c> whose name <paramref name="lookup"/> resolves. Returns the input
    /// unchanged (same instance) when there is nothing to substitute.
    /// </summary>
    /// <param name="input">The raw template text, exactly as stored on the stub.</param>
    /// <param name="lookup">Resolves a key name to its active value, or returns false to leave it alone.</param>
    public static string Apply(string input, TryResolve lookup)
    {
        // Cheap rejection first: this runs on every body, header and URL of every served request.
        if (string.IsNullOrEmpty(input) || input.IndexOf("{{", StringComparison.Ordinal) < 0)
        {
            return input;
        }

        StringBuilder? builder = null;
        var copiedTo = 0;
        var index = 0;

        while (index < input.Length)
        {
            var open = input.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var close = input.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            var name = input.AsSpan(open + 2, close - open - 2).Trim();
            if (IsBareIdentifier(name) && lookup(name.ToString(), out var value))
            {
                builder ??= new StringBuilder(input.Length);
                builder.Append(input, copiedTo, open - copiedTo).Append(value);
                copiedTo = close + 2;
            }

            index = close + 2;
        }

        if (builder is null)
        {
            return input;
        }

        return builder.Append(input, copiedTo, input.Length - copiedTo).ToString();
    }

    /// <summary>Resolves an environment key name to its active value.</summary>
    public delegate bool TryResolve(string key, out string value);

    /// <summary>
    /// True for names that could be an environment key: a leading letter or underscore followed by
    /// letters, digits, underscores or hyphens. This deliberately excludes anything with a dot, a
    /// space, or an argument — i.e. every Handlebars construct (<c>{{request.path}}</c>,
    /// <c>{{random 'X.y'}}</c>, <c>{{#if x}}</c>) is rejected before the lookup even runs.
    /// </summary>
    /// <summary>
    /// Whether the text between the braces is a bare key name — no dots, no spaces, no helper syntax.
    /// </summary>
    /// <remarks>Internal rather than private since #352: composition needs the same rule to find references.</remarks>
    internal static bool IsBareIdentifier(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || (!char.IsLetter(name[0]) && name[0] != '_'))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Names an environment key may not take, because a stub referencing <c>{{name}}</c> would otherwise
/// silently shadow the built-in templating helper of the same name (G17). Rejecting at create time is
/// what keeps <see cref="EnvironmentSubstitution"/>'s bare-identifier surface unambiguous — the
/// alternative, letting a key named <c>now</c> exist, produces a stub whose <c>{{now}}</c> stops
/// returning a timestamp for reasons nothing in the stub explains.
/// </summary>
public static class ReservedEnvironmentKeys
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        // Request model + extraction
        "request", "originalRequest", "jsonPath", "xPath", "soapXPath", "hostname",
        // Data / JSON
        "parseJson", "toJson", "pickRandom", "size",
        // String
        "trim", "capitalize", "upper", "lower", "abbreviate", "substring", "replace", "join", "split",
        "stringJoiner", "truncate", "padLeft", "padRight",
        // Number & math
        "add", "subtract", "multiply", "divide", "round", "abs", "floor", "ceil", "max", "min",
        // Random / faker / identity
        "random", "randomValue", "randomInt", "randomDecimal", "uuid", "jwt", "jwks",
        // Date & time
        "now", "date", "dateFormat", "parseDate", "unixEpoch",
        // Encoding
        "base64", "urlEncode", "urlDecode", "formData", "hash",
        // Block helpers and built-ins
        "if", "unless", "each", "with", "eq", "neq", "gt", "lt", "gte", "lte", "and", "or", "not",
        "this", "else", "lookup", "log",
    };

    /// <summary>True when <paramref name="key"/> collides with a built-in helper name.</summary>
    public static bool IsReserved(string key) => Reserved.Contains(key);

    /// <summary>
    /// True when <paramref name="key"/> is shaped like a key <see cref="EnvironmentSubstitution"/> can
    /// actually substitute. A key that fails this would be stored but never resolve.
    /// </summary>
    public static bool IsWellFormed(string key)
    {
        if (string.IsNullOrEmpty(key) || (!char.IsLetter(key[0]) && key[0] != '_'))
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}


/// <summary>
/// Values that resolve values (#352): composition, its bound, and the cycle check that keeps it safe.
/// </summary>
/// <remarks>
/// <para>
/// The gap: substitution walked the text once, so a value containing <c>{{otherKey}}</c> was never
/// resolved. The host name therefore had to be copied into every value that needed it, and changing it
/// meant changing all of them together — exactly the class of edit people get wrong.
/// </para>
/// <para>
/// Bounded and checked at <b>write</b> time. A cycle discovered at serve time is a hung request on
/// somebody's demo; discovered at write time it is a message naming the two keys involved.
/// </para>
/// </remarks>
public static class EnvironmentComposition
{
    /// <summary>
    /// How many times a value may resolve through another before this stops.
    /// </summary>
    /// <remarks>
    /// Ten is far past anything legible — <c>a → b → c</c> is already the edge of what a person can
    /// hold — and it exists so a cycle that somehow evaded the write-time check still cannot hang a
    /// request.
    /// </remarks>
    public const int MaxDepth = 10;

    /// <summary>
    /// The literal a key resolves to once composition is applied, or null when the key is unknown.
    /// </summary>
    /// <param name="key">The key being resolved.</param>
    /// <param name="lookup">Resolves any other key's raw (uncomposed) literal.</param>
    public static string? Resolve(string key, Func<string, string?> lookup)
    {
        var raw = lookup(key);
        if (raw is null)
        {
            return null;
        }

        var text = raw;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var expanded = EnvironmentSubstitution.Apply(text, (string name, out string value) =>
            {
                // A self-reference resolves to nothing rather than to itself: expanding it would be the
                // one substitution guaranteed never to terminate.
                var resolved = string.Equals(name, key, StringComparison.Ordinal) ? null : lookup(name);
                value = resolved ?? string.Empty;
                return resolved is not null;
            });

            if (ReferenceEquals(expanded, text) || string.Equals(expanded, text, StringComparison.Ordinal))
            {
                return expanded;
            }

            text = expanded;
        }

        // The depth bound was reached, which means something references its way in a loop the write-time
        // check did not see. Returning what we have beats hanging, and the check below is what keeps
        // this from being reachable in practice.
        return text;
    }

    /// <summary>
    /// The reference cycle <paramref name="candidate"/> would create among <paramref name="existing"/>,
    /// as the chain of key names, or null when it creates none.
    /// </summary>
    /// <remarks>
    /// Returns the path rather than a boolean: "a references b references a" is actionable, and "there
    /// is a cycle" is a puzzle handed back to the person who just made one.
    /// </remarks>
    public static IReadOnlyList<string>? FindCycle(EnvironmentKey candidate, IReadOnlyList<EnvironmentKey> existing)
    {
        var byName = existing
            .Where(key => !string.Equals(key.Key, candidate.Key, StringComparison.Ordinal))
            .ToDictionary(key => key.Key, StringComparer.Ordinal);
        byName[candidate.Key] = candidate;

        var path = new List<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        return Walk(candidate.Key, byName, visiting, path) ? path : null;
    }

    private static bool Walk(
        string name,
        Dictionary<string, EnvironmentKey> byName,
        HashSet<string> visiting,
        List<string> path)
    {
        path.Add(name);
        if (!visiting.Add(name))
        {
            return true;
        }

        if (byName.TryGetValue(name, out var key) && key.Resolve() is { } literal)
        {
            foreach (var reference in References(literal))
            {
                if (Walk(reference, byName, visiting, path))
                {
                    return true;
                }
            }
        }

        visiting.Remove(name);
        path.RemoveAt(path.Count - 1);
        return false;
    }

    /// <summary>Every <c>{{key}}</c> named in a literal, in order of appearance.</summary>
    public static IReadOnlyList<string> References(string literal)
    {
        var names = new List<string>();
        var index = 0;
        while (index < literal.Length)
        {
            var open = literal.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0) break;
            var close = literal.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) break;

            var name = literal.AsSpan(open + 2, close - open - 2).Trim();
            if (EnvironmentSubstitution.IsBareIdentifier(name))
            {
                names.Add(name.ToString());
            }

            index = close + 2;
        }

        return names;
    }

    /// <summary>
    /// Whether the composed value of <paramref name="key"/> carries a secret — its own or one it
    /// references (#348 + #352).
    /// </summary>
    /// <remarks>
    /// Secrecy is contagious by necessity. If <c>authHeader = Bearer {{apiToken}}</c> and
    /// <c>apiToken</c> is secret, then reading <c>authHeader</c> reads the secret — so composition
    /// would otherwise be a way around redaction rather than a convenience.
    /// </remarks>
    public static bool ResolvesToSecret(string key, Func<string, EnvironmentKey?> lookup, int depth = 0)
    {
        if (depth >= MaxDepth || lookup(key) is not { } entry)
        {
            return false;
        }

        if (entry.ResolvesToSecret())
        {
            return true;
        }

        return entry.Resolve() is { } literal
            && References(literal).Any(reference =>
                !string.Equals(reference, key, StringComparison.Ordinal)
                && ResolvesToSecret(reference, lookup, depth + 1));
    }
}

/// <summary>
/// Values shared by every tenant, resolved when a tenant has not defined the key itself (#352).
/// </summary>
/// <remarks>
/// The sandbox's own base URL, a shared test IBAN, a common certificate thumbprint: copied into every
/// tenant before this, so a new tenant began by re-entering them and an update left the others stale.
/// A tenant's own key always wins — an override that were impossible would turn a convenience into a
/// constraint.
/// </remarks>
public sealed class HostEnvironment(IReadOnlyList<EnvironmentKey> keys)
{
    private readonly Dictionary<string, EnvironmentKey> _keys =
        keys.ToDictionary(key => key.Key, StringComparer.Ordinal);

    /// <summary>Nothing shared — what every host meant before this existed.</summary>
    public static HostEnvironment Empty { get; } = new([]);

    /// <summary>The shared keys, name-ordered.</summary>
    public IReadOnlyList<EnvironmentKey> Keys => [.. _keys.Values.OrderBy(key => key.Key, StringComparer.Ordinal)];

    /// <summary>One shared key, or null.</summary>
    public EnvironmentKey? Get(string key) => _keys.GetValueOrDefault(key);

    /// <summary>
    /// Parses <c>key=value</c> pairs from the command line into shared constants.
    /// </summary>
    /// <remarks>
    /// Constants, not choices: a value declared on the command line has exactly one form and no way to
    /// switch it at runtime, and saying so in the model is more honest than presenting a selector with
    /// one option.
    /// </remarks>
    public static HostEnvironment Parse(IEnumerable<string> pairs)
    {
        var keys = new List<EnvironmentKey>();
        foreach (var pair in pairs)
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = pair[..separator].Trim();
            if (!ReservedEnvironmentKeys.IsWellFormed(name) || ReservedEnvironmentKeys.IsReserved(name))
            {
                continue;
            }

            keys.Add(new EnvironmentKey(name, "shared", [new EnvironmentValue("shared", pair[(separator + 1)..])], Constant: true));
        }

        return new HostEnvironment(keys);
    }
}
