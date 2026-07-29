using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire tests for #224: with <c>--tenant-credential</c>, the tenant header stops being a claim a
/// caller can rewrite. A tenant principal may only address its own tenant; the global admin
/// credential keeps the system scope; a host without the flag behaves exactly as before.
/// </summary>
public sealed class TenantCredentialTests : IAsyncLifetime
{
    private Microsoft.AspNetCore.Builder.WebApplication? _host;
    private HttpClient _client = null!;

    private static string Basic(string user, string pass) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build([
            "--port", "0",
            "--admin-user", "system", "--admin-pass", "root-pass",
            "--tenant-credential", "acme:acme-user:acme-pass",
            "--tenant-credential", "globex:globex-user:globex-pass"]);
        await _host.StartAsync();
        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            await _host.DisposeAsync();
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string authorization, string? tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        if (tenant is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenant);
        }

        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task A_tenant_principal_reaches_its_own_tenant_and_nothing_else()
    {
        using var own = await GetAsync("/__admin/mappings", Basic("acme-user", "acme-pass"), "acme");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // The substitution the issue is about: renaming the header no longer works.
        using var other = await GetAsync("/__admin/mappings", Basic("acme-user", "acme-pass"), "globex");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Contains("Admin.TenantForbidden", await other.Content.ReadAsStringAsync());

        // Including the sharpest case — the OTP route, which exists to reveal one-time codes.
        using var otp = await GetAsync("/__admin/messages/otp?recipient=x@y.z", Basic("acme-user", "acme-pass"), "globex");
        Assert.Equal(HttpStatusCode.Forbidden, otp.StatusCode);

        // Omitting the header addresses the default tenant, which this principal does not own either.
        using var implicitDefault = await GetAsync("/__admin/mappings", Basic("acme-user", "acme-pass"), null);
        Assert.Equal(HttpStatusCode.Forbidden, implicitDefault.StatusCode);
    }

    [Fact]
    public async Task The_system_credential_keeps_reaching_every_tenant_and_bad_credentials_are_401()
    {
        foreach (var tenant in new[] { "acme", "globex", "default" })
        {
            using var response = await GetAsync("/__admin/mappings", Basic("system", "root-pass"), tenant);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // A wrong password is authentication, not authorization: 401, never a 403 that would leak
        // that the tenant exists.
        using var wrong = await GetAsync("/__admin/mappings", Basic("acme-user", "not-the-pass"), "acme");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // Probes stay open (#218) even with tenant credentials configured.
        using var health = await _client.GetAsync("/__admin/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Writes_are_scoped_too_so_one_tenant_cannot_reset_another()
    {
        var stub = new StringContent(
            """{"request":{"method":"GET","urlPath":"/acme-only"},"response":{"status":200,"body":"acme"}}""",
            Encoding.UTF8, "application/json");
        using var create = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings") { Content = stub };
        create.Headers.TryAddWithoutValidation("Authorization", Basic("acme-user", "acme-pass"));
        create.Headers.Add("X-Mockifyr-Tenant", "acme");
        using var created = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The destructive route is refused across tenants.
        using var reset = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings/reset");
        reset.Headers.TryAddWithoutValidation("Authorization", Basic("globex-user", "globex-pass"));
        reset.Headers.Add("X-Mockifyr-Tenant", "acme");
        using var refused = await _client.SendAsync(reset);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // …and the stub is still there for its owner.
        using var list = await GetAsync("/__admin/mappings", Basic("acme-user", "acme-pass"), "acme");
        Assert.Contains("/acme-only", await list.Content.ReadAsStringAsync());
    }
}
