using Mockifyr.Core;
using Mockifyr.Stores.InMemory;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Values that resolve values, constants, and shared host-level values (#352).
/// </summary>
public sealed class EnvironmentCompositionTests
{
    private static readonly TenantId Acme = new("acme");

    private static EnvironmentKey Key(string name, string value, bool secret = false, bool constant = false) =>
        new(name, "v", [new EnvironmentValue("v", value, secret)], constant);

    private static Func<string, string?> From(params EnvironmentKey[] keys) =>
        name => keys.FirstOrDefault(k => k.Key == name)?.Resolve();

    [Fact]
    public void A_value_resolves_through_another_value()
    {
        // The edit people get wrong: copying a host name into every value that needs it, then changing
        // them all together.
        var lookup = From(Key("apiBase", "https://sandbox.acme.com"), Key("paymentsUrl", "{{apiBase}}/v2/payments"));

        Assert.Equal("https://sandbox.acme.com/v2/payments", EnvironmentComposition.Resolve("paymentsUrl", lookup));
    }

    [Fact]
    public void Composition_goes_more_than_one_level_deep()
    {
        var lookup = From(
            Key("host", "acme.com"),
            Key("apiBase", "https://sandbox.{{host}}"),
            Key("paymentsUrl", "{{apiBase}}/v2/payments"));

        Assert.Equal("https://sandbox.acme.com/v2/payments", EnvironmentComposition.Resolve("paymentsUrl", lookup));
    }

    [Fact]
    public void An_unknown_key_is_left_exactly_as_written()
    {
        // Undefined names belong to Handlebars, and touching them here would shadow a helper.
        var lookup = From(Key("greeting", "hello {{name}}"));

        Assert.Equal("hello {{name}}", EnvironmentComposition.Resolve("greeting", lookup));
    }

    [Fact]
    public void An_unknown_key_resolves_to_nothing_rather_than_an_empty_string()
    {
        Assert.Null(EnvironmentComposition.Resolve("absent", From()));
    }

    [Fact]
    public void A_self_reference_terminates_instead_of_expanding_forever()
    {
        var lookup = From(Key("loop", "a {{loop}} b"));

        Assert.Equal("a {{loop}} b", EnvironmentComposition.Resolve("loop", lookup));
    }

    [Fact]
    public void A_two_key_cycle_is_found_at_write_time_and_names_both_keys()
    {
        // "There is a cycle" is a puzzle handed back to the person who just made one.
        var existing = new[] { Key("a", "{{b}}") };
        var candidate = Key("b", "{{a}}");

        var cycle = EnvironmentComposition.FindCycle(candidate, existing);

        Assert.NotNull(cycle);
        Assert.Contains("a", cycle!);
        Assert.Contains("b", cycle);
    }

    [Fact]
    public void A_longer_cycle_is_found_too()
    {
        var existing = new[] { Key("a", "{{b}}"), Key("b", "{{c}}") };

        Assert.NotNull(EnvironmentComposition.FindCycle(Key("c", "{{a}}"), existing));
    }

    [Fact]
    public void A_value_referencing_itself_is_a_cycle()
    {
        Assert.NotNull(EnvironmentComposition.FindCycle(Key("loop", "{{loop}}"), []));
    }

    [Fact]
    public void A_chain_that_does_not_close_is_not_a_cycle()
    {
        var existing = new[] { Key("apiBase", "https://sandbox.acme.com") };

        Assert.Null(EnvironmentComposition.FindCycle(Key("paymentsUrl", "{{apiBase}}/v2"), existing));
    }

    [Fact]
    public void Replacing_a_key_is_checked_against_its_new_value_not_the_stored_one()
    {
        // Otherwise the way to create a cycle is to edit one of the two keys afterwards.
        var existing = new[] { Key("a", "{{b}}"), Key("b", "harmless") };

        Assert.NotNull(EnvironmentComposition.FindCycle(Key("b", "{{a}}"), existing));
    }

    [Fact]
    public void Secrecy_travels_through_composition()
    {
        // If authHeader is "Bearer {{apiToken}}" and apiToken is secret, then reading authHeader reads
        // the secret — composition must not become a way around redaction.
        var keys = new[] { Key("apiToken", "sk-live-1234", secret: true), Key("authHeader", "Bearer {{apiToken}}") };
        EnvironmentKey? Lookup(string name) => keys.FirstOrDefault(k => k.Key == name);

        Assert.True(EnvironmentComposition.ResolvesToSecret("authHeader", Lookup));
        Assert.False(EnvironmentComposition.ResolvesToSecret("apiToken", name => null));
    }

    [Fact]
    public void A_value_referencing_nothing_secret_stays_readable()
    {
        var keys = new[] { Key("apiBase", "https://sandbox.acme.com"), Key("url", "{{apiBase}}/v2") };

        Assert.False(EnvironmentComposition.ResolvesToSecret("url", name => keys.FirstOrDefault(k => k.Key == name)));
    }

    [Fact]
    public void References_are_read_in_order_and_ignore_helper_syntax()
    {
        var found = EnvironmentComposition.References("{{a}} {{request.path}} {{#each x}} {{b}}");

        Assert.Equal(["a", "b"], found);
    }

    [Fact]
    public void A_tenants_own_key_beats_the_shared_one_and_the_shared_one_fills_the_gap()
    {
        // A shared value that could not be overridden would be a constraint rather than a convenience.
        var store = new InMemoryEnvironmentStore(new HostEnvironment([
            Key("apiBase", "https://shared.example", constant: true),
            Key("iban", "DE89370400440532013000", constant: true),
        ]));
        store.Put(Acme, Key("apiBase", "https://acme.example"));

        Assert.True(store.TryResolve(Acme, "apiBase", out var own));
        Assert.Equal("https://acme.example", own);

        Assert.True(store.TryResolve(Acme, "iban", out var inherited));
        Assert.Equal("DE89370400440532013000", inherited);

        // And another tenant that overrode nothing still sees the shared value.
        Assert.True(store.TryResolve(new TenantId("other"), "apiBase", out var shared));
        Assert.Equal("https://shared.example", shared);
    }

    [Fact]
    public void A_tenant_value_composes_with_an_inherited_one()
    {
        var store = new InMemoryEnvironmentStore(new HostEnvironment([Key("apiBase", "https://shared.example")]));
        store.Put(Acme, Key("paymentsUrl", "{{apiBase}}/v2/payments"));

        Assert.True(store.TryResolve(Acme, "paymentsUrl", out var composed));
        Assert.Equal("https://shared.example/v2/payments", composed);
    }

    [Theory]
    [InlineData("baseUrl=https://x.test", "baseUrl", "https://x.test")]
    [InlineData("url=https://x.test/a=b", "url", "https://x.test/a=b")]
    public void Host_values_parse_at_the_first_equals_so_a_url_survives(string pair, string key, string value)
    {
        var parsed = HostEnvironment.Parse([pair]);

        Assert.Equal(value, parsed.Get(key)!.Resolve());
        Assert.True(parsed.Get(key)!.Constant);
    }

    [Theory]
    [InlineData("=nokey")]
    [InlineData("no-equals-at-all")]
    [InlineData("has space=x")]
    [InlineData("now=x")]
    public void A_host_value_that_could_not_be_referenced_is_refused_rather_than_stored(string pair)
    {
        // A key named after a built-in helper, or one that is not identifier-shaped, could never be
        // resolved by a stub — storing it would be a value nobody can use and nobody can see is broken.
        Assert.Empty(HostEnvironment.Parse([pair]).Keys);
    }

    [Fact]
    public void An_empty_host_environment_resolves_nothing_and_changes_nothing()
    {
        var store = new InMemoryEnvironmentStore();

        Assert.False(store.TryResolve(Acme, "anything", out _));
        Assert.False(store.HasKeys(Acme));
    }

    [Fact]
    public void Composition_stops_at_the_depth_bound_rather_than_running_forever()
    {
        // The backstop for a cycle the write-time check never saw — a store restored from a file
        // written by an older version, say. It must return something rather than hang a request.
        var keys = Enumerable.Range(0, EnvironmentComposition.MaxDepth + 5)
            .Select(i => Key($"k{i}", $"{{{{k{i + 1}}}}}"))
            .ToArray();

        var resolved = EnvironmentComposition.Resolve("k0", From(keys));

        Assert.NotNull(resolved);
        Assert.Contains("{{", resolved!);
    }

    [Fact]
    public void A_chain_inside_the_bound_resolves_completely()
    {
        var keys = Enumerable.Range(0, 5).Select(i => Key($"k{i}", $"{{{{k{i + 1}}}}}"))
            .Append(Key("k5", "end")).ToArray();

        Assert.Equal("end", EnvironmentComposition.Resolve("k0", From(keys)));
    }

    [Fact]
    public void Secrecy_stops_at_the_depth_bound_too_rather_than_recursing_forever()
    {
        var keys = Enumerable.Range(0, EnvironmentComposition.MaxDepth + 5)
            .Select(i => Key($"k{i}", $"{{{{k{i + 1}}}}}"))
            .ToArray();

        Assert.False(EnvironmentComposition.ResolvesToSecret("k0", n => keys.FirstOrDefault(k => k.Key == n)));
    }

    [Fact]
    public void A_secret_deep_in_a_chain_still_marks_the_top()
    {
        var keys = new[] { Key("a", "{{b}}"), Key("b", "{{c}}"), Key("c", "sk-live", secret: true) };

        Assert.True(EnvironmentComposition.ResolvesToSecret("a", n => keys.FirstOrDefault(k => k.Key == n)));
    }

    [Fact]
    public void An_unclosed_reference_is_not_read_as_one()
    {
        // "{{a" is text somebody is still typing, not a reference — and treating it as one would make
        // the cycle check see edges that do not exist.
        Assert.Empty(EnvironmentComposition.References("{{a"));
        Assert.Empty(EnvironmentComposition.References("no references here"));
        Assert.Empty(EnvironmentComposition.References(string.Empty));
    }

    [Fact]
    public void A_reference_at_the_very_end_of_a_value_is_still_found()
    {
        Assert.Equal(["a"], EnvironmentComposition.References("prefix {{a}}"));
    }

    [Fact]
    public void Shared_keys_are_listed_by_name_so_the_screen_does_not_shuffle()
    {
        var shared = HostEnvironment.Parse(["zeta=1", "alpha=2", "mid=3"]);

        Assert.Equal(["alpha", "mid", "zeta"], shared.Keys.Select(k => k.Key));
    }

    [Fact]
    public void A_pair_whose_name_is_missing_entirely_is_refused()
    {
        // "=value" has a separator at index zero: a key with no name is not a key.
        Assert.Empty(HostEnvironment.Parse(["=value"]).Keys);
        Assert.Null(HostEnvironment.Empty.Get("anything"));
    }

    [Fact]
    public void Two_values_referencing_the_same_third_one_is_not_a_cycle()
    {
        // The diamond: apiBase used by both paymentsUrl and webhookUrl is the ordinary case this
        // feature exists for, and a search that forgot to unmark a visited key would refuse it as a
        // loop — turning the most common shape into an error.
        var existing = new[]
        {
            Key("apiBase", "https://sandbox.acme.com"),
            Key("paymentsUrl", "{{apiBase}}/v2/payments"),
            Key("webhookUrl", "{{apiBase}}/hooks"),
        };

        Assert.Null(EnvironmentComposition.FindCycle(Key("summary", "{{paymentsUrl}} and {{webhookUrl}}"), existing));
    }
}
