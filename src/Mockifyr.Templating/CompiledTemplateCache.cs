using System.Collections.Concurrent;
using HandlebarsDotNet;

namespace Mockifyr.Templating;

/// <summary>
/// Caches compiled Handlebars templates by their source text (#266).
/// </summary>
/// <remarks>
/// <para>
/// <c>IHandlebars.Compile</c> parses the template and builds a delegate. Doing that per request cost
/// ~699 µs on a templated response against ~357 ns on a static one — almost all of it work that could
/// have been done once (measured in <c>docs/parity/performance.md</c>). Compilation is the only thing
/// cached: helpers like <c>randomValue</c>, <c>now</c> and <c>faker</c> are invocations *inside* the
/// compiled delegate, so they still run on every render and still vary per request.
/// </para>
/// <para>
/// Bounded on purpose. Template text is authored input, and on a shared sandbox a stub author can
/// produce unlimited distinct templates — an unbounded dictionary keyed by them is a memory leak with
/// a trivial trigger. At the bound the cache is cleared rather than evicted one entry at a time: a
/// template that is still in use recompiles once and returns, while an LRU would cost a bookkeeping
/// write on every hit to defend against a case that is pathological rather than normal.
/// </para>
/// </remarks>
public sealed class CompiledTemplateCache(IHandlebars handlebars, int capacity = CompiledTemplateCache.DefaultCapacity)
{
    /// <summary>
    /// The default number of distinct templates kept. A host serves far fewer distinct templates than
    /// requests — this is sized for "every stub on a busy host", not for a caller generating templates.
    /// </summary>
    public const int DefaultCapacity = 2000;

    private readonly ConcurrentDictionary<string, HandlebarsTemplate<object, object>> _compiled = new(StringComparer.Ordinal);

    /// <summary>
    /// A cache over the standard helper set — what a renderer needing no custom helpers wants, and what
    /// a test can construct without reaching into the assembly's internals.
    /// </summary>
    public static CompiledTemplateCache Create(int capacity = DefaultCapacity) =>
        new(HandlebarsFactory.Create(), capacity);

    /// <summary>How many compiled templates are currently held (for tests and diagnostics).</summary>
    public int Count => _compiled.Count;

    /// <summary>Renders <paramref name="template"/> against <paramref name="model"/>, compiling it at most once.</summary>
    public string Render(string template, object model)
    {
        if (_compiled.TryGetValue(template, out var cached))
        {
            return cached(model);
        }

        // Compile outside the dictionary so two threads racing on the same new template each compile
        // once and one wins the slot — cheaper than holding a lock across compilation.
        var compiled = handlebars.Compile(template);

        if (_compiled.Count >= capacity)
        {
            _compiled.Clear();
        }

        _compiled[template] = compiled;
        return compiled(model);
    }
}
