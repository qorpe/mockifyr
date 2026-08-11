using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// CORS at the wire (#349): a browser application may call the sandbox from a configured origin, the
/// admin API stays same-origin, and an unconfigured host emits nothing. Mockifyr-specific, so a
/// self-test; no Docker.
/// </summary>
public sealed class CorsWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build([
            "--port", "0", "--https-port", "0",
            "--allow-origin", "https://app.example",
            "--tenant-allow-origin", "acme=https://acme.example",
        ]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        using var stub = await _client.PostAsync("/__admin/mappings", new StringContent(
            """{"request":{"method":"GET","url":"/orders"},"response":{"status":200,"body":"[]"}}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Send(
        HttpMethod method, string path, string? origin = null, string? tenant = null, string? requestHeaders = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (tenant is not null) request.Headers.Add("X-Mockifyr-Tenant", tenant);
        if (requestHeaders is not null) request.Headers.Add("Access-Control-Request-Headers", requestHeaders);
        return await _client!.SendAsync(request);
    }

    private static string? AllowOrigin(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.First() : null;

    [Fact]
    public async Task An_allowed_origin_gets_the_header_and_the_response()
    {
        using var response = await Send(HttpMethod.Get, "/orders", origin: "https://app.example");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Echoed, not '*': '*' is incompatible with credentials, and a sandbox key travels as one.
        Assert.Equal("https://app.example", AllowOrigin(response));
        Assert.Contains("Origin", response.Headers.Vary, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_origin_nobody_allowed_gets_no_header_and_still_gets_the_response()
    {
        // No headers rather than a refusal: the browser is what enforces CORS, and answering 403 here
        // would break every non-browser client for a rule that does not apply to them.
        using var response = await Send(HttpMethod.Get, "/orders", origin: "https://evil.example");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(AllowOrigin(response));
    }

    [Fact]
    public async Task Preflight_is_answered_rather_than_falling_through_to_a_404()
    {
        // The serving catch-all would 404 an OPTIONS, and a 404 preflight is indistinguishable to the
        // developer on the other side from "CORS is broken".
        using var response = await Send(
            HttpMethod.Options, "/orders", origin: "https://app.example", requestHeaders: "X-Api-Key");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("https://app.example", AllowOrigin(response));
        Assert.Contains("GET", response.Headers.GetValues("Access-Control-Allow-Methods").First(), StringComparison.Ordinal);
        // The requested headers are echoed, so a key or a content type the caller needs is permitted.
        Assert.Contains("X-Api-Key", response.Headers.GetValues("Access-Control-Allow-Headers").First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tenants_own_list_replaces_the_host_wide_one()
    {
        // acme named its origins, so it means the whole set: the shared entry does not silently apply.
        using var own = await Send(HttpMethod.Get, "/orders", origin: "https://acme.example", tenant: "acme");
        Assert.Equal("https://acme.example", AllowOrigin(own));

        using var shared = await Send(HttpMethod.Get, "/orders", origin: "https://app.example", tenant: "acme");
        Assert.Null(AllowOrigin(shared));

        // …and another tenant still inherits it.
        using var other = await Send(HttpMethod.Get, "/orders", origin: "https://app.example", tenant: "globex");
        Assert.Equal("https://app.example", AllowOrigin(other));
    }

    [Fact]
    public async Task The_admin_api_stays_same_origin()
    {
        // An operator's browser reaches the admin API from the dashboard that served it. Handing a
        // configured sandbox origin credentialed access to the control plane is the one thing this
        // feature must not quietly do.
        using var response = await Send(HttpMethod.Get, "/__admin/mappings", origin: "https://app.example");

        Assert.Null(AllowOrigin(response));
    }

    [Fact]
    public async Task The_partner_surface_is_included_because_a_browser_partner_needs_it()
    {
        // Leaving /__sandbox out would mean a browser application could call the mock and still not
        // read its own OTP — the gap #347 closed, reopened at the edge.
        using var response = await Send(HttpMethod.Get, "/__sandbox/messages", origin: "https://app.example");

        Assert.Equal("https://app.example", AllowOrigin(response));
    }
}

/// <summary>The same host with no origins configured (#349): nothing is emitted, nothing changes.</summary>
public sealed class CorsWithoutOriginsTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0"]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        using var stub = await _client.PostAsync("/__admin/mappings", new StringContent(
            """{"request":{"method":"GET","url":"/orders"},"response":{"status":200,"body":"[]"}}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task An_unconfigured_host_emits_no_cors_headers_at_all()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders");
        request.Headers.Add("Origin", "https://app.example");

        using var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
