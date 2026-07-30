using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for the Kubernetes probe split (#242): liveness and readiness answer different
/// questions, both stay outside admin auth, and readiness turns off when the host starts draining so
/// a rolling update takes the pod out of rotation before in-flight work finishes.
/// </summary>
public sealed class ProbeEndpointTests
{
    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication Host, HttpClient Client)> StartAsync(params string[] args)
    {
        var host = MockifyrHost.Build([.. new[] { "--port", "0" }.Concat(args)]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }

    [Fact]
    public async Task A_started_host_is_alive_and_ready()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var live = await client.GetAsync("/__admin/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Contains("alive", await live.Content.ReadAsStringAsync());

            using var ready = await client.GetAsync("/__admin/ready");
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            Assert.Contains("ready", await ready.Content.ReadAsStringAsync());

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Draining_fails_readiness_while_liveness_still_answers()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            // What ApplicationStopping does on a rolling update: stop being routable, stay alive.
            host.Services.GetRequiredService<HostReadiness>().BeginDraining();

            using var ready = await client.GetAsync("/__admin/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Contains("draining", await ready.Content.ReadAsStringAsync());

            // Liveness must NOT fail here — a failing liveness probe would have the orchestrator
            // kill the pod instead of letting it finish serving.
            using var live = await client.GetAsync("/__admin/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);

            // And serving still works while draining.
            using var stub = await client.PostAsync("/__admin/mappings", new StringContent(
                """{"request":{"method":"GET","urlPath":"/still"},"response":{"status":200,"body":"served"}}""",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
            Assert.Equal("served", await client.GetStringAsync("/still"));

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Probes_stay_open_when_the_admin_api_requires_credentials()
    {
        var (host, client) = await StartAsync("--admin-user", "op", "--admin-pass", "secret");
        await using (host)
        {
            // A kubelet cannot attach credentials; a 401 probe would restart-loop the pod (#218).
            foreach (var path in new[] { "/__admin/live", "/__admin/ready", "/__admin/health" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            // Everything else stays guarded.
            using var guarded = await client.GetAsync("/__admin/mappings");
            Assert.Equal(HttpStatusCode.Unauthorized, guarded.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Probes_are_also_open_under_per_tenant_credentials()
    {
        var (host, client) = await StartAsync("--admin-user", "op", "--admin-pass", "secret",
            "--tenant-credential", "acme:acme-user:acme-pass");
        await using (host)
        {
            foreach (var path in new[] { "/__admin/live", "/__admin/ready" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            await host.StopAsync();
            client.Dispose();
        }
    }
}
