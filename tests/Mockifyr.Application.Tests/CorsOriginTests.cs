using Mockifyr.Core;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Which browser origins may call a tenant's sandbox (#349). Pure, so unit-tested; the headers
/// themselves are asserted at the wire.
/// </summary>
public sealed class CorsOriginTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Globex = new("globex");

    [Fact]
    public void Nothing_configured_allows_nothing_and_says_so()
    {
        // The caller reads IsConfigured to decide whether to emit headers at all, which is what leaves
        // an existing host byte-identical rather than newly refusing things.
        Assert.False(CorsOrigins.None.IsConfigured);
        Assert.False(CorsOrigins.None.Allows(Acme, "https://app.example"));
    }

    [Fact]
    public void A_host_wide_origin_covers_every_tenant_that_names_none_of_its_own()
    {
        var policy = CorsOrigins.From(["https://app.example"]);

        Assert.True(policy.Allows(Acme, "https://app.example"));
        Assert.True(policy.Allows(Globex, "https://app.example"));
        Assert.False(policy.Allows(Acme, "https://evil.example"));
    }

    [Fact]
    public void A_tenant_with_its_own_list_uses_it_instead_of_the_host_wide_one()
    {
        // Replaces rather than adds: a tenant naming its origins is stating the whole set, and quietly
        // unioning in a host-wide entry would grant access nobody asked for on that tenant.
        var policy = CorsOrigins.From(["https://shared.example"], ["acme=https://acme.example"]);

        Assert.True(policy.Allows(Acme, "https://acme.example"));
        Assert.False(policy.Allows(Acme, "https://shared.example"));
        Assert.True(policy.Allows(Globex, "https://shared.example"));
    }

    [Fact]
    public void A_tenant_may_name_several_origins()
    {
        var policy = CorsOrigins.From(null, ["acme=https://a.example", "acme=https://b.example"]);

        Assert.True(policy.Allows(Acme, "https://a.example"));
        Assert.True(policy.Allows(Acme, "https://b.example"));
    }

    [Fact]
    public void Scheme_and_port_are_part_of_the_origin()
    {
        // http and https are different security contexts; treating them as one is how a mixed-content
        // mistake turns into a permission.
        var policy = CorsOrigins.From(["https://app.example"]);

        Assert.False(policy.Allows(Acme, "http://app.example"));
        Assert.False(policy.Allows(Acme, "https://app.example:8443"));
    }

    [Fact]
    public void A_trailing_slash_or_path_in_configuration_still_matches_what_a_browser_sends()
    {
        // Configuration is written by hand, and a browser never sends a path in Origin. Failing on
        // "https://app.example/" would look like the feature being broken rather than mistyped.
        var policy = CorsOrigins.From(["https://app.example/", "https://other.example/some/path"]);

        Assert.True(policy.Allows(Acme, "https://app.example"));
        Assert.True(policy.Allows(Acme, "https://other.example"));
    }

    [Fact]
    public void A_default_port_written_out_is_the_same_origin()
    {
        Assert.True(CorsOrigins.From(["https://app.example:443"]).Allows(Acme, "https://app.example"));
    }

    [Fact]
    public void Host_comparison_ignores_case_because_DNS_does()
    {
        Assert.True(CorsOrigins.From(["https://App.Example"]).Allows(Acme, "https://app.example"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("app.example")]
    [InlineData("*")]
    public void Something_that_is_not_an_origin_is_never_allowed(string? origin)
    {
        // "null" is what a sandboxed iframe or a file:// page sends, and "*" is not an origin at all —
        // neither should match a configured entry by accident.
        Assert.False(CorsOrigins.From(["https://app.example"]).Allows(Acme, origin));
    }

    [Theory]
    [InlineData("not-an-origin")]
    [InlineData("=https://app.example")]
    [InlineData("acme=")]
    [InlineData("acme=nonsense")]
    public void A_configuration_entry_that_names_no_usable_origin_is_dropped(string entry)
    {
        Assert.False(CorsOrigins.From([entry], [entry]).IsConfigured);
    }

    [Fact]
    public void The_separator_is_not_a_colon_because_every_origin_contains_one()
    {
        // tenant=origin, so there is no "which colon did you mean" rule for anyone to get wrong.
        Assert.True(CorsOrigins.From(null, ["acme=https://app.example:8443"]).Allows(Acme, "https://app.example:8443"));
    }

    [Fact]
    public void Either_kind_of_entry_on_its_own_counts_as_configured()
    {
        // IsConfigured decides whether the middleware is installed at all, so reporting false with only
        // one of the two set would mean headers nobody ever emits.
        Assert.True(CorsOrigins.From(["https://app.example"]).IsConfigured);
        Assert.True(CorsOrigins.From(null, ["acme=https://app.example"]).IsConfigured);
        Assert.False(CorsOrigins.From(null, null).IsConfigured);
    }

    [Fact]
    public void The_configured_set_reads_back_for_the_startup_line_in_a_stable_order()
    {
        // An operator comparing today's startup line with yesterday's should not have to wonder whether
        // the order means anything. Two entries per side, or the ordering is never exercised.
        var described = CorsOrigins.From(
            ["https://b.example", "https://a.example"],
            ["zeta=https://z.example", "acme=https://acme.example:8443", "acme=https://acme.example"]).Describe();

        Assert.Equal(
            [
                "https://a.example",
                "https://b.example",
                "acme=https://acme.example",
                "acme=https://acme.example:8443",
                "zeta=https://z.example",
            ],
            described);
    }
}
