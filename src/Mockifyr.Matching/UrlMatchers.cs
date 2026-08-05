using System.Text;
using System.Text.RegularExpressions;
using Mockifyr.Core;

namespace Mockifyr.Matching;

/// <summary>
/// Matches the full request URL (path plus query string) for exact equality.
/// Maps from the imported JSON <c>request.url</c> field.
/// </summary>
public sealed class UrlEqualToMatcher(string expected) : IMatcher, INamedTargetMatcher
{
    /// <inheritdoc />
    public string TargetName => "url";

    private readonly string _expected = expected;

    /// <inheritdoc />
    public MatchResult Match(MatchInput input) =>
        string.Equals(input.Request.Url, _expected, StringComparison.Ordinal)
            ? MatchResult.Exact
            : MatchResult.NoMatch(1d);
}

/// <summary>
/// Matches the request path (ignoring the query string) for exact equality.
/// Maps from the imported JSON <c>request.urlPath</c> field.
/// </summary>
public sealed class UrlPathEqualToMatcher(string expected) : IMatcher, IExactValueMatcher, INamedTargetMatcher
{
    /// <inheritdoc />
    public string TargetName => "urlPath";

    private readonly string _expected = expected;

    /// <summary>
    /// The path this matcher pins, for the engine's index (#265). Note that
    /// <see cref="UrlEqualToMatcher"/> deliberately does NOT implement this: it pins path plus query
    /// string, which the index's path-based key cannot reproduce.
    /// </summary>
    public string? ExactValue => _expected;

    /// <inheritdoc />
    public MatchResult Match(MatchInput input) =>
        string.Equals(input.Request.Path, _expected, StringComparison.Ordinal)
            ? MatchResult.Exact
            : MatchResult.NoMatch(1d);
}

/// <summary>
/// Matches the full request URL (path plus query string) against a regular expression. The imported
/// JSON <c>request.urlPattern</c> (urlMatching) field uses Java <c>matches</c> semantics, so the
/// pattern is anchored to the whole URL. Anchoring behavior is verified by the differential suite.
/// </summary>
public sealed class UrlPatternMatcher(string pattern) : IMatcher, INamedTargetMatcher
{
    /// <inheritdoc />
    public string TargetName => "urlPattern";

    private readonly Regex _regex = new($@"\A(?:{pattern})\z", RegexOptions.None);

    /// <inheritdoc />
    public MatchResult Match(MatchInput input) =>
        _regex.IsMatch(input.Request.Url) ? MatchResult.Exact : MatchResult.NoMatch(1d);
}

/// <summary>
/// Matches the request path (ignoring the query string) against a regular expression. The imported
/// JSON <c>request.urlPathPattern</c> (urlPathMatching) field is a whole-path match, so the pattern is
/// anchored. Anchoring behavior is verified by the differential suite.
/// </summary>
public sealed class UrlPathPatternMatcher(string pattern) : IMatcher, INamedTargetMatcher
{
    /// <inheritdoc />
    public string TargetName => "urlPathPattern";

    private readonly Regex _regex = new($@"\A(?:{pattern})\z", RegexOptions.None);

    /// <inheritdoc />
    public MatchResult Match(MatchInput input) =>
        _regex.IsMatch(input.Request.Path) ? MatchResult.Exact : MatchResult.NoMatch(1d);
}

/// <summary>
/// Matches the request path against a URI template (imported JSON <c>request.urlPathTemplate</c>). Each
/// <c>{var}</c> placeholder matches exactly one path segment; the query string is ignored. Named
/// path-variable extraction (for templating) arrives with G2b; here only the match decision is used.
/// </summary>
public sealed class UrlPathTemplateMatcher : IMatcher, INamedTargetMatcher
{
    /// <inheritdoc />
    public string TargetName => "urlPathTemplate";

    private readonly Regex _regex;

    /// <summary>Compiles the template into an anchored path regex.</summary>
    public UrlPathTemplateMatcher(string template)
    {
        var builder = new StringBuilder();
        var last = 0;
        foreach (Match token in Regex.Matches(template, @"\{[^/{}]+\}"))
        {
            builder.Append(Regex.Escape(template[last..token.Index]));
            builder.Append("[^/]+"); // a single non-empty path segment
            last = token.Index + token.Length;
        }

        builder.Append(Regex.Escape(template[last..]));
        _regex = new Regex($@"\A(?:{builder})\z", RegexOptions.None);
    }

    /// <inheritdoc />
    public MatchResult Match(MatchInput input) =>
        _regex.IsMatch(input.Request.Path) ? MatchResult.Exact : MatchResult.NoMatch(1d);
}
