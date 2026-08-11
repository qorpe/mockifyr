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
public sealed record EnvironmentKey(string Key, string ActiveValue, IReadOnlyList<EnvironmentValue> Values)
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
    private static bool IsBareIdentifier(ReadOnlySpan<char> name)
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
