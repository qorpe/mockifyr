using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Which hosts this instance may call out to (#349). Pure, so unit-tested; enforced at the webhook,
/// proxy and recording edges.
/// </summary>
public sealed class OutboundHostPolicyTests
{
    [Fact]
    public void With_nothing_configured_everything_is_allowed()
    {
        // The default, and what every existing host does. A mock server proxies and calls webhooks for
        // a living, so deny-by-default would break each of them on upgrade.
        var policy = OutboundHostPolicy.From([]);

        Assert.False(policy.IsRestricted);
        Assert.True(policy.Allows("http://anything.example"));
        Assert.True(policy.Allows("not even a url"));
    }

    [Fact]
    public void A_named_host_is_allowed_on_any_port()
    {
        // An operator naming a machine means the machine. Making them enumerate ports produces
        // allowlists that block something legitimate, which is how a control comes to be switched off.
        var policy = OutboundHostPolicy.From(["partner.example"]);

        Assert.True(policy.Allows("https://partner.example/hooks"));
        Assert.True(policy.Allows("http://partner.example:8443/hooks"));
        Assert.False(policy.Allows("https://other.example/hooks"));
    }

    [Fact]
    public void A_port_can_be_pinned_when_that_is_what_is_meant()
    {
        var policy = OutboundHostPolicy.From(["partner.example:8443"]);

        Assert.True(policy.Allows("https://partner.example:8443/hooks"));
        Assert.False(policy.Allows("https://partner.example/hooks"));
    }

    [Fact]
    public void A_wildcard_covers_subdomains_and_deliberately_not_the_apex()
    {
        // Somebody allowing *.internal.example almost never means internal.example itself, and for a
        // control like this the permissive guess is the wrong guess.
        var policy = OutboundHostPolicy.From(["*.partner.example"]);

        Assert.True(policy.Allows("https://hooks.partner.example/"));
        Assert.True(policy.Allows("https://a.b.partner.example/"));
        Assert.False(policy.Allows("https://partner.example/"));
        Assert.False(policy.Allows("https://notpartner.example/"));
    }

    [Fact]
    public void A_wildcard_does_not_match_a_host_that_merely_ends_with_the_text()
    {
        // "evilpartner.example" ends with "partner.example" as a string. The dot is what makes this a
        // domain check rather than a suffix check.
        Assert.False(OutboundHostPolicy.From(["*.partner.example"]).Allows("https://evilpartner.example/"));
    }

    [Fact]
    public void Host_matching_ignores_case_because_DNS_does()
    {
        Assert.True(OutboundHostPolicy.From(["Partner.Example"]).Allows("https://PARTNER.example/"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/only")]
    public void An_unparseable_target_is_refused_once_a_restriction_is_in_force(string? url)
    {
        // "We could not tell, so we allowed it" is precisely the failure an allowlist exists to remove.
        Assert.False(OutboundHostPolicy.From(["partner.example"]).Allows(url));
    }

    [Fact]
    public void The_refusal_names_the_host_and_what_was_allowed()
    {
        // An operator reading this in a journal entry has to be able to act on it without a second
        // round trip to whoever configured the host.
        var refusal = OutboundHostPolicy.From(["partner.example"]).Refusal("https://internal.svc/hook");

        Assert.Contains("internal.svc", refusal, StringComparison.Ordinal);
        Assert.Contains("partner.example", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*.")]
    [InlineData(":8443")]
    public void An_entry_that_names_no_host_is_dropped_rather_than_matching_everything(string entry)
    {
        // A blank entry that matched anything would silently turn the allowlist off — the worst
        // outcome for a control whose whole value is being trusted.
        Assert.False(OutboundHostPolicy.From([entry, "partner.example"]).Allows("https://other.example/"));
    }

    [Fact]
    public void An_entry_list_of_only_junk_leaves_the_policy_unrestricted_and_says_so()
    {
        // Not silently "restricted to nothing", which would break every outbound call with no clue
        // why. IsRestricted is what the startup line reports, so an operator sees the truth.
        Assert.False(OutboundHostPolicy.From(["", "  "]).IsRestricted);
    }

    [Fact]
    public void The_configured_entries_read_back_the_way_they_were_written()
    {
        Assert.Equal(
            ["*.partner.example", "hooks.example:9000"],
            OutboundHostPolicy.From(["*.partner.example", "hooks.example:9000"]).Entries);
    }
}
