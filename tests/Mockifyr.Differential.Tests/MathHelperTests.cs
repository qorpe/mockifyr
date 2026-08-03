using Mockifyr.Differential.Generator;
using Mockifyr.Differential.Harness;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential coverage for the <c>math</c> helper. Added after probing turned up two things the
/// documentation had wrong: the reference engine <em>does</em> support <c>%</c>, and its integer
/// division rounds half AWAY FROM ZERO — so <c>-9/2</c> is <c>-5</c>, where Mockifyr answered
/// <c>-4</c>. Every negative case here is one the suite previously had no example of.
/// </summary>
public sealed class MathHelperTests : IAsyncLifetime
{
    private readonly DifferentialRunner _runner = new();

    public Task InitializeAsync() => _runner.StartAsync();

    public async Task DisposeAsync() => await _runner.DisposeAsync();

    private async Task VerifyAsync(string expression)
    {
        var mapping =
            "{\"request\":{\"method\":\"GET\",\"urlPath\":\"/math\"}," +
            "\"response\":{\"status\":200,\"transformers\":[\"response-template\"]," +
            "\"body\":\"" + expression + "\"}}";

        var outcome = await _runner.RunAsync(mapping, new RequestSpec { Method = "GET", Url = "/math" });
        Assert.True(outcome.Diff.IsMatch, $"{expression}: {outcome.Diff}");
    }

    [Theory]
    [InlineData("{{math 2 '+' 3}}")]
    [InlineData("{{math 9 '-' 4}}")]
    [InlineData("{{math 2 '*' 3}}")]
    [InlineData("{{math 2.5 '*' 2}}")]
    [InlineData("{{math -3 '+' -4}}")]
    public Task Arithmetic_matches_the_oracle(string expression) => VerifyAsync(expression);

    [Theory]
    [InlineData("{{math 7 '/' 2}}")]      // 3.5 → 4
    [InlineData("{{math 8 '/' 3}}")]      // 2.67 → 3
    [InlineData("{{math 5 '/' 2}}")]      // 2.5 → 3
    [InlineData("{{math -7 '/' 2}}")]     // -3.5 → -4, NOT -3
    [InlineData("{{math -9 '/' 2}}")]     // -4.5 → -5, NOT -4
    [InlineData("{{math -8 '/' 3}}")]     // -2.67 → -3
    [InlineData("{{math 7.0 '/' 2}}")]    // a decimal operand does not round at all
    public Task Integer_division_rounds_half_away_from_zero(string expression) => VerifyAsync(expression);

    [Theory]
    [InlineData("{{math 7 '%' 2}}")]
    [InlineData("{{math 7.5 '%' 2}}")]
    [InlineData("{{math -7 '%' 2}}")]     // the sign follows the dividend
    [InlineData("{{math 7 '%' 2.5}}")]
    public Task Modulo_matches_the_oracle(string expression) =>
        // The helper used to reject '%' outright, with a comment claiming the oracle did not support
        // it. The oracle answers 1.
        VerifyAsync(expression);
}
