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
/// The partner self-service surface (#347): <c>/__sandbox/*</c>, reachable with the sandbox key a
/// partner already holds and answering only for that key's tenant. Mockifyr-specific, so a self-test;
/// no Docker.
/// </summary>
public sealed class SandboxSelfServiceTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;
    private string _acmeKey = string.Empty;
    private string _globexKey = string.Empty;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--sandbox-auth", "true"]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        _acmeKey = await IssueKey("acme");
        _globexKey = await IssueKey("globex");

        // One document per tenant, so "reads its own" and "cannot read another's" are both observable.
        await Admin(HttpMethod.Put, "/__admin/resources/orders/o-acme", "acme", """{"who":"acme"}""");
        await Admin(HttpMethod.Put, "/__admin/resources/orders/o-globex", "globex", """{"who":"globex"}""");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Admin(HttpMethod method, string path, string tenant, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _client!.SendAsync(request);
    }

    private async Task<string> IssueKey(string tenant)
    {
        using var response = await Admin(HttpMethod.Post, "/__admin/apikeys", tenant, $$"""{"name":"{{tenant}}-partner"}""");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString()!;
    }

    private async Task<(HttpStatusCode Status, string Body)> AsPartner(
        string key, HttpMethod method, string path, string? tenantHeader = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", key);
        if (tenantHeader is not null) request.Headers.Add("X-Mockifyr-Tenant", tenantHeader);
        using var response = await _client!.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_partner_reads_their_own_tenant_and_is_told_which_one_it_is()
    {
        var (status, body) = await AsPartner(_acmeKey, HttpMethod.Get, "/__sandbox/");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("acme", JsonDocument.Parse(body).RootElement.GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task A_partner_sees_their_own_documents_and_not_another_tenants()
    {
        var (status, mine) = await AsPartner(_acmeKey, HttpMethod.Get, "/__sandbox/resources/orders");

        Assert.Equal(HttpStatusCode.OK, status);
        // Positive first, so the absence below means something rather than meaning "the request failed".
        Assert.Contains("o-acme", mine, StringComparison.Ordinal);
        Assert.DoesNotContain("o-globex", mine, StringComparison.Ordinal);

        var (_, theirs) = await AsPartner(_globexKey, HttpMethod.Get, "/__sandbox/resources/orders");
        Assert.Contains("o-globex", theirs, StringComparison.Ordinal);
        Assert.DoesNotContain("o-acme", theirs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tenant_header_changes_nothing_because_this_surface_does_not_read_one()
    {
        // The strongest form of the scoping rule: not "a forged header is refused" but "there is no
        // header to forge". Naming another tenant is inert, not an error.
        var (status, body) = await AsPartner(_acmeKey, HttpMethod.Get, "/__sandbox/resources/orders", tenantHeader: "globex");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("o-acme", body, StringComparison.Ordinal);
        Assert.DoesNotContain("o-globex", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/__sandbox/")]
    [InlineData("/__sandbox/requests")]
    [InlineData("/__sandbox/messages")]
    [InlineData("/__sandbox/resources")]
    [InlineData("/__sandbox/environments")]
    public async Task No_key_is_a_401_on_every_route(string path)
    {
        using var response = await _client!.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_that_is_not_ours_is_a_401_rather_than_a_quiet_default_tenant()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await AsPartner("mfk_not-a-real-key", HttpMethod.Get, "/__sandbox/resources")).Status);
    }

    [Fact]
    public async Task The_admin_surface_still_ignores_a_sandbox_key_entirely()
    {
        // ADR 0011's binding criterion, re-asserted from this side. The point of standing a second
        // surface beside /__admin rather than teaching /__admin to accept these keys is that this stays
        // true — and true by construction, not by auditing a route list.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/apikeys");
        request.Headers.Add("X-Api-Key", _acmeKey);
        request.Headers.Add("X-Mockifyr-Tenant", "acme");

        using var response = await _client!.SendAsync(request);

        // The host under test has no admin credentials, so /__admin is open to anyone who can reach it —
        // which is exactly why the assertion is about the key granting nothing EXTRA, and the partner
        // surface exists at all.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_secret_environment_value_is_withheld_here_too()
    {
        await Admin(HttpMethod.Put, "/__admin/environments/signingKey", "acme",
            """{"activeValue":"live","values":[{"name":"live","value":"whsec-live-9c1f","secret":true}]}""");

        var (status, body) = await AsPartner(_acmeKey, HttpMethod.Get, "/__sandbox/environments");

        Assert.Equal(HttpStatusCode.OK, status);
        var key = JsonDocument.Parse(body).RootElement.GetProperty("environments").EnumerateArray().Single();
        // Positive: the key is visible, which is the useful half — a partner needs to know what their
        // sandbox points at. Then: the literal is not.
        Assert.Equal("signingKey", key.GetProperty("key").GetString());
        Assert.True(key.GetProperty("secret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, key.GetProperty("resolved").ValueKind);
        Assert.DoesNotContain("whsec-live-9c1f", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partner_resets_their_own_data_between_runs()
    {
        var (reset, _) = await AsPartner(_acmeKey, HttpMethod.Post, "/__sandbox/resources/reset");
        Assert.Equal(HttpStatusCode.OK, reset);

        var (_, mine) = await AsPartner(_acmeKey, HttpMethod.Get, "/__sandbox/resources/orders");
        Assert.DoesNotContain("o-acme", mine, StringComparison.Ordinal);

        // And only their own: the other tenant's documents are untouched, or "reset my sandbox" would
        // be the most destructive button on the platform.
        var (_, theirs) = await AsPartner(_globexKey, HttpMethod.Get, "/__sandbox/resources/orders");
        Assert.Contains("o-globex", theirs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_partner_cannot_reach_anything_that_is_not_theirs_to_reach()
    {
        // The surface is an allowlist, so a route that does not exist here answers 404 rather than
        // falling through to the mock-serving catch-all and pretending to be a stub miss.
        foreach (var path in (string[])["/__sandbox/mappings", "/__sandbox/recordings", "/__sandbox/apikeys"])
        {
            Assert.Equal(HttpStatusCode.NotFound, (await AsPartner(_acmeKey, HttpMethod.Get, path)).Status);
        }
    }
}

/// <summary>
/// The surface on a host that never enabled sandbox authentication (#347). Its own class because the
/// claim is about how the host was built, and that is decided once at startup.
/// </summary>
public sealed class SandboxSurfaceWithoutAuthTests : IAsyncLifetime
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
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Without_sandbox_auth_the_surface_is_absent_rather_than_open()
    {
        // There is no way to tell one partner from another without keys, so the namespace is not
        // mapped at all. It reaches the mock-serving catch-all like any other unmatched path — which
        // is a 404, and is what an unconfigured host should say about a route it does not have.
        using var response = await _client!.GetAsync("/__sandbox/resources");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
