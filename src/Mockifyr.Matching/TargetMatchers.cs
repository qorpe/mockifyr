using System.Text;
using Mockifyr.Core;

namespace Mockifyr.Matching;

/// <summary>Applies a value matcher to a named request header.</summary>
public sealed class HeaderMatcher(string name, IValueMatcher value) : IMatcher, INamedTargetMatcher
{
    /// <inheritdoc />
    public string TargetName => name;

    /// <inheritdoc />
    public MatchResult Match(MatchInput input)
    {
        var present = input.Request.Headers.Contains(name);
        var values = present ? [.. input.Request.Headers[name]] : Array.Empty<string>();
        return value.Match(present, values);
    }
}

/// <summary>Applies a value matcher to a named query parameter.</summary>
public sealed class QueryMatcher(string name, IValueMatcher value) : IMatcher, INamedTargetMatcher
{
    /// <inheritdoc />
    public string TargetName => name;

    /// <inheritdoc />
    public MatchResult Match(MatchInput input)
    {
        var present = input.Request.Query.Contains(name);
        var values = present ? [.. input.Request.Query[name]] : Array.Empty<string>();
        return value.Match(present, values);
    }
}

/// <summary>
/// Applies a value matcher to a named form parameter (the <c>formParameters</c> stub field). The
/// <c>application/x-www-form-urlencoded</c> request body is parsed into parameters the same way a query
/// string is; a non-form body yields none.
/// </summary>
public sealed class FormParameterMatcher(string name, IValueMatcher value) : IMatcher, INamedTargetMatcher
{
    /// <inheritdoc />
    public string TargetName => name;

    private static readonly ILookup<string, string> Empty =
        Array.Empty<KeyValuePair<string, string>>().ToLookup(pair => pair.Key, pair => pair.Value);

    /// <inheritdoc />
    public MatchResult Match(MatchInput input)
    {
        var form = ParseForm(input.Request);
        var present = form.Contains(name);
        var values = present ? [.. form[name]] : Array.Empty<string>();
        return value.Match(present, values);
    }

    private static ILookup<string, string> ParseForm(CanonicalRequest request)
    {
        var contentType = request.Headers["Content-Type"].FirstOrDefault() ?? string.Empty;
        if (request.Body.Length == 0 ||
            !contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return Empty;
        }

        return Encoding.UTF8.GetString(request.Body)
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                return separator >= 0
                    ? new KeyValuePair<string, string>(Decode(pair[..separator]), Decode(pair[(separator + 1)..]))
                    : new KeyValuePair<string, string>(Decode(pair), string.Empty);
            })
            .ToLookup(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}

/// <summary>Applies a value matcher to a named cookie.</summary>
public sealed class CookieMatcher(string name, IValueMatcher value) : IMatcher, INamedTargetMatcher
{
    /// <inheritdoc />
    public string TargetName => name;

    /// <inheritdoc />
    public MatchResult Match(MatchInput input)
    {
        var present = input.Request.Cookies.TryGetValue(name, out var cookie);
        var values = present ? new[] { cookie! } : Array.Empty<string>();
        return value.Match(present, values);
    }
}

/// <summary>
/// Matches the request body against an exact byte sequence (the <c>binaryEqualTo</c> body pattern).
/// Comparison is byte-for-byte, so it is correct for non-text payloads.
/// </summary>
public sealed class BinaryEqualToBodyMatcher(byte[] expected) : IMatcher
{
    /// <inheritdoc />
    public MatchResult Match(MatchInput input) =>
        input.Request.Body.AsSpan().SequenceEqual(expected) ? MatchResult.Exact : MatchResult.NoMatch(1d);
}

/// <summary>Applies a value matcher to the request body.</summary>
/// <remarks>
/// An empty request body is treated as absent for body matching, verified by the differential
/// suite: <c>bodyPatterns equalTo ""</c> does not match an empty body. See
/// docs/parity/g1-matching.md.
/// </remarks>
public sealed class BodyMatcher(IValueMatcher value) : IMatcher
{
    /// <inheritdoc />
    public MatchResult Match(MatchInput input)
    {
        if (input.Request.Body.Length == 0)
        {
            return value.Match(present: false, Array.Empty<string>());
        }

        var text = System.Text.Encoding.UTF8.GetString(input.Request.Body);
        return value.Match(present: true, [text]);
    }
}
