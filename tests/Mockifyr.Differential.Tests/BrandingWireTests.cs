using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// White-labelling the dashboard (#396): the host's identity reaches the browser through the served
/// shell. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
/// <remarks>
/// The shell is the only channel — there is no config endpoint — so these tests read the HTML the host
/// actually sends. Asserting on the served bytes rather than on a C# object is the point: the failure
/// this guards against is configuration that is accepted, stored, and never reaches the page.
/// </remarks>
public sealed class BrandingWireTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mockifyr-brand-" + Guid.NewGuid().ToString("N"));
    private string _dashboard = null!;
    private string _logo = null!;

    public Task InitializeAsync()
    {
        // A minimal SPA shell — the same shape the real build produces, which is all the injection
        // depends on.
        _dashboard = Path.Combine(_root, "dashboard");
        Directory.CreateDirectory(_dashboard);
        File.WriteAllText(Path.Combine(_dashboard, "index.html"),
            "<!doctype html><html><head><title>d</title></head><body><div id=\"root\"></div></body></html>");

        _logo = Path.Combine(_root, "logo.svg");
        File.WriteAllText(_logo, "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<(WebApplication Host, HttpClient Client)> StartAsync(params string[] extraArgs)
    {
        var host = MockifyrHost.Build(
            ["--port", "0", "--https-port", "0", "--dashboard", _dashboard, .. extraArgs]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }

    /// <summary>The injected configuration object, parsed out of the served shell.</summary>
    private static JsonElement ConfigIn(string shell)
    {
        const string marker = "window.__MOCKIFYR__=";
        var start = shell.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "the shell carries no injected configuration");
        start += marker.Length;
        var end = shell.IndexOf(";</script>", start, StringComparison.Ordinal);
        Assert.True(end > start, "the injected configuration is not terminated");
        return JsonDocument.Parse(shell[start..end]).RootElement;
    }

    [Fact]
    public async Task An_unbranded_host_injects_no_identity()
    {
        // The compatibility criterion: nothing configured means the dashboard's own defaults, and the
        // fields are absent rather than empty strings — an empty string would render as a blank name.
        var (host, client) = await StartAsync();
        using var _ = host;
        using var __ = client;

        var config = ConfigIn(await client.GetStringAsync("/__mockifyr/"));

        Assert.Equal(JsonValueKind.Null, config.GetProperty("brandName").ValueKind);
        Assert.Equal(JsonValueKind.Null, config.GetProperty("brandLogo").ValueKind);
        Assert.Equal(JsonValueKind.Null, config.GetProperty("supportUrl").ValueKind);
    }

    [Fact]
    public async Task The_configured_identity_reaches_the_shell()
    {
        var (host, client) = await StartAsync(
            "--brand-name", "dfx-mockapi",
            "--brand-subtitle", "Integration Sandbox",
            "--support-url", "https://example.invalid/help",
            "--brand-logo", _logo);
        using var _ = host;
        using var __ = client;

        var config = ConfigIn(await client.GetStringAsync("/__mockifyr/"));

        Assert.Equal("dfx-mockapi", config.GetProperty("brandName").GetString());
        Assert.Equal("Integration Sandbox", config.GetProperty("brandSubtitle").GetString());
        Assert.Equal("https://example.invalid/help", config.GetProperty("supportUrl").GetString());
        // A URL, not the operator's filesystem path — the browser cannot read the latter and has no
        // business knowing it.
        Assert.Equal("/__mockifyr/brand-logo", config.GetProperty("brandLogo").GetString());
    }

    [Fact]
    public async Task The_logo_is_served_with_its_own_content_type()
    {
        var (host, client) = await StartAsync("--brand-logo", _logo);
        using var _ = host;
        using var __ = client;

        using var response = await client.GetAsync("/__mockifyr/brand-logo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_logo_the_route_is_absent_rather_than_empty()
    {
        // It falls through to the SPA route, which is the honest answer for a path this host does not
        // serve: there is no logo, so there is no logo endpoint pretending there might be.
        var (host, client) = await StartAsync();
        using var _ = host;
        using var __ = client;

        using var response = await client.GetAsync("/__mockifyr/brand-logo");
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void A_logo_that_does_not_exist_is_refused_at_startup()
    {
        // Refused rather than ignored: a logo that silently does not appear is indistinguishable from
        // a flag that was never read, and the operator goes looking in the wrong place.
        var thrown = Assert.Throws<InvalidOperationException>(() => MockifyrHost.Build(
            ["--port", "0", "--https-port", "0", "--brand-logo", Path.Combine(_root, "nope.svg")]));

        Assert.Contains("does not exist", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("/relative/help")]
    public void A_support_url_a_browser_should_not_follow_is_refused(string url)
    {
        // The dashboard renders this in an anchor. A `javascript:` URL there would be a scripting
        // vector handed to the operator by their own configuration file, which is a strange way to be
        // compromised — and a relative one would point at the mock surface, not at help.
        var thrown = Assert.Throws<InvalidOperationException>(() => MockifyrHost.Build(
            ["--port", "0", "--https-port", "0", "--support-url", url]));

        Assert.Contains("absolute http or https URL", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shell_still_carries_the_tenant_header()
    {
        // #396a's field must survive #396b's additions — the two slices share one injection point.
        var (host, client) = await StartAsync("--tenant-header", "X-Team");
        using var _ = host;
        using var __ = client;

        var config = ConfigIn(await client.GetStringAsync("/__mockifyr/"));
        Assert.Equal("X-Team", config.GetProperty("tenantHeader").GetString());
    }
    [Fact]
    public async Task The_browser_tab_carries_the_brand_too()
    {
        // The title is baked into the built shell rather than rendered by the app, so a host branded
        // everywhere else still announced the product name in every tab. Found by running it.
        var (host, client) = await StartAsync("--brand-name", "dfx-mockapi", "--brand-subtitle", "Sandbox");
        using var _ = host;
        using var __ = client;

        var shell = await client.GetStringAsync("/__mockifyr/");

        Assert.Contains("<title>dfx-mockapi — Sandbox</title>", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>d</title>", shell, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unbranded_host_keeps_the_shell_title_it_was_built_with()
    {
        var (host, client) = await StartAsync();
        using var _ = host;
        using var __ = client;

        Assert.Contains("<title>d</title>", await client.GetStringAsync("/__mockifyr/"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_reports_the_configured_name()
    {
        // The dashboard's status line and Settings screen render this. A literal here would have left
        // the product name at the bottom of every page of an otherwise branded host.
        var (host, client) = await StartAsync("--brand-name", "dfx-mockapi");
        using var _ = host;
        using var __ = client;

        var health = JsonDocument.Parse(await client.GetStringAsync("/__admin/health")).RootElement;
        Assert.Equal("dfx-mockapi", health.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Health_reports_the_product_name_when_unbranded()
    {
        var (host, client) = await StartAsync();
        using var _ = host;
        using var __ = client;

        var health = JsonDocument.Parse(await client.GetStringAsync("/__admin/health")).RootElement;
        Assert.Equal("Mockifyr", health.GetProperty("name").GetString());
    }

}
