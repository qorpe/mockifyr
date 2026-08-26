using Mockifyr.Core;

namespace Mockifyr.Core.Tests;

/// <summary>
/// The pure half of a renameable tenant header (#396): what counts as a legal name, and what the
/// unconfigured host answers.
/// </summary>
public sealed class TenantHeaderOptionsTests
{
    [Fact]
    public void The_default_is_the_historical_name()
    {
        // The compatibility criterion: a host that sets nothing behaves exactly as it always did.
        Assert.Equal("X-Mockifyr-Tenant", TenantHeaderOptions.Default.Name);
        Assert.Equal("X-Mockifyr-Tenant", TenantHeaderOptions.DefaultName);
    }

    [Theory]
    [InlineData("X-Mockifyr-Tenant")]
    [InlineData("X-Team")]
    [InlineData("tenant")]
    [InlineData("X-Tenant-Id-2")]
    [InlineData("a")]
    // Every boundary of every range, in one name: a and z, A and Z, 0 and 9. Without it the tests
    // pass while five of the six comparisons are off by one — mutation testing found exactly that.
    [InlineData("azAZ09")]
    [InlineData("!#$%&'*+-.^_`|~")]   // every punctuation RFC 9110 §5.1 admits in a token
    public void A_legal_field_name_is_accepted(string name)
    {
        Assert.True(TenantHeaderOptions.IsWellFormed(name));
    }

    [Theory]
    [InlineData("")]                  // nothing to match on
    [InlineData("X Team")]            // the realistic typo: a quoted flag with a space in it
    [InlineData("X-Team:")]           // somebody pasting the header as it appears on the wire
    [InlineData("X-Team\n")]          // header splitting, if it ever reached a response
    [InlineData("X/Team")]
    [InlineData("X-Tenant(1)")]
    [InlineData("Kiracı")]            // non-ASCII: legal in a name, not in an HTTP field name
    public void An_illegal_field_name_is_refused(string name)
    {
        // Refusing matters more than it looks. None of these are rejected by the framework — they
        // simply never match, so the host starts, every request falls back to the default tenant, and
        // the symptom (one tenant's stubs answering another's calls) points nowhere near the flag.
        Assert.False(TenantHeaderOptions.IsWellFormed(name));
    }

    [Fact]
    public void A_configured_name_replaces_only_the_name()
    {
        var renamed = TenantHeaderOptions.Default with { Name = "X-Team" };

        Assert.Equal("X-Team", renamed.Name);
        Assert.Equal("X-Mockifyr-Tenant", TenantHeaderOptions.Default.Name);
    }
}
