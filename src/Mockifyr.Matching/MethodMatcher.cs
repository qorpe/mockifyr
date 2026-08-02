using Mockifyr.Core;

namespace Mockifyr.Matching;

/// <summary>
/// Matches the HTTP method. A method of <c>ANY</c> matches every request (verified by the differential suite).
/// </summary>
public sealed class MethodMatcher(string expected) : IMatcher, IExactValueMatcher
{
    private readonly string _expected = expected;

    /// <summary>
    /// The method this matcher pins, for the engine's index (#265) — null for <c>ANY</c>, which
    /// matches every request and so belongs in no bucket.
    /// </summary>
    public string? ExactValue =>
        string.Equals(_expected, "ANY", StringComparison.OrdinalIgnoreCase) ? null : _expected;

    /// <inheritdoc />
    public MatchResult Match(MatchInput input)
    {
        if (string.Equals(_expected, "ANY", StringComparison.OrdinalIgnoreCase))
        {
            return MatchResult.Exact;
        }

        return string.Equals(input.Request.Method, _expected, StringComparison.OrdinalIgnoreCase)
            ? MatchResult.Exact
            : MatchResult.NoMatch(1d);
    }
}
