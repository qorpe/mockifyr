using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Validation of embedded dashboard serving (G12g): with <c>--dashboard &lt;dir&gt;</c> the host serves the
/// built UI under the reserved <c>/__mockifyr</c> prefix (static assets + an SPA fallback to index.html
/// for client routes), while every other path is still owned by the mock-serving catch-all. Mockifyr-
/// specific (no oracle), so a self-test. No Docker required.
/// </summary>
public sealed class G12gDashboardTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mockifyr-dash-").FullName;
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_dir, "index.html"), "<!doctype html><title>Mockifyr</title><div id=root></div>");
        Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        File.WriteAllText(Path.Combine(_dir, "assets", "app.js"), "export const marker = 1;");
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--dashboard", _dir]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Dashboard_IsServedUnderReservedPrefix_WithoutBreakingMockServing()
    {
        // The dashboard index is served at the prefix root.
        var index = await _client!.GetAsync("/__mockifyr/");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("text/html", index.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Mockifyr", await index.Content.ReadAsStringAsync());

        // A built asset is served as its real file with the correct content type — NOT the SPA index.
        // (A module script served as text/html fails to load and blanks the dashboard.)
        var asset = await _client.GetAsync("/__mockifyr/assets/app.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.DoesNotContain("text/html", asset.Content.Headers.ContentType?.MediaType ?? string.Empty);
        Assert.Contains("export const marker", await asset.Content.ReadAsStringAsync());

        // A client-side route falls back to index.html (SPA).
        var route = await _client.GetAsync("/__mockifyr/stubs");
        Assert.Equal(HttpStatusCode.OK, route.StatusCode);
        Assert.Contains("id=root", await route.Content.ReadAsStringAsync());

        // Every other path is still the mock-serving catch-all — no stub loaded, so a 404 (not the SPA).
        var mocked = await _client.GetAsync("/api/anything");
        Assert.Equal(HttpStatusCode.NotFound, mocked.StatusCode);
    }

    [Fact]
    public async Task The_shell_is_never_served_from_a_stale_browser_cache()
    {
        // A cached `index.html` can load a bundle older than the host it is talking to — one that
        // predates a capability like the OIDC login gate — and then fail in ways that look like a
        // server bug. Revalidating the shell on every load is what stops that, and it is cheap: the
        // shell is a few hundred bytes and its hashed assets still cache forever.
        using var index = await _client!.GetAsync("/__mockifyr/");
        Assert.Equal("no-cache", index.Headers.CacheControl?.ToString());

        // The SPA fallback is the same document and must carry the same rule — a client route is how
        // most people actually arrive at the dashboard.
        using var route = await _client.GetAsync("/__mockifyr/stubs");
        Assert.Equal("no-cache", route.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task A_hashed_asset_is_cached_forever_because_its_name_changes_when_it_does()
    {
        using var asset = await _client!.GetAsync("/__mockifyr/assets/app.js");

        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Nothing_outside_assets_is_marked_immutable()
    {
        // The asymmetry is the whole design, and it is the dangerous half: `immutable` on a file whose
        // name does NOT change with its content pins a stale copy in somebody's browser for a year,
        // with no way to reach it. Only Vite's content-hashed `assets/` output earns it.
        File.WriteAllText(Path.Combine(_dir, "favicon.ico"), "not really an icon");

        using var icon = await _client!.GetAsync("/__mockifyr/favicon.ico");

        Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("no-cache", icon.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("immutable", icon.Headers.CacheControl?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task A_path_that_merely_starts_with_the_word_assets_is_not_treated_as_hashed()
    {
        // "assets-old/…" starts with "assets" and is not the hashed output directory. Matching the
        // slash is what keeps a hand-placed file from being pinned for a year by accident.
        Directory.CreateDirectory(Path.Combine(_dir, "assets-old"));
        File.WriteAllText(Path.Combine(_dir, "assets-old", "legacy.js"), "// hand-placed");

        using var legacy = await _client!.GetAsync("/__mockifyr/assets-old/legacy.js");

        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);

        // Asserted as an equality, not as "does not contain immutable": with no header at all that
        // weaker form passes for the wrong reason, which is how a test survives the very change it
        // exists to pin. Caught by reverting the fix and watching this one stay green.
        Assert.Equal("no-cache", legacy.Headers.CacheControl?.ToString());
    }
}
