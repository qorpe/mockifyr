using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// How large a request body this host reads, overall and per tenant (#349). Pure, so unit-tested;
/// enforced by a middleware at the edge.
/// </summary>
public sealed class RequestBodyLimitTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");

    [Fact]
    public void With_nothing_configured_nothing_is_bounded()
    {
        Assert.False(RequestBodyLimits.Unset.IsConfigured);
        Assert.Null(RequestBodyLimits.Unset.For(Acme));
    }

    [Fact]
    public void A_tenant_with_no_value_of_its_own_inherits_the_host_ceiling()
    {
        Assert.Equal(1_000, RequestBodyLimits.From(1_000).For(Acme));
    }

    [Fact]
    public void A_tenant_may_be_held_below_the_ceiling()
    {
        var limits = RequestBodyLimits.From(1_000, ["acme:200"]);

        Assert.Equal(200, limits.For(Acme));
        Assert.Equal(1_000, limits.For(Globex));
    }

    [Fact]
    public void A_tenant_cannot_raise_itself_above_the_ceiling()
    {
        // The whole point of the host value. If a tenant entry could exceed it, the one number an
        // operator sets to bound the machine could be undone by configuration written later — which
        // would not be a ceiling at all.
        Assert.Equal(1_000, RequestBodyLimits.From(1_000, ["acme:5000"]).For(Acme));
    }

    [Fact]
    public void A_tenant_limit_works_with_no_host_ceiling_at_all()
    {
        var limits = RequestBodyLimits.From(hostCeiling: null, ["acme:200"]);

        Assert.Equal(200, limits.For(Acme));
        Assert.Null(limits.For(Globex));
    }

    [Theory]
    [InlineData("acme:0")]
    [InlineData("acme:-5")]
    [InlineData("acme:lots")]
    [InlineData("acme")]
    [InlineData(":500")]
    [InlineData("")]
    public void An_entry_that_does_not_name_a_positive_size_is_dropped(string entry)
    {
        // Never read as zero: a limit of zero refuses every request carrying a body, which is not what
        // any typo meant, and would look like the host being broken rather than misconfigured.
        Assert.Equal(1_000, RequestBodyLimits.From(1_000, [entry]).For(Acme));
    }

    [Fact]
    public void A_tenant_name_containing_a_colon_still_parses_by_its_last_separator()
    {
        Assert.Equal(200, RequestBodyLimits.From(null, ["team:acme:200"]).For(new TenantId("team:acme")));
    }

    [Fact]
    public void A_non_positive_host_ceiling_is_no_ceiling_rather_than_a_refusal_of_everything()
    {
        Assert.Null(RequestBodyLimits.From(0).For(Acme));
        Assert.Null(RequestBodyLimits.From(-1).For(Acme));
    }

    [Fact]
    public void The_refusal_says_which_limit_was_hit()
    {
        // An operator reading a 413 needs to know whether to raise the tenant's number or the host's.
        var limits = RequestBodyLimits.From(1_000, ["acme:200"]);

        Assert.Contains("tenant 'acme'", limits.Refusal(Acme, 200), StringComparison.Ordinal);
        Assert.Contains("host's limit", limits.Refusal(Globex, 1_000), StringComparison.Ordinal);
    }

    [Fact]
    public void A_clamped_tenant_is_told_it_hit_the_hosts_limit_not_its_own()
    {
        // acme asked for 5000 and got 1000. Reporting that as "the limit for tenant acme" would send
        // somebody to raise a number that is already higher than the one actually stopping them.
        var limits = RequestBodyLimits.From(1_000, ["acme:5000"]);

        Assert.Contains("host's limit", limits.Refusal(Acme, 1_000), StringComparison.Ordinal);
    }
}
