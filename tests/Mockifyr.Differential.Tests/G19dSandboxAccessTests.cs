using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire-level self-tests for sandbox access (G19d, ADR 0011 addendum) against a REAL host built by
/// <c>MockifyrHost.Build</c> with <c>--sandbox-auth</c>, admin Basic auth, and a file root-dir:
/// issue-once semantics, key-based tenant resolution ahead of the header chain, provable tenant
/// isolation, the admin surface refusing sandbox keys, honest 401/429 with rate headers, an exact
/// parallel quota boundary, and keys surviving a full host restart.
/// </summary>
public sealed class G19dSandboxAccessTests : IAsyncLifetime
{
    private readonly string _rootDir = Path.Combine(Path.GetTempPath(), "mockifyr-g19d-" + Guid.NewGuid().ToString("N"));
    private Microsoft.AspNetCore.Builder.WebApplication? _host;
    private HttpClient _client = null!;

    private static readonly string AdminBasic =
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("op:secret"));

    public async Task InitializeAsync() => await StartHostAsync();

    public async Task DisposeAsync()
    {
        await StopHostAsync();
        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }

    private async Task StartHostAsync()
    {
        Directory.CreateDirectory(_rootDir);
        _host = MockifyrHost.Build(
            ["--port", "0", "--sandbox-auth", "true", "--admin-user", "op", "--admin-pass", "secret", "--root-dir", _rootDir]);
        await _host.StartAsync();
        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    private async Task StopHostAsync()
    {
        _client.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            await _host.DisposeAsync();
        }
    }

    private async Task<HttpResponseMessage> AdminAsync(HttpMethod method, string path, string? body = null, string tenant = "acme")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", AdminBasic);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await _client.SendAsync(request);
    }

    private async Task<string> IssueKeyAsync(string tenant, string name, int? quotaPerHour = null)
    {
        var payload = quotaPerHour is { } q
            ? $$"""{"name":"{{name}}","quotaPerHour":{{q}}}"""
            : $$"""{"name":"{{name}}"}""";
        using var response = await AdminAsync(HttpMethod.Post, "/__admin/apikeys", payload, tenant);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("key").GetString()!;
        Assert.StartsWith("mfk_", token);
        return token;
    }

    private async Task SeedStubAsync(string tenant, string url, string body)
    {
        var stub = """{"request":{"method":"GET","urlPath":""" + JsonSerializer.Serialize(url) +
            """},"response":{"status":200,"body":""" + JsonSerializer.Serialize(body) + "}}";
        using var response = await AdminAsync(HttpMethod.Post, "/__admin/mappings", stub, tenant);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<(HttpStatusCode Status, string Body)> ServeAsync(
        string path, string? apiKey = null, bool bearer = false, string? tenantHeader = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (apiKey is not null)
        {
            if (bearer)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            }
            else
            {
                request.Headers.Add("X-Api-Key", apiKey);
            }
        }

        if (tenantHeader is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenantHeader);
        }

        using var response = await _client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed record RateHeaders(string? Limit, string? Remaining, string? Reset, string? RetryAfter);

    private static RateHeaders Rate(HttpResponseMessage response) => new(
        response.Headers.TryGetValues("X-RateLimit-Limit", out var l) ? l.First() : null,
        response.Headers.TryGetValues("X-RateLimit-Remaining", out var r) ? r.First() : null,
        response.Headers.TryGetValues("X-RateLimit-Reset", out var s) ? s.First() : null,
        response.Headers.TryGetValues("Retry-After", out var a) ? a.First() : null);

    [Fact]
    public async Task Keys_resolve_the_tenant_ahead_of_the_chain_and_tenants_stay_isolated()
    {
        await SeedStubAsync("acme", "/whoami", "acme-answer");
        await SeedStubAsync("globex", "/whoami", "globex-answer");
        var acmeKey = await IssueKeyAsync("acme", "acme-ci");
        var globexKey = await IssueKeyAsync("globex", "globex-ci");

        // The key alone selects the tenant — no tenant header anywhere.
        Assert.Equal((HttpStatusCode.OK, "acme-answer"), await ServeAsync("/whoami", acmeKey) is var a ? (a.Status, a.Body) : default);
        Assert.Equal((HttpStatusCode.OK, "globex-answer"), await ServeAsync("/whoami", globexKey) is var g ? (g.Status, g.Body) : default);

        // Bearer carries the same credential; and the key WINS over a contradicting tenant header.
        var bearer = await ServeAsync("/whoami", acmeKey, bearer: true);
        Assert.Equal("acme-answer", bearer.Body);
        var contradicting = await ServeAsync("/whoami", acmeKey, tenantHeader: "globex");
        Assert.Equal("acme-answer", contradicting.Body);
    }

    [Fact]
    public async Task No_key_falls_through_to_the_legacy_chain_and_bad_keys_are_401()
    {
        await SeedStubAsync("acme", "/legacy", "via-header");

        // Zero-change proof: without credentials the header chain still works under --sandbox-auth.
        var legacy = await ServeAsync("/legacy", tenantHeader: "acme");
        Assert.Equal((HttpStatusCode.OK, "via-header"), (legacy.Status, legacy.Body));

        var garbled = await ServeAsync("/legacy", "mfk_totally-garbled");
        Assert.Equal(HttpStatusCode.Unauthorized, garbled.Status);

        // A revoked key stops working immediately — and revocation is tenant-checked.
        var token = await IssueKeyAsync("acme", "to-revoke");
        using (var list = await AdminAsync(HttpMethod.Get, "/__admin/apikeys"))
        {
            using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            var key = doc.RootElement.GetProperty("keys").EnumerateArray()
                .Single(k => k.GetProperty("name").GetString() == "to-revoke");
            var id = key.GetProperty("id").GetString()!;

            using var crossTenant = await AdminAsync(HttpMethod.Delete, $"/__admin/apikeys/{id}", tenant: "globex");
            Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
            using var revoke = await AdminAsync(HttpMethod.Delete, $"/__admin/apikeys/{id}");
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }

        var revoked = await ServeAsync("/legacy", token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.Status);
    }

    [Fact]
    public async Task The_admin_surface_refuses_sandbox_keys_and_never_leaks_secrets()
    {
        var token = await IssueKeyAsync("acme", "not-an-admin");

        // A valid sandbox key is NOT admin auth (addendum): Bearer and X-Api-Key are both refused.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/mappings"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/mappings"))
        {
            request.Headers.Add("X-Api-Key", token);
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // The listing exposes prefixes only — never the token, salt, or hash.
        using var list = await AdminAsync(HttpMethod.Get, "/__admin/apikeys");
        var body = await list.Content.ReadAsStringAsync();
        Assert.Contains("\"prefix\":\"" + token[..12] + "\"", body.Replace(" ", ""));
        Assert.DoesNotContain(token, body);
        Assert.DoesNotContain("salt", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hash", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Quotas_answer_429_with_honest_rate_headers_and_hold_under_parallel_load()
    {
        await SeedStubAsync("acme", "/limited", "ok");
        var token = await IssueKeyAsync("acme", "limited", quotaPerHour: 40);

        // Sequential: the first request reports the full window.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/limited"))
        {
            request.Headers.Add("X-Api-Key", token);
            using var response = await _client.SendAsync(request);
            var rate = Rate(response);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("40", rate.Limit);
            Assert.Equal("39", rate.Remaining);
            Assert.NotNull(rate.Reset);
        }

        // Parallel across the boundary: exactly 39 more succeed, everything beyond is 429.
        var statuses = await Task.WhenAll(Enumerable.Range(0, 60).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/limited");
            request.Headers.Add("X-Api-Key", token);
            using var response = await _client.SendAsync(request);
            return response.StatusCode;
        }));

        Assert.Equal(39, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(21, statuses.Count(s => s == HttpStatusCode.TooManyRequests));

        // The refusal carries Retry-After and the usage counter is queryable through the admin.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/limited"))
        {
            request.Headers.Add("X-Api-Key", token);
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.NotNull(Rate(response).RetryAfter);
        }

        using var list = await AdminAsync(HttpMethod.Get, "/__admin/apikeys");
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var entry = doc.RootElement.GetProperty("keys").EnumerateArray()
            .Single(k => k.GetProperty("name").GetString() == "limited");
        // 62 attempts were made against a limit of 40, and the counter reports attempts rather than
        // stopping at the limit (#354). A number that stops at the limit cannot distinguish a partner
        // who fitted inside their quota from one hammering a closed door — and the second is the case
        // an operator is looking for.
        Assert.Equal(62, entry.GetProperty("usedThisHour").GetInt32());
    }

    [Fact]
    public async Task Issued_keys_survive_a_full_host_restart()
    {
        var token = await IssueKeyAsync("acme", "durable");

        await StopHostAsync();
        await StartHostAsync();

        // The stub is re-seeded (stub persistence is G16's concern) — the claim here is that the
        // CREDENTIAL survives: the reloaded key still authenticates and still selects its tenant.
        await SeedStubAsync("acme", "/durable", "still-here");
        var served = await ServeAsync("/durable", token);
        Assert.Equal((HttpStatusCode.OK, "still-here"), (served.Status, served.Body));

        // And the reloaded listing still carries the metadata (prefix included), never the token.
        using var list = await AdminAsync(HttpMethod.Get, "/__admin/apikeys");
        var body = await list.Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"durable\"", body.Replace(" ", ""));
        Assert.Contains(token[..12], body);
        Assert.DoesNotContain(token, body);
    }
}
