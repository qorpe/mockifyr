using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for tenant-scoped recording. Recording used to be one global session, so on a shared
/// host a team starting a recording discarded another team's captures and proxied everyone's traffic
/// to their own upstream — silently, in both directions. These tests pin that each tenant's session is
/// its own.
/// </summary>
public sealed class RecordingTenantScopeTests : IAsyncDisposable
{
    private readonly Microsoft.AspNetCore.Builder.WebApplication _upstreamAlpha;
    private readonly Microsoft.AspNetCore.Builder.WebApplication _upstreamBeta;
    private readonly Microsoft.AspNetCore.Builder.WebApplication _host;

    public RecordingTenantScopeTests()
    {
        _upstreamAlpha = Upstream("from-alpha-upstream");
        _upstreamBeta = Upstream("from-beta-upstream");
        _host = MockifyrHost.Build(["--port", "0"]);
    }

    private static Microsoft.AspNetCore.Builder.WebApplication Upstream(string body)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/thing", () => Results.Text(body));
        return app;
    }

    private static string AddressOf(Microsoft.AspNetCore.Builder.WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");

    private async Task StartAsync()
    {
        await _upstreamAlpha.StartAsync();
        await _upstreamBeta.StartAsync();
        await _host.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstreamAlpha.DisposeAsync();
        await _upstreamBeta.DisposeAsync();
    }

    private HttpClient Client() => new() { BaseAddress = new Uri(AddressOf(_host)) };

    private static HttpRequestMessage For(HttpMethod method, string path, string tenant, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    [Fact]
    public async Task One_tenants_recording_leaves_another_tenant_serving_normally()
    {
        await StartAsync();
        using var client = Client();

        using var stub = await client.SendAsync(For(HttpMethod.Post, "/__admin/mappings", "beta",
            """{"request":{"method":"GET","urlPath":"/thing"},"response":{"status":200,"body":"beta-stub"}}"""));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        using var started = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/start", "alpha",
            $$"""{"targetBaseUrl":"{{AddressOf(_upstreamAlpha)}}"}"""));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        // alpha is recording, so its request is proxied upstream…
        using var alphaServed = await client.SendAsync(For(HttpMethod.Get, "/thing", "alpha"));
        Assert.Equal("from-alpha-upstream", await alphaServed.Content.ReadAsStringAsync());

        // …and beta, which is not, still gets its own stub. Globally, beta's traffic went to alpha's
        // upstream — the sharpest form of this bug, because beta never asked for anything.
        using var betaServed = await client.SendAsync(For(HttpMethod.Get, "/thing", "beta"));
        Assert.Equal("beta-stub", await betaServed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Recording_status_is_per_tenant()
    {
        await StartAsync();
        using var client = Client();

        using var started = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/start", "alpha",
            $$"""{"targetBaseUrl":"{{AddressOf(_upstreamAlpha)}}"}"""));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        using var alphaStatus = await client.SendAsync(For(HttpMethod.Get, "/__admin/recordings/status", "alpha"));
        Assert.Contains("Recording", await alphaStatus.Content.ReadAsStringAsync());

        using var betaStatus = await client.SendAsync(For(HttpMethod.Get, "/__admin/recordings/status", "beta"));
        Assert.Contains("Stopped", await betaStatus.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_tenants_record_at_once_against_their_own_upstreams()
    {
        await StartAsync();
        using var client = Client();

        using var alphaStart = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/start", "alpha",
            $$"""{"targetBaseUrl":"{{AddressOf(_upstreamAlpha)}}"}"""));
        Assert.Equal(HttpStatusCode.OK, alphaStart.StatusCode);

        // Starting beta's recording must not disturb alpha's — globally, this discarded alpha's
        // captures and repointed its traffic.
        using var betaStart = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/start", "beta",
            $$"""{"targetBaseUrl":"{{AddressOf(_upstreamBeta)}}"}"""));
        Assert.Equal(HttpStatusCode.OK, betaStart.StatusCode);

        using var alphaServed = await client.SendAsync(For(HttpMethod.Get, "/thing", "alpha"));
        Assert.Equal("from-alpha-upstream", await alphaServed.Content.ReadAsStringAsync());

        using var betaServed = await client.SendAsync(For(HttpMethod.Get, "/thing", "beta"));
        Assert.Equal("from-beta-upstream", await betaServed.Content.ReadAsStringAsync());

        using var alphaStopped = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/stop", "alpha"));
        var alphaStubs = JsonDocument.Parse(await alphaStopped.Content.ReadAsStringAsync())
            .RootElement.GetProperty("mappings");
        Assert.Equal(1, alphaStubs.GetArrayLength());
        Assert.Contains("from-alpha-upstream", alphaStubs.EnumerateArray().Single().ToString());

        // Each side captured only its own exchange.
        using var betaStopped = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/stop", "beta"));
        var betaStubs = JsonDocument.Parse(await betaStopped.Content.ReadAsStringAsync())
            .RootElement.GetProperty("mappings");
        Assert.Equal(1, betaStubs.GetArrayLength());
        Assert.Contains("from-beta-upstream", betaStubs.EnumerateArray().Single().ToString());
    }

    [Fact]
    public async Task Stopping_one_tenants_recording_leaves_the_other_recording()
    {
        await StartAsync();
        using var client = Client();

        foreach (var (tenant, upstream) in new[] { ("alpha", _upstreamAlpha), ("beta", _upstreamBeta) })
        {
            using var started = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/start", tenant,
                $$"""{"targetBaseUrl":"{{AddressOf(upstream)}}"}"""));
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        }

        using var stopped = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/stop", "alpha"));
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);

        using var alphaStatus = await client.SendAsync(For(HttpMethod.Get, "/__admin/recordings/status", "alpha"));
        Assert.Contains("Stopped", await alphaStatus.Content.ReadAsStringAsync());

        // The one thing a shared host cannot tolerate: someone else's "stop" ending your session.
        using var betaStatus = await client.SendAsync(For(HttpMethod.Get, "/__admin/recordings/status", "beta"));
        Assert.Contains("Recording", await betaStatus.Content.ReadAsStringAsync());

        using var betaServed = await client.SendAsync(For(HttpMethod.Get, "/thing", "beta"));
        Assert.Equal("from-beta-upstream", await betaServed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_snapshot_shows_only_the_asking_tenants_captures()
    {
        await StartAsync();
        using var client = Client();

        using var started = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/start", "alpha",
            $$"""{"targetBaseUrl":"{{AddressOf(_upstreamAlpha)}}"}"""));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);

        using var served = await client.SendAsync(For(HttpMethod.Get, "/thing", "alpha"));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        using var alphaSnapshot = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/snapshot", "alpha"));
        Assert.Equal(1, JsonDocument.Parse(await alphaSnapshot.Content.ReadAsStringAsync())
            .RootElement.GetProperty("mappings").GetArrayLength());

        // A tenant that never recorded sees nothing — not another tenant's captures, and not an error.
        using var betaSnapshot = await client.SendAsync(For(HttpMethod.Post, "/__admin/recordings/snapshot", "beta"));
        Assert.Equal(0, JsonDocument.Parse(await betaSnapshot.Content.ReadAsStringAsync())
            .RootElement.GetProperty("mappings").GetArrayLength());
    }
}
