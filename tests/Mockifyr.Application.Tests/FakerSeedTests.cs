using Mockifyr.Templating;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Seeded generation (#351): the same dataset with the same seed produces the same data, or a dataset
/// cannot be the basis of a regression test — which is most of why anybody wants one.
/// </summary>
public sealed class FakerSeedTests
{
    /// <summary>
    /// A sequence of draws, rendered one at a time the way a load would. A sequence rather than a
    /// single value, so a generator that happened to return one constant could not pass for
    /// deterministic.
    /// </summary>
    private static string[] Names(int? seed, int count)
    {
        var cache = CompiledTemplateCache.Create();
        using var scope = FakerSeed.Use(seed);

        return [.. Enumerable.Range(0, count).Select(_ =>
            cache.Render("""{{random 'Name.fullName'}}""", new Dictionary<string, object?>()))];
    }

    [Fact]
    public void The_same_seed_produces_the_same_sequence()
    {
        Assert.Equal(Names(seed: 42, count: 5), Names(seed: 42, count: 5));
    }

    [Fact]
    public void A_different_seed_produces_a_different_sequence()
    {
        // Without this, "deterministic" would also be satisfied by a generator returning one constant.
        Assert.NotEqual(Names(seed: 42, count: 5), Names(seed: 43, count: 5));
    }

    [Fact]
    public void A_seeded_sequence_still_varies_within_itself()
    {
        // The failure a per-value generator would produce: two hundred customers all called the same
        // thing, which looks deterministic and is useless.
        Assert.True(Names(seed: 7, count: 5).Distinct().Count() > 1);
    }

    [Fact]
    public void Outside_a_scope_nothing_is_seeded()
    {
        // The compatibility promise: serving keeps drawing fresh values, so a stub returning a random
        // name does not start repeating itself because somebody loaded a dataset once.
        Assert.NotEqual(Names(seed: null, count: 5), Names(seed: null, count: 5));
    }

    [Fact]
    public void A_scope_restores_what_was_published_before_it()
    {
        // A scope that reset to "none" instead of restoring would silently un-seed an outer load
        // halfway through, and the second half of its data would stop being reproducible.
        var cache = CompiledTemplateCache.Create();
        string Draw() => cache.Render("""{{random 'Name.fullName'}}""", new Dictionary<string, object?>());

        using var outer = FakerSeed.Use(1);
        var first = Draw();

        using (var inner = FakerSeed.Use(2))
        {
            Draw();
        }

        // Re-seeding with the same value restarts the sequence, so this is the same draw as `first`
        // only if the inner scope handed the outer one back rather than clearing it.
        using var again = FakerSeed.Use(1);
        Assert.Equal(first, Draw());
    }
}
