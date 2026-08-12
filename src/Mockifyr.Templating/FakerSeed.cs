using Bogus;

namespace Mockifyr.Templating;

/// <summary>
/// Publishes a seeded generator for the Faker helpers, for the life of a scope (#351).
/// </summary>
/// <remarks>
/// <para>
/// A dataset that produced different customers every time could not be the basis of a regression
/// test, which is most of why anybody wants one. So a load may fix the seed and get the same two
/// hundred customers every time.
/// </para>
/// <para>
/// Deliberately <em>not</em> Bogus's global <c>Randomizer.Seed</c>. That is a static shared by the
/// whole process, so seeding it for a load would also make every concurrently served response
/// deterministic — and two overlapping loads would draw from each other's sequence. An ambient scope
/// is the same idiom <see cref="RenderClock"/> already uses for the tenant clock, and it keeps the
/// effect inside the operation that asked for it.
/// </para>
/// </remarks>
public static class FakerSeed
{
    [ThreadStatic]
    private static Faker? _current;

    /// <summary>The generator helpers should use: the scoped one, or a fresh unseeded one.</summary>
    internal static Faker Current => _current ?? new Faker();

    /// <summary>
    /// Publishes a generator seeded with <paramref name="seed"/> for the life of the returned scope.
    /// A null seed means "no opinion" and leaves helpers on fresh, unseeded generators.
    /// </summary>
    public static Scope Use(int? seed) => new(seed);

    /// <summary>Restores whatever was published before, so nested renders behave.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly Faker? _previous;

        internal Scope(int? seed)
        {
            _previous = _current;
            // One generator for the whole scope, not one per value: a seed is only reproducible if the
            // draws come from a single sequence, and a fresh Faker per expression would hand every
            // document the same "random" name.
            _current = seed is { } value ? new Faker { Random = new Randomizer(value) } : null;
        }

        public void Dispose() => _current = _previous;
    }
}
