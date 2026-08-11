using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Request body limits at the wire (#349): a host ceiling, a per-tenant value beneath it, and a
/// refusal that names which of the two was hit. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class RequestBodyLimitWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build([
            "--port", "0", "--https-port", "0",
            "--max-request-body-bytes", "2000",
            "--tenant-max-request-body", "small:200",
        ]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        // The same stub in three tenants, because the interesting assertions are comparisons between
        // them: an identical request has to be able to succeed for one and be refused for another.
        foreach (var tenant in (string?[])[null, "small", "other"])
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = new StringContent(
                    """{"request":{"method":"POST","url":"/echo"},"response":{"status":200,"body":"ok"}}""",
                    Encoding.UTF8, "application/json"),
            };
            if (tenant is not null) request.Headers.Add("X-Mockifyr-Tenant", tenant);

            using var stub = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<(HttpStatusCode Status, string Body)> Post(int bytes, string? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/echo")
        {
            Content = new StringContent(new string('x', bytes), Encoding.UTF8, "text/plain"),
        };
        if (tenant is not null) request.Headers.Add("X-Mockifyr-Tenant", tenant);

        using var response = await _client!.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_body_under_the_ceiling_is_served_normally()
    {
        // The positive first: without this, every refusal below could be the host rejecting everything.
        var (status, body) = await Post(500);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", body);
    }

    [Fact]
    public async Task A_body_over_the_host_ceiling_is_refused_and_says_so()
    {
        var (status, body) = await Post(3000);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Contains("host's limit", body, StringComparison.Ordinal);
        Assert.Contains("2000", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tenant_held_lower_is_refused_at_its_own_limit_and_told_which()
    {
        // 500 bytes is fine for everybody else and too much for this tenant — so the refusal has to
        // name the tenant, or an operator goes and raises the host number that was never in the way.
        var (status, body) = await Post(500, tenant: "small");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Contains("tenant 'small'", body, StringComparison.Ordinal);
        Assert.Contains("200", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_body_still_passes_for_a_tenant_that_is_not_held_lower()
    {
        // The pair that proves the limit is per tenant rather than a host-wide number that happens to
        // be small: identical request, different tenant, different answer.
        Assert.Equal(HttpStatusCode.OK, (await Post(500, tenant: "other")).Status);
    }

    [Fact]
    public async Task A_body_that_declares_no_length_is_still_stopped()
    {
        // The explanatory check reads Content-Length; a chunked body has none, so only Kestrel's own
        // per-request limit stands between this host and an unbounded read. It answers 413 without our
        // message, which is the honest trade — a bare refusal beats no refusal.
        using var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 5000))));
        content.Headers.ContentLength = null;
        content.Headers.Add("Content-Type", "text/plain");

        using var response = await _client!.PostAsync("/echo", content);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>
/// The same host with no limits configured (#349) — the claim that an unconfigured host is unchanged.
/// </summary>
public sealed class RequestBodyWithoutLimitsTests : IAsyncLifetime
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
            """{"request":{"method":"POST","url":"/echo"},"response":{"status":200,"body":"ok"}}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task A_body_far_larger_than_any_configured_limit_is_served_as_before()
    {
        using var response = await _client!.PostAsync(
            "/echo", new StringContent(new string('x', 100_000), Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
