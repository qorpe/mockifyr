using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// A renameable tenant header (#396), at the wire. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
/// <remarks>
/// The case that matters is not "the new name works" — it is that the OLD name stops working. A
/// facade that kept its own constant would keep answering to <c>X-Mockifyr-Tenant</c>, and the
/// symptom would be one tenant's stubs serving another's calls: no error, no log line, just the
/// wrong data. So every assertion here is paired.
/// </remarks>
public sealed class TenantHeaderWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient _client = null!;

    private const string Renamed = "X-Team";

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--tenant-header", Renamed]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? tenantHeader = null, string? tenant = null, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (tenantHeader is not null && tenant is not null)
        {
            request.Headers.Add(tenantHeader, tenant);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _client.SendAsync(request);
    }

    private const string Stub =
        """{"request":{"method":"GET","urlPath":"/who"},"response":{"status":200,"body":"acme"}}""";

    [Fact]
    public async Task The_admin_surface_reads_the_configured_header()
    {
        using var created = await SendAsync(HttpMethod.Post, "/__admin/mappings", Renamed, "acme", Stub);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var listed = await SendAsync(HttpMethod.Get, "/__admin/mappings", Renamed, "acme");
        var mappings = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("mappings");
        Assert.Equal(1, mappings.GetArrayLength());
    }

    [Fact]
    public async Task The_old_header_no_longer_names_a_tenant()
    {
        // The whole point of the rename. Written into `acme` through the configured header, then read
        // back through the historical one: if any facade kept its own constant this finds the stub,
        // and a renamed host would be quietly serving cross-tenant.
        using var created = await SendAsync(HttpMethod.Post, "/__admin/mappings", Renamed, "acme", Stub);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var listed = await SendAsync(HttpMethod.Get, "/__admin/mappings", "X-Mockifyr-Tenant", "acme");
        var mappings = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("mappings");

        // The old name is now just an unrecognised header, so the request is the default tenant's.
        Assert.Equal(0, mappings.GetArrayLength());
    }

    [Fact]
    public async Task The_serving_path_reads_the_configured_header_too()
    {
        // The admin surface and the mock surface must agree, or a stub is created somewhere it can
        // never be served from.
        using var created = await SendAsync(HttpMethod.Post, "/__admin/mappings", Renamed, "acme", Stub);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var served = await SendAsync(HttpMethod.Get, "/who", Renamed, "acme");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("acme", await served.Content.ReadAsStringAsync());

        using var wrongHeader = await SendAsync(HttpMethod.Get, "/who", "X-Mockifyr-Tenant", "acme");
        Assert.Equal(HttpStatusCode.NotFound, wrongHeader.StatusCode);
    }

    [Fact]
    public async Task No_header_still_means_the_default_tenant()
    {
        // Renaming the header does not change what its absence means.
        using var created = await SendAsync(HttpMethod.Post, "/__admin/mappings", body: Stub);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var served = await SendAsync(HttpMethod.Get, "/who");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    public void A_malformed_header_name_is_refused_at_startup()
    {
        // Not rejected by the framework, merely never matched: the host would start, every request
        // would fall back to the default tenant, and the symptom points nowhere near the flag.
        var thrown = Assert.Throws<InvalidOperationException>(
            () => MockifyrHost.Build(["--port", "0", "--https-port", "0", "--tenant-header", "X Team"]));

        Assert.Contains("not a legal HTTP header name", thrown.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// An unconfigured host answers exactly as it always did — the compatibility criterion for #396.
/// </summary>
public sealed class TenantHeaderDefaultWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0"]);
        await _host.StartAsync();
        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task The_historical_header_still_names_the_tenant()
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
        {
            Content = new StringContent(
                """{"request":{"method":"GET","urlPath":"/who"},"response":{"status":200,"body":"acme"}}""",
                Encoding.UTF8, "application/json"),
        };
        create.Headers.Add("X-Mockifyr-Tenant", "acme");
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var serve = new HttpRequestMessage(HttpMethod.Get, "/who");
        serve.Headers.Add("X-Mockifyr-Tenant", "acme");
        using var served = await _client.SendAsync(serve);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("acme", await served.Content.ReadAsStringAsync());
    }
}
