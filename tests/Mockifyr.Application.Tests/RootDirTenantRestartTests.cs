using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Application.Tests;

/// <summary>
/// #467 wired, the verifier's probe on PR #483: a real <c>--root-dir</c> host, a stub created over the admin API under a
/// tenant whose name is not a plain path segment, then a FRESH host. Each one is served to its tenant again, and nothing
/// is written outside <c>mappings/</c>. A self-test: tenancy is Mockifyr's own, so no oracle has an opinion.
/// </summary>
public sealed class RootDirTenantRestartTests
{
    private const string StubJson =
        """{"request":{"method":"GET","url":"/persisted"},"response":{"status":200,"body":"durable"}}""";

    [Theory]
    [InlineData("Acme Corp")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("/tmp/x")]
    [InlineData("a%2Fb")]
    public async Task A_tenants_stub_survives_a_restart_whatever_its_name(string tenant)
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-root-tenant-");
        try
        {
            await using (var first = await StartHostAsync(root.FullName))
            {
                using var client = Client(first);
                using var create = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
                {
                    Content = new StringContent(StubJson, Encoding.UTF8, "application/json"),
                };
                create.Headers.Add("X-Mockifyr-Tenant", tenant);
                Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(create)).StatusCode);
            }

            // Nothing beside mappings/ but what the host itself keeps, and the tenant is one directory directly inside it.
            Assert.DoesNotContain(Directory.GetDirectories(root.FullName), d => Path.GetFileName(d) is "escape" or "tmp" or "x");
            var mappings = Path.Combine(root.FullName, "mappings");
            Assert.Single(Directory.GetDirectories(mappings));
            Assert.Empty(Directory.GetDirectories(Directory.GetDirectories(mappings)[0]));

            await using var second = await StartHostAsync(root.FullName);
            using var again = Client(second);
            using var asTenant = new HttpRequestMessage(HttpMethod.Get, "/persisted");
            asTenant.Headers.Add("X-Mockifyr-Tenant", tenant);
            using var served = await again.SendAsync(asTenant);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal("durable", await served.Content.ReadAsStringAsync());
            using var asDefault = await again.GetAsync("/persisted");
            Assert.Equal(HttpStatusCode.NotFound, asDefault.StatusCode);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task<WebApplication> StartHostAsync(string root)
    {
        var app = MockifyrHost.Build(["--port", "0", "--root-dir", root]);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
