namespace Mockifyr.Core;

/// <summary>
/// A matcher that pins one exact value, letting the engine index on it (#265).
/// </summary>
/// <remarks>
/// Implemented only by equality matchers. A matcher that does not implement this — a regex, a URI
/// template, anything with alternatives — is simply not indexable, and the index falls back to
/// considering it for every request. That default is what keeps the optimization safe: forgetting to
/// implement this interface costs performance, never correctness.
/// </remarks>
public interface IExactValueMatcher
{
    /// <summary>The single value this matcher accepts, or null when it accepts more than one.</summary>
    string? ExactValue { get; }
}

/// <summary>
/// Narrows a tenant's stubs to those that could possibly match a request, so matching does not
/// evaluate every stub in the store (#265).
/// </summary>
/// <remarks>
/// <para>
/// Measured before this existed: matching the last of 1000 stubs cost 29 µs and allocated 94.8 KB,
/// against 378 ns and 1.14 KB for a single stub — matching was O(stubs) in both time and garbage.
/// </para>
/// <para>
/// The index is <b>conservative by construction</b>: a stub goes into a specific bucket only when its
/// method and path matchers pin exact values, and everything else goes into a bucket consulted on
/// every request. So the candidate set is always a superset of the stubs that can match, and the
/// engine's own evaluation still decides. An index bug can make matching slower; it cannot make it
/// wrong.
/// </para>
/// <para>
/// Candidates keep their position in the store, because the engine breaks priority ties by insertion
/// order. Returning them in a different order would silently change which stub wins.
/// </para>
/// </remarks>
public sealed class StubIndex
{
    private readonly Dictionary<string, List<Entry>> _byMethodAndPath;
    private readonly List<Entry> _unindexed;
    private readonly int _count;

    private readonly record struct Entry(StubMapping Stub, int Position);

    /// <summary>Builds an index over the stubs in store order.</summary>
    public StubIndex(IReadOnlyList<StubMapping> stubs)
    {
        _byMethodAndPath = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        _unindexed = [];
        _count = stubs.Count;

        for (var position = 0; position < stubs.Count; position++)
        {
            var stub = stubs[position];
            var entry = new Entry(stub, position);
            if (KeyFor(stub) is { } key)
            {
                if (!_byMethodAndPath.TryGetValue(key, out var bucket))
                {
                    _byMethodAndPath[key] = bucket = [];
                }

                bucket.Add(entry);
            }
            else
            {
                _unindexed.Add(entry);
            }
        }
    }

    /// <summary>How many stubs the index was built over — used to detect that the store has changed.</summary>
    public int Count => _count;

    /// <summary>
    /// The stubs that could match this request, in store order. Callers must still evaluate each one:
    /// this narrows the field, it does not decide the match.
    /// </summary>
    public IReadOnlyList<StubMapping> Candidates(CanonicalRequest request)
    {
        var indexed = _byMethodAndPath.TryGetValue(Key(request.Method, request.Path), out var bucket)
            ? bucket
            : [];

        // The common shape of a busy host: many stubs, all indexable, and one bucket for this exact
        // method and path. Returning the bucket straight is what keeps the hot path light — the sort
        // below is correct for this case too, so this branch is an optimization only, and the
        // benchmark rather than a unit test is what holds it in place.
        if (_unindexed.Count == 0)
        {
            return [.. indexed.Select(e => e.Stub)];
        }

        // Ordered by store position, because the engine breaks priority ties by insertion order.
        // Sorting the two buckets back together — rather than hand-rolling a merge — costs nothing
        // worth measuring (this runs over candidates, not over the store) and leaves no boundary
        // conditions to get wrong.
        return [.. indexed.Concat(_unindexed).OrderBy(e => e.Position).Select(e => e.Stub)];
    }

    /// <summary>
    /// The bucket key for a stub, or null when it cannot be indexed and must be considered for every
    /// request.
    /// </summary>
    /// <remarks>
    /// A stub is indexable only when BOTH its method and its path pin exact values. Which matchers
    /// qualify is decided by the matchers themselves: <c>urlPath</c> equality implements
    /// <see cref="IExactValueMatcher"/> and <c>url</c> equality deliberately does not, because a
    /// full-URL matcher pins path PLUS query while the lookup key is built from the path alone —
    /// keying both the same way is exactly how an index starts hiding stubs.
    /// </remarks>
    private static string? KeyFor(StubMapping stub)
    {
        if (stub.Request.Method is not IExactValueMatcher { ExactValue: { } method })
        {
            return null;
        }

        return stub.Request.Url is IExactValueMatcher { ExactValue: { } path }
            ? Key(method, path)
            : null;
    }

    private static string Key(string method, string path) => $"{method}\n{path}";
}
