using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Startup reload covers every tenant on a backend that spans tenants (#466). A host on a database that
/// already held stubs for non-default tenants used to load only the default tenant's: the rows were still
/// there, the tenants' keys still authenticated, and their imported API answered 404 — until something
/// re-imported it. Each fact writes stubs for two non-default tenants and the default one through the
/// admin API (the <c>X-Mockifyr-Tenant</c> header), replaces the host with a fresh one on the same backend
/// — no change feed, so nothing but startup can load them — and serves each tenant's stub under its own
/// header, and nobody else's. Coherence infrastructure, so no oracle. Postgres and Redis require Docker.
/// </summary>
public abstract class StartupReloadAllTenantsFacts
{
    /// <summary>The backend flags a host is started with; both hosts share them.</summary>
    protected abstract string[] BackendArguments();

    private static string Stub(string body) =>
        "{\"request\":{\"method\":\"GET\",\"url\":\"/t\"},\"response\":{\"status\":200,\"body\":\"" + body + "\"}}";

    [Fact]
    public async Task A_new_host_serves_every_tenants_stubs_from_the_backend_without_the_change_feed()
    {
        await using (var first = await StartHostAsync())
        {
            using var client = Client(first);
            foreach (var tenant in new[] { "acme", "globex", null })
            {
                using var created = await SendAsync(client, HttpMethod.Post, "/__admin/mappings", tenant, Stub(tenant ?? "default"));
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }
        }

        await using var second = await StartHostAsync();
        using var fresh = Client(second);

        foreach (var tenant in new[] { "acme", "globex", null })
        {
            using var served = await SendAsync(fresh, HttpMethod.Get, "/t", tenant);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal(tenant ?? "default", await served.Content.ReadAsStringAsync());

            // Per tenant, never merged: the admin API lists exactly the tenant's own stub.
            using var listed = await SendAsync(fresh, HttpMethod.Get, "/__admin/mappings", tenant);
            using var document = await System.Text.Json.JsonDocument.ParseAsync(await listed.Content.ReadAsStreamAsync());
            Assert.Equal(1, document.RootElement.GetProperty("mappings").GetArrayLength());
        }

        // A tenant that never had stubs still has none.
        using var stranger = await SendAsync(fresh, HttpMethod.Get, "/t", "initech");
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }

    private async Task<WebApplication> StartHostAsync()
    {
        var app = MockifyrHost.Build(["--port", "0", .. BackendArguments()]);
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? tenant, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (tenant is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Mockifyr-Tenant", tenant);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
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

/// <summary><see cref="StartupReloadAllTenantsFacts"/> on Postgres.</summary>
public sealed class StartupReloadAllTenantsPostgresTests : StartupReloadAllTenantsFacts, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    protected override string[] BackendArguments() => ["--postgres", _postgres.GetConnectionString()];
}

/// <summary><see cref="StartupReloadAllTenantsFacts"/> on Redis.</summary>
public sealed class StartupReloadAllTenantsRedisTests : StartupReloadAllTenantsFacts, IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public Task InitializeAsync() => _redis.StartAsync();

    public async Task DisposeAsync() => await _redis.DisposeAsync();

    protected override string[] BackendArguments() => ["--redis", _redis.GetConnectionString()];
}

/// <summary><see cref="StartupReloadAllTenantsFacts"/> on LiteDB: one embedded file, every tenant's rows in it.</summary>
public sealed class StartupReloadAllTenantsLiteDbTests : StartupReloadAllTenantsFacts, IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mockifyr-466-");

    public void Dispose() => _dir.Delete(recursive: true);

    protected override string[] BackendArguments() => ["--litedb", Path.Combine(_dir.FullName, "mockifyr.db")];
}
