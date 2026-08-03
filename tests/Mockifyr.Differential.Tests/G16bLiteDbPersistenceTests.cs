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
/// Validation of LiteDB persistence (G16b): the second provider behind the <c>IStubPersistence</c> seam.
/// Stub mutations made over the admin API against a host with <c>--litedb</c> persist to an embedded
/// single-file database and are reloaded by a fresh host — so they survive a "restart". As in G16a the
/// reloaded stub's served response is diffed against the oracle, so parity is proven. Requires Docker.
/// </summary>
public sealed class G16bLiteDbPersistenceTests : IAsyncLifetime
{
    private const string StubJson =
        """{"request":{"method":"GET","url":"/lite"},"response":{"status":200,"body":"in-litedb"}}""";

    private static readonly RequestSpec Request = new() { Method = "GET", Url = "/lite" };

    private readonly WireMockOracle _oracle = new();

    public Task InitializeAsync() => _oracle.StartAsync();

    public async Task DisposeAsync() => await _oracle.DisposeAsync();

    [Fact]
    public async Task CreatedStub_SurvivesRestart_AndMatchesOracle()
    {
        var dir = Directory.CreateTempSubdirectory("mockifyr-litedb-");
        var dbPath = Path.Combine(dir.FullName, "stubs.db");
        try
        {
            // Host A: create the stub over the admin API, then shut down (flushes + closes the db file).
            await using (var hostA = await StartHostAsync(dbPath))
            {
                using var client = Client(hostA);
                using var content = new StringContent(StubJson, Encoding.UTF8, "application/json");
                var created = await client.PostAsync("/__admin/mappings", content);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            Assert.True(File.Exists(dbPath), "the LiteDB file was not created");

            // Host B: a fresh host on the same db reloads and serves it.
            await using var hostB = await StartHostAsync(dbPath);
            using var clientB = Client(hostB);
            using var served = await clientB.GetAsync("/lite");

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
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SandboxResources_SurviveRestart()
    {
        var dir = Directory.CreateTempSubdirectory("mockifyr-litedb-res-");
        var dbPath = Path.Combine(dir.FullName, "stubs.db");
        try
        {
            await using (var hostA = await StartHostAsync(dbPath))
            {
                using var client = Client(hostA);
                using var body = new StringContent("""{"total":10}""", Encoding.UTF8, "application/json");
                using var put = await client.PutAsync("/__admin/resources/orders/A-1", body);
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);

                using var gone = await client.DeleteAsync("/__admin/resources/orders/A-1");
                Assert.Equal(HttpStatusCode.OK, gone.StatusCode);

                using var kept = new StringContent("""{"total":20}""", Encoding.UTF8, "application/json");
                using var second = await client.PutAsync("/__admin/resources/orders/A-2", kept);
                Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            }

            // A fresh host on the same database: what a partner seeded is still there, and what they
            // deleted stays deleted — persisting creates but not deletes is the classic half-fix.
            await using var hostB = await StartHostAsync(dbPath);
            using var clientB = Client(hostB);

            using var deleted = await clientB.GetAsync("/__admin/resources/orders/A-1");
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);

            using var alive = await clientB.GetAsync("/__admin/resources/orders/A-2");
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
            Assert.Contains("\"total\"", await alive.Content.ReadAsStringAsync());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeletedStub_And_Reset_StayGoneAfterRestart()
    {
        var dir = Directory.CreateTempSubdirectory("mockifyr-litedb-del-");
        var dbPath = Path.Combine(dir.FullName, "stubs.db");
        try
        {
            await using (var host = await StartHostAsync(dbPath))
            {
                using var client = Client(host);
                using var content = new StringContent(StubJson, Encoding.UTF8, "application/json");
                var created = await client.PostAsync("/__admin/mappings", content);
                var id = (await System.Text.Json.JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync()))
                    .RootElement.GetProperty("id").GetGuid();

                using var deleted = await client.DeleteAsync($"/__admin/mappings/{id}");
                Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            }

            // A fresh host does not serve the deleted stub.
            await using var restarted = await StartHostAsync(dbPath);
            using var clientR = Client(restarted);
            using var response = await clientR.GetAsync("/lite");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static async Task<WebApplication> StartHostAsync(string dbPath)
    {
        var app = MockifyrHost.Build(["--port", "0", "--litedb", dbPath]);
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
