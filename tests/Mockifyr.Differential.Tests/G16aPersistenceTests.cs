using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Differential.Generator;
using Mockifyr.Differential.Harness;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Validation of file-based persistence (G16a): stub mutations made over the admin API against a host
/// with <c>--root-dir</c> are written to <c>&lt;root&gt;/mappings</c> and reloaded by a fresh host — so
/// they survive a "restart". Durability is infrastructure (no WireMock-semantic novelty), but the
/// reloaded stub's <em>served response</em> is still diffed against the oracle so parity is proven, not
/// assumed. Requires Docker.
/// </summary>
public sealed class G16aPersistenceTests : IAsyncLifetime
{
    private const string StubJson =
        """{"request":{"method":"GET","url":"/persisted"},"response":{"status":200,"body":"durable"}}""";

    private static readonly RequestSpec Request = new() { Method = "GET", Url = "/persisted" };

    private readonly WireMockOracle _oracle = new();

    public Task InitializeAsync() => _oracle.StartAsync();

    public async Task DisposeAsync() => await _oracle.DisposeAsync();

    [Fact]
    public async Task CreatedStub_SurvivesRestart_AndMatchesOracle()
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-persist-");
        try
        {
            // Host A: create the stub over the admin API, then shut down.
            await using (var hostA = await StartHostAsync(root.FullName))
            {
                using var client = Client(hostA);
                using var content = new StringContent(StubJson, Encoding.UTF8, "application/json");
                var created = await client.PostAsync("/__admin/mappings", content);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            // It was written to <root>/mappings as a WireMock JSON file.
            var files = Directory.GetFiles(Path.Combine(root.FullName, "mappings"), "*.json");
            Assert.Single(files);

            // Host B: a fresh host on the same root reloads and serves it.
            await using var hostB = await StartHostAsync(root.FullName);
            using var clientB = Client(hostB);
            using var served = await clientB.GetAsync("/persisted");

            // ...and the reloaded response matches what the oracle serves for the same mapping.
            await _oracle.ResetAsync();
            await _oracle.LoadMappingAsync(StubJson);
            var oracle = await _oracle.SendAsync(Request);

            Assert.Equal(oracle.Status, (int)served.StatusCode);
            var body = await served.Content.ReadAsByteArrayAsync();
            Assert.True(oracle.Body.AsSpan().SequenceEqual(body),
                $"body oracle=\"{Encoding.UTF8.GetString(oracle.Body)}\" mockifyr=\"{Encoding.UTF8.GetString(body)}\"");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeletedStub_And_Reset_StayGoneAfterRestart()
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-persist-del-");
        try
        {
            Guid id;
            await using (var host = await StartHostAsync(root.FullName))
            {
                using var client = Client(host);
                using var content = new StringContent(StubJson, Encoding.UTF8, "application/json");
                var created = await client.PostAsync("/__admin/mappings", content);
                id = (await System.Text.Json.JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync()))
                    .RootElement.GetProperty("id").GetGuid();

                // Delete it — the persisted file should be removed too.
                using var deleted = await client.DeleteAsync($"/__admin/mappings/{id}");
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            }

            Assert.Empty(Directory.GetFiles(Path.Combine(root.FullName, "mappings"), "*.json"));

            // A fresh host does not serve the deleted stub.
            await using var restarted = await StartHostAsync(root.FullName);
            using var clientR = Client(restarted);
            using var response = await clientR.GetAsync("/persisted");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// #467 on a real host: a non-default tenant's stub created over the admin API is written to
    /// <c>mappings/&lt;tenant&gt;/</c> and a FRESH host serves it — to that tenant only — while a directory a person
    /// made beside it does not become a tenant. A self-test (no oracle: tenancy is Mockifyr's own).
    /// </summary>
    [Fact]
    public async Task A_tenants_stub_survives_a_restart_and_a_handmade_directory_does_not_become_a_tenant()
    {
        var root = Directory.CreateTempSubdirectory("mockifyr-persist-tenant-");
        try
        {
            await using (var hostA = await StartHostAsync(root.FullName))
            {
                using var client = Client(hostA);
                using var create = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
                {
                    Content = new StringContent(StubJson, Encoding.UTF8, "application/json"),
                };
                create.Headers.Add("X-Mockifyr-Tenant", "acme");
                Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(create)).StatusCode);
            }

            Assert.Single(Directory.GetFiles(Path.Combine(root.FullName, "mappings", "acme"), "*.json"));
            var handmade = Directory.CreateDirectory(Path.Combine(root.FullName, "mappings", "handmade"));
            File.WriteAllText(Path.Combine(handmade.FullName, "orders.json"), StubJson);

            await using var hostB = await StartHostAsync(root.FullName);
            using var clientB = Client(hostB);

            using var asAcme = new HttpRequestMessage(HttpMethod.Get, "/persisted");
            asAcme.Headers.Add("X-Mockifyr-Tenant", "acme");
            using var served = await clientB.SendAsync(asAcme);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal("durable", await served.Content.ReadAsStringAsync());

            using var asDefault = await clientB.GetAsync("/persisted");   // the default tenant never had it
            Assert.Equal(HttpStatusCode.NotFound, asDefault.StatusCode);

            using var asHandmade = new HttpRequestMessage(HttpMethod.Get, "/persisted");
            asHandmade.Headers.Add("X-Mockifyr-Tenant", "handmade");
            using var notATenant = await clientB.SendAsync(asHandmade);
            Assert.Equal(HttpStatusCode.NotFound, notATenant.StatusCode);
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
