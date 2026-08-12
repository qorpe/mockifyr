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
/// The tenant as a declared object at the wire (#357): declare, suspend, offboard with a receipt, and
/// the per-tenant storage ceiling. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class TenantLifecycleWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;
    private string _root = string.Empty;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "mockifyr-tenants-" + Guid.NewGuid().ToString("N"));
        _host = MockifyrHost.Build([
            "--port", "0", "--https-port", "0", "--root-dir", _root, "--tenant-storage-limit", "200",
        ]);
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
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string body, string? tenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (tenant is not null) request.Headers.Add("X-Mockifyr-Tenant", tenant);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PutAsync(string path, string body, string tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        return await _client!.SendAsync(request);
    }

    [Fact]
    public async Task A_declared_tenant_is_listed_beside_the_derived_ones()
    {
        // Declaring is additive: the derived list — every tenant that owns a stub — is what existing
        // deployments read, and it keeps working untouched.
        using var stub = await PostAsync("/__admin/mappings",
            """{"request":{"method":"GET","url":"/ping"},"response":{"status":200}}""", "derived-only");
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        using var declared = await PostAsync("/__admin/tenants", """{"id":"acme","displayName":"Acme Ltd"}""");
        Assert.Equal(HttpStatusCode.Created, declared.StatusCode);

        using var listed = await _client!.GetAsync("/__admin/tenants");
        var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement;

        Assert.Contains("derived-only", body.GetProperty("tenants").EnumerateArray().Select(t => t.GetString()));
        var entry = body.GetProperty("declared").EnumerateArray().Single();
        Assert.Equal("Acme Ltd", entry.GetProperty("displayName").GetString());
        Assert.Equal("active", entry.GetProperty("status").GetString());
        Assert.Equal(200, entry.GetProperty("storageLimitBytes").GetInt64());
    }

    [Fact]
    public async Task A_suspended_tenant_is_refused_by_name_and_nothing_of_theirs_is_deleted()
    {
        await PostAsync("/__admin/tenants", """{"id":"paused","displayName":"Paused"}""");
        using var stub = await PostAsync("/__admin/mappings",
            """{"request":{"method":"GET","url":"/ping"},"response":{"status":200,"body":"pong"}}""", "paused");
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        using (var suspended = await PostAsync("/__admin/tenants/paused/suspend", string.Empty))
        {
            Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("X-Mockifyr-Tenant", "paused");
        using var refused = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Tenant.Suspended", await refused.Content.ReadAsStringAsync());

        // The sandbox is still there — that is the whole reason suspension exists.
        using var stubsRequest = new HttpRequestMessage(HttpMethod.Get, "/__admin/mappings");
        stubsRequest.Headers.Add("X-Mockifyr-Tenant", "paused");
        using var stubs = await _client.SendAsync(stubsRequest);
        Assert.Contains("/ping", await stubs.Content.ReadAsStringAsync());

        // And resuming puts it back exactly as it was.
        using (var resumed = await PostAsync("/__admin/tenants/paused/resume", string.Empty))
        {
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        }

        using var again = new HttpRequestMessage(HttpMethod.Get, "/ping");
        again.Headers.Add("X-Mockifyr-Tenant", "paused");
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_keeps_serving_while_one_is_suspended()
    {
        await PostAsync("/__admin/tenants", """{"id":"stopped","displayName":"Stopped"}""");
        await PostAsync("/__admin/mappings",
            """{"request":{"method":"GET","url":"/shared"},"response":{"status":200}}""", "stopped");
        await PostAsync("/__admin/mappings",
            """{"request":{"method":"GET","url":"/shared"},"response":{"status":200}}""", "running");
        await PostAsync("/__admin/tenants/stopped/suspend", string.Empty);

        using var neighbour = new HttpRequestMessage(HttpMethod.Get, "/shared");
        neighbour.Headers.Add("X-Mockifyr-Tenant", "running");

        Assert.Equal(HttpStatusCode.OK, (await _client!.SendAsync(neighbour)).StatusCode);
    }

    [Fact]
    public async Task Offboarding_removes_everything_scoped_to_the_tenant_and_returns_a_receipt()
    {
        await PostAsync("/__admin/tenants", """{"id":"leaving","displayName":"Leaving"}""");
        await PostAsync("/__admin/mappings",
            """{"request":{"method":"GET","url":"/ping"},"response":{"status":200}}""", "leaving");
        await PutAsync("/__admin/resources/orders/A-1", """{"total":1}""", "leaving");
        await PutAsync("/__admin/environments/greeting", """{"activeValue":"prod","values":[{"name":"prod","value":"hi"}]}""", "leaving");
        await PostAsync("/__admin/apikeys", """{"name":"partner"}""", "leaving");

        using var deleted = await _client!.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/__admin/tenants/leaving"));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var removed = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync()).RootElement.GetProperty("removed");

        Assert.Equal(1, removed.GetProperty("stubs").GetInt32());
        Assert.Equal(1, removed.GetProperty("documents").GetInt32());
        Assert.Equal(1, removed.GetProperty("environmentKeys").GetInt32());
        Assert.Equal(1, removed.GetProperty("apiKeys").GetInt32());

        using var resources = new HttpRequestMessage(HttpMethod.Get, "/__admin/resources");
        resources.Headers.Add("X-Mockifyr-Tenant", "leaving");
        Assert.Equal("""{"collections":[]}""", await (await _client.SendAsync(resources)).Content.ReadAsStringAsync());

        using var listed = await _client.GetAsync("/__admin/tenants");
        Assert.DoesNotContain(
            JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("declared").EnumerateArray(),
            t => t.GetProperty("id").GetString() == "leaving");
    }

    [Fact]
    public async Task A_write_past_the_tenants_ceiling_is_refused_with_both_numbers()
    {
        // The host ceiling is 200 bytes; two documents of ~120 do not both fit.
        var body = "{\"padding\":\"" + new string('x', 100) + "\"}";

        using var first = await PutAsync("/__admin/resources/bulk/A-1", body, "heavy");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var refused = await PutAsync("/__admin/resources/bulk/A-2", body, "heavy");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var error = await refused.Content.ReadAsStringAsync();
        Assert.Contains("Tenant.StorageExceeded", error);
        Assert.Contains("200", error);
    }

    [Fact]
    public async Task A_tenants_own_limit_overrides_the_host_default_and_usage_is_visible_before_it_is_hit()
    {
        await PostAsync("/__admin/tenants", """{"id":"roomy","displayName":"Roomy","storageLimitBytes":100000}""");
        var body = "{\"padding\":\"" + new string('x', 100) + "\"}";

        Assert.Equal(HttpStatusCode.OK, (await PutAsync("/__admin/resources/bulk/A-1", body, "roomy")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync("/__admin/resources/bulk/A-2", body, "roomy")).StatusCode);

        using var listed = await _client!.GetAsync("/__admin/tenants");
        var entry = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("declared").EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "roomy");

        Assert.Equal(100000, entry.GetProperty("storageLimitBytes").GetInt64());
        // Visible before it is hit, which is the difference between a ceiling and a surprise.
        Assert.True(entry.GetProperty("storageUsedBytes").GetInt64() > 200);
    }

    [Fact]
    public async Task A_declaration_survives_a_restart()
    {
        await PostAsync("/__admin/tenants", """{"id":"durable","displayName":"Durable"}""");
        await PostAsync("/__admin/tenants/durable/suspend", string.Empty);

        await _host!.StopAsync();
        await _host.DisposeAsync();
        _client!.Dispose();
        await InitializeSameRootAsync();

        using var listed = await _client!.GetAsync("/__admin/tenants");
        var entry = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("declared").EnumerateArray()
            .Single(t => t.GetProperty("id").GetString() == "durable");

        // A suspension that forgot itself on restart would be a suspension in name only.
        Assert.Equal("suspended", entry.GetProperty("status").GetString());
    }

    private async Task InitializeSameRootAsync()
    {
        _host = MockifyrHost.Build([
            "--port", "0", "--https-port", "0", "--root-dir", _root, "--tenant-storage-limit", "200",
        ]);
        await _host.StartAsync();
        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }
}
