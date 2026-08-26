using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// The pure half of white-labelling (#396): what the unbranded host reports, and which support URLs
/// the dashboard may be told to link to.
/// </summary>
public sealed class BrandOptionsTests
{
    [Fact]
    public void An_unconfigured_brand_is_recognisably_unconfigured()
    {
        // Callers skip the whole injection when nothing was set, so this has to be exact rather than
        // approximately right.
        Assert.True(BrandOptions.Default.IsDefault);
        Assert.Null(BrandOptions.Default.Name);
        Assert.Null(BrandOptions.Default.Subtitle);
        Assert.Null(BrandOptions.Default.SupportUrl);
        Assert.Null(BrandOptions.Default.LogoPath);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("subtitle")]
    [InlineData("supportUrl")]
    [InlineData("logoPath")]
    public void Any_single_field_makes_it_configured(string field)
    {
        // Partial branding is a supported state — a name without a logo is a normal thing to want —
        // so one field is enough, and each field has to count.
        var brand = field switch
        {
            "name" => BrandOptions.Default with { Name = "x" },
            "subtitle" => BrandOptions.Default with { Subtitle = "x" },
            "supportUrl" => BrandOptions.Default with { SupportUrl = "https://x.invalid" },
            _ => BrandOptions.Default with { LogoPath = "/x.svg" },
        };

        Assert.False(brand.IsDefault);
    }

    [Theory]
    [InlineData("https://example.invalid/help")]
    [InlineData("http://example.invalid")]
    [InlineData("https://example.invalid:8443/a/b?c=d#e")]
    public void An_http_url_is_usable(string url)
    {
        Assert.True(BrandOptions.IsUsableSupportUrl(url));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.invalid")]
    [InlineData("/relative/help")]
    [InlineData("example.invalid/help")]
    [InlineData("")]
    [InlineData("   ")]
    public void Anything_a_browser_should_not_follow_is_refused(string url)
    {
        // The dashboard renders this in an anchor. A `javascript:` or `data:` URL there is a scripting
        // vector handed to the operator by their own configuration file — a strange way to be
        // compromised. A relative one would point at the mock surface rather than at help.
        Assert.False(BrandOptions.IsUsableSupportUrl(url));
    }
}
