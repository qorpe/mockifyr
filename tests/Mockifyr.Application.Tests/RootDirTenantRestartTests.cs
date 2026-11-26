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

    /// <summary>
    /// The same class on the resources provider (verifier, PR #483, second round): it escaped the tenant with its own
    /// encoder and read the ESCAPED name back as the tenant, so <c>Acme Corp</c> came back as <c>Acme%20Corp</c> and its
    /// documents answered 404 after a restart.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("Acme Corp")]
    [InlineData("a*b")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a%2Fb")]
    public async Task A_tenants_resource_survives_a_restart_whatever_its_name(string tenant)
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-root-resource-");
        try
        {
            await using (var first = await StartHostAsync(root.FullName))
            {
                using var client = Client(first);
                Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, "/__admin/resources/orders/42", tenant, """{"n":42}""")).StatusCode);
            }

            var resources = Path.Combine(root.FullName, "resources");
            Assert.DoesNotContain(Directory.GetDirectories(root.FullName), d => Path.GetFileName(d) is "escape");
            Assert.Equal(TenantPathSegment.Encode(new Mockifyr.Core.TenantId(tenant)), Path.GetFileName(Assert.Single(Directory.GetDirectories(resources))));

            await using var second = await StartHostAsync(root.FullName);
            using var again = Client(second);
            using var read = await SendAsync(again, HttpMethod.Get, "/__admin/resources/orders/42", tenant);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Contains("42", await read.Content.ReadAsStringAsync());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Upgrading from the release before #467, for every file provider that keeps a directory per tenant: the earlier
    /// writer's directory is read as its tenant and renamed once to this release's name, so a delete after the upgrade
    /// reaches the very file the reload read, and nothing comes back on the restart after that.
    /// </summary>
    [Theory]
    [InlineData("resources", "Acme Corp", "Acme%20Corp")] // the resources provider's own escape
    [InlineData("resources", "a b*c", "a%20b%2Ac")]
    [InlineData("resources", "..", "%2E%2E")]
    [InlineData("environments", "a*b", "a*b")]            // the raw tenant value
    [InlineData("environments", "a%41", "a%41")]          // a raw name that holds an escape this release would not write
    [InlineData("mappings", "a*b", "a*b")]
    [InlineData("mappings", "x:y", "x:y")]
    public async Task A_directory_an_earlier_release_wrote_is_read_as_its_tenant_and_a_delete_after_upgrade_holds(
        string area, string tenant, string earlierName)
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-root-upgrade-");
        try
        {
            // Write through this release under a stand-in tenant, then give its directory the earlier release's name.
            await using (var first = await StartHostAsync(root.FullName))
            {
                using var client = Client(first);
                var created = area switch
                {
                    "resources" => await SendAsync(client, HttpMethod.Put, "/__admin/resources/orders/42", "standin", """{"n":42}"""),
                    "environments" => await SendAsync(client, HttpMethod.Put, "/__admin/environments/apiHost", "standin",
                        """{"activeValue":"main","values":[{"name":"main","value":"https://kept.example"}]}"""),
                    _ => await SendAsync(client, HttpMethod.Post, "/__admin/mappings", "standin", StubJson),
                };
                Assert.True(created.IsSuccessStatusCode, $"{created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
            }

            Directory.Move(Path.Combine(root.FullName, area, "standin"), Path.Combine(root.FullName, area, earlierName));
            File.Delete(Path.Combine(root.FullName, area, earlierName, TenantPathSegment.MarkerFileName)); // an earlier release wrote none

            string? stubId = null;
            await using (var second = await StartHostAsync(root.FullName))
            {
                using var client = Client(second);
                using var read = await SendAsync(client, HttpMethod.Get, ReadPath(area), tenant);
                var body = await read.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.Contains(area switch { "resources" => "42", "environments" => "kept.example", _ => "durable" }, body);
                Assert.True(Directory.Exists(Path.Combine(root.FullName, area, TenantPathSegment.Encode(new Mockifyr.Core.TenantId(tenant)))));
                Assert.Equal(earlierName == TenantPathSegment.Encode(new Mockifyr.Core.TenantId(tenant)),
                    Directory.Exists(Path.Combine(root.FullName, area, earlierName)));

                if (area == "mappings")
                {
                    using var list = await SendAsync(client, HttpMethod.Get, "/__admin/mappings", tenant);
                    stubId = System.Text.Json.JsonDocument.Parse(await list.Content.ReadAsStringAsync())
                        .RootElement.GetProperty("mappings")[0].GetProperty("id").GetString();
                }

                var deletePath = area switch
                {
                    "resources" => "/__admin/resources/orders/42",
                    "environments" => "/__admin/environments/apiHost",
                    _ => $"/__admin/mappings/{stubId}",
                };
                using var deleted = await SendAsync(client, HttpMethod.Delete, deletePath, tenant);
                Assert.True(deleted.IsSuccessStatusCode, $"{deleted.StatusCode}");
            }

            await using var third = await StartHostAsync(root.FullName);
            using var afterDelete = Client(third);
            using var gone = await SendAsync(afterDelete, HttpMethod.Get, ReadPath(area), tenant);
            if (area == "environments")
            {
                Assert.DoesNotContain("kept.example", await gone.Content.ReadAsStringAsync());
            }
            else
            {
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string ReadPath(string area) => area switch
    {
        "resources" => "/__admin/resources/orders/42",
        "environments" => "/__admin/environments",
        _ => "/persisted",
    };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string tenant, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        return await client.SendAsync(request);
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
