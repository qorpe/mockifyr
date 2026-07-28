using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire test for #218: with <c>--admin-user</c>/<c>--admin-pass</c> set, <c>/__admin/health</c> must
/// stay reachable WITHOUT credentials — Kubernetes/OpenShift probes cannot carry them, and a 401
/// health check sends the pod into a restart loop. Every other admin path stays guarded.
/// </summary>
public sealed class HealthProbeAuthTests : IAsyncLifetime
{
    private Microsoft.AspNetCore.Builder.WebApplication? _host;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--admin-user", "op", "--admin-pass", "secret"]);
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

    [Fact]
    public async Task Health_answers_probes_without_credentials_while_the_rest_stays_guarded()
    {
        // The probe path: no Authorization header, any casing K8s might be configured with.
        using var health = await _client.GetAsync("/__admin/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("\"name\"", await health.Content.ReadAsStringAsync());

        using var cased = await _client.GetAsync("/__ADMIN/HEALTH");
        Assert.Equal(HttpStatusCode.OK, cased.StatusCode);

        // The exemption is EXACT — a sibling path or a suffixed path is still guarded.
        using var mappings = await _client.GetAsync("/__admin/mappings");
        Assert.Equal(HttpStatusCode.Unauthorized, mappings.StatusCode);
        using var suffixed = await _client.GetAsync("/__admin/health/../mappings");
        Assert.Equal(HttpStatusCode.Unauthorized, suffixed.StatusCode);

        // Credentials still work everywhere, health included.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/mappings");
        request.Headers.TryAddWithoutValidation(
            "Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("op:secret")));
        using var authed = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
    }
}
