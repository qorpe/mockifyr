using Mockifyr.Templating;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Coverage for template compilation caching (#266). The cache is an optimization, so the tests are
/// mostly about what it must NOT change: a helper that varies per request has to keep varying, and the
/// cache must not become an unbounded dictionary keyed by authored input.
/// </summary>
public sealed class CompiledTemplateCacheTests
{
    private static CompiledTemplateCache Cache(int capacity = CompiledTemplateCache.DefaultCapacity) =>
        CompiledTemplateCache.Create(capacity);

    private static Dictionary<string, object?> Model(string value) =>
        new() { ["request"] = new Dictionary<string, object?> { ["body"] = value } };

    [Fact]
    public void The_same_template_renders_the_same_way_on_every_call()
    {
        var cache = Cache();

        Assert.Equal("hello world", cache.Render("hello {{request.body}}", Model("world")));
        Assert.Equal("hello again", cache.Render("hello {{request.body}}", Model("again")));
        // Compiled once, rendered twice — the model is not part of the key.
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Distinct_templates_are_compiled_separately()
    {
        var cache = Cache();

        Assert.Equal("a: x", cache.Render("a: {{request.body}}", Model("x")));
        Assert.Equal("b: x", cache.Render("b: {{request.body}}", Model("x")));

        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Per_request_helpers_still_vary_between_renders()
    {
        var cache = Cache();

        var first = cache.Render("{{randomValue type='UUID'}}", Model(""));
        var second = cache.Render("{{randomValue type='UUID'}}", Model(""));

        // The single most important property: caching COMPILATION must never become caching OUTPUT.
        // randomValue/now/faker are invocations inside the compiled delegate and must still run per
        // render — a cached response body would silently break every stub that relies on them.
        Assert.NotEqual(first, second);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void The_cache_is_bounded()
    {
        var cache = Cache(capacity: 8);

        for (var i = 0; i < 200; i++)
        {
            cache.Render($"template-{i} {{{{request.body}}}}", Model("x"));

            // Checked after EVERY render, not once at the end: a bound that is exceeded and then
            // happens to be back under the limit when the test looks is not a bound. Template text is
            // authored input — on a shared sandbox a stub author can produce unlimited distinct
            // templates, so an unbounded dictionary keyed by them is a memory leak with a trivial
            // trigger.
            Assert.True(cache.Count <= 8, $"cache held {cache.Count} entries after {i + 1} renders, expected at most 8");
        }
    }

    [Fact]
    public void A_template_still_renders_after_the_cache_is_recycled()
    {
        var cache = Cache(capacity: 4);
        const string Template = "kept: {{request.body}}";

        Assert.Equal("kept: first", cache.Render(Template, Model("first")));
        for (var i = 0; i < 50; i++)
        {
            cache.Render($"filler-{i}", Model("x"));
        }

        // Eviction is a cache miss, never a failure: the template recompiles and renders identically.
        Assert.Equal("kept: second", cache.Render(Template, Model("second")));
    }

    [Fact]
    public void Concurrent_renders_of_a_new_template_all_succeed()
    {
        var cache = Cache();
        var results = new string[64];

        Parallel.For(0, results.Length, i => results[i] = cache.Render("concurrent {{request.body}}", Model($"{i}")));

        // Two threads racing on the same uncached template each compile and one wins the slot; neither
        // may see a half-published entry.
        Assert.All(results, result => Assert.StartsWith("concurrent ", result));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void An_invalid_template_throws_rather_than_caching_a_broken_delegate()
    {
        var cache = Cache();

        Assert.ThrowsAny<Exception>(() => cache.Render("{{#if true}}never closed", Model("x")));

        // A template that failed to compile must not occupy a slot — the next call should try again
        // rather than replay a failure from a poisoned cache.
        Assert.Equal(0, cache.Count);
    }
}
