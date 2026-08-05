namespace Mockifyr.Templating;

/// <summary>
/// The instant the helpers rendering right now should call "now" (#290).
/// </summary>
/// <remarks>
/// <para>
/// Handlebars helpers are registered once on a shared engine — that is what makes the compiled-template
/// cache (#266) worth having — so a helper cannot be handed a tenant. The renderer therefore publishes
/// the tenant's instant for the duration of one render and the clock-reading helpers pick it up here.
/// </para>
/// <para>
/// <c>[ThreadStatic]</c> rather than <c>AsyncLocal</c>: a render is synchronous from
/// <c>Render</c> to the last helper, so there is no continuation for the value to flow across, and an
/// <c>AsyncLocal</c> write copies the execution context on a path that was measured in microseconds.
/// The <c>finally</c> is what makes it safe — a render that throws must not leave the next request on
/// this thread believing it is 2027.
/// </para>
/// <para>
/// Absent a scope, every helper reads the real clock, so nothing changes for a host that never sets an
/// override.
/// </para>
/// </remarks>
internal static class RenderClock
{
    [ThreadStatic]
    private static DateTimeOffset? _current;

    /// <summary>The instant helpers should use: the tenant's, or the host's when none is published.</summary>
    public static DateTimeOffset UtcNow => _current ?? DateTimeOffset.UtcNow;

    /// <summary>
    /// Publishes <paramref name="instant"/> for the life of the returned scope. A null instant means
    /// "no opinion" and leaves helpers on the real clock.
    /// </summary>
    public static Scope Use(DateTimeOffset? instant) => new(instant);

    /// <summary>Restores whatever was published before, so nested renders behave.</summary>
    internal readonly struct Scope : IDisposable
    {
        private readonly DateTimeOffset? _previous;

        internal Scope(DateTimeOffset? instant)
        {
            _previous = _current;
            _current = instant;
        }

        public void Dispose() => _current = _previous;
    }
}
