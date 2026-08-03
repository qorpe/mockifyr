using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Differential.Generator;
using Mockifyr.Differential.Harness;
using Mockifyr.Server;
using Testcontainers.Redis;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Validation of Redis persistence (G16d): the fourth provider behind the <c>IStubPersistence</c> seam
/// — a key-value backend. Stub mutations made over the admin API against a host with <c>--redis</c>
/// persist to a Redis hash (in a real container) and are reloaded by a fresh host — surviving a
/// "restart" (the store outlives the app process). As before the reloaded stub's served response is
/// diffed against the oracle. Requires Docker.
/// </summary>
public sealed class G16dRedisPersistenceTests : IAsyncLifetime
{
    private const string StubJson =
        """{"request":{"method":"GET","url":"/redis"},"response":{"status":200,"body":"in-redis"}}""";

    private static readonly RequestSpec Request = new() { Method = "GET", Url = "/redis" };

    private readonly WireMockOracle _oracle = new();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public async Task InitializeAsync()
    {
        await _oracle.StartAsync();
        await _redis.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _redis.DisposeAsync();
        await _oracle.DisposeAsync();
    }

    [Fact]
    public async Task SandboxResources_SurviveRestart()
    {
        var connectionString = _redis.GetConnectionString();;

        await using (var hostA = await StartHostAsync(connectionString))
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

        // A fresh host against the same backend: what a partner seeded is still there, and what they
        // deleted stays deleted — persisting creates but not deletes is the classic half-fix.
        await using var hostB = await StartHostAsync(connectionString);
        using var clientB = Client(hostB);

        using var deleted = await clientB.GetAsync("/__admin/resources/orders/A-1");
        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);

        using var alive = await clientB.GetAsync("/__admin/resources/orders/A-2");
        Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        Assert.Contains("\"total\"", await alive.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreatedStub_SurvivesRestart_AndMatchesOracle()
    {
        var connectionString = _redis.GetConnectionString();

        await using (var hostA = await StartHostAsync(connectionString))
        {
            using var client = Client(hostA);
            using var content = new StringContent(StubJson, Encoding.UTF8, "application/json");
            var created = await client.PostAsync("/__admin/mappings", content);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        await using var hostB = await StartHostAsync(connectionString);
        using var clientB = Client(hostB);
        using var served = await clientB.GetAsync("/redis");

        await _oracle.ResetAsync();
        await _oracle.LoadMappingAsync(StubJson);
        var oracle = await _oracle.SendAsync(Request);

        Assert.Equal(oracle.Status, (int)served.StatusCode);
        var body = await served.Content.ReadAsByteArrayAsync();
        Assert.True(oracle.Body.AsSpan().SequenceEqual(body),
            $"body oracle=\"{Encoding.UTF8.GetString(oracle.Body)}\" mockifyr=\"{Encoding.UTF8.GetString(body)}\"");
    }

    [Fact]
    public async Task DeletedStub_And_Reset_StayGoneAfterRestart()
    {
        var connectionString = _redis.GetConnectionString();

        await using (var host = await StartHostAsync(connectionString))
        {
            using var client = Client(host);
            using var content = new StringContent(StubJson, Encoding.UTF8, "application/json");
            var created = await client.PostAsync("/__admin/mappings", content);
            var id = (await System.Text.Json.JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync()))
                .RootElement.GetProperty("id").GetGuid();

            using var deleted = await client.DeleteAsync($"/__admin/mappings/{id}");
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }

        await using var restarted = await StartHostAsync(connectionString);
        using var clientR = Client(restarted);
        using var response = await clientR.GetAsync("/redis");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<WebApplication> StartHostAsync(string connectionString)
    {
        var app = MockifyrHost.Build(["--port", "0", "--redis", connectionString]);
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
