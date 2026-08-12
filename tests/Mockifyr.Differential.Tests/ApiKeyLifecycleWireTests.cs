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
/// The key lifecycle at the wire (#355): what a partner actually sees when their credential expires,
/// is revoked, is read-only, or is rotated. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class ApiKeyLifecycleWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--sandbox-auth", "true"]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        foreach (var method in new[] { "GET", "POST" })
        {
            using var stub = await _client.PostAsync("/__admin/mappings", new StringContent(
                $$$"""{"request":{"method":"{{{method}}}","urlPath":"/thing"},"response":{"status":200,"body":"ok"}}""",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<JsonElement> IssueAsync(string body)
    {
        using var response = await _client!.PostAsync(
            "/__admin/apikeys", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> Call(string key, HttpMethod method)
    {
        var request = new HttpRequestMessage(method, "/thing");
        request.Headers.Add("X-Api-Key", key);
        return await _client!.SendAsync(request);
    }

    [Fact]
    public async Task An_expired_key_is_told_it_expired_and_an_unknown_one_is_told_nothing()
    {
        // A partner re-reading their config and a partner asking for a new credential are different
        // actions; one bare 401 for both costs a support round trip every time. An unknown token still
        // learns nothing — anything more would answer whether a guess was a real key.
        var issued = await IssueAsync("""{"name":"pilot","expiresInDays":1}""");
        var key = issued.GetProperty("key").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Call(key, HttpMethod.Get)).StatusCode);

        // Rotating with no overlap is the only way to make a key unusable on the spot, so expiry is
        // asserted through the model and revocation through the wire below.
        using var expiredIssue = await _client!.PostAsync("/__admin/apikeys", new StringContent(
            """{"name":"already-gone","expiresAt":"2020-01-01T00:00:00Z"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, expiredIssue.StatusCode);
        Assert.Contains("ApiKey.InvalidExpiry", await expiredIssue.Content.ReadAsStringAsync());

        using var unknown = await Call("mfk_not-a-real-key", HttpMethod.Get);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(string.Empty, await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_key_that_reaches_its_expiry_stops_being_accepted_and_says_why()
    {
        // Two seconds of real waiting, on purpose. Key expiry deliberately does NOT follow the tenant
        // clock (#290): that clock is an API call away, and an expiry an API call can undo is not an
        // expiry. So the only honest way to watch a key lapse is to let it.
        var expiry = DateTimeOffset.UtcNow.AddSeconds(2);
        var issued = await IssueAsync(
            $$"""{"name":"short","expiresAt":"{{expiry:O}}"}""");
        var key = issued.GetProperty("key").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await Call(key, HttpMethod.Get)).StatusCode);

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        using var refused = await Call(key, HttpMethod.Get);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("ApiKey.Expired", await refused.Content.ReadAsStringAsync());

        using var listed = await _client!.GetAsync("/__admin/apikeys");
        var entry = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("keys").EnumerateArray()
            .Single(k => k.GetProperty("name").GetString() == "short");
        Assert.Equal("expired", entry.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_revoked_key_says_so_and_the_record_of_who_revoked_it_survives()
    {
        var issued = await IssueAsync("""{"name":"partner"}""");
        var key = issued.GetProperty("key").GetString()!;
        var id = issued.GetProperty("id").GetString()!;

        using var revoked = await _client!.DeleteAsync($"/__admin/apikeys/{id}?reason=pilot%20ended");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        using var refused = await Call(key, HttpMethod.Get);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("ApiKey.Revoked", await refused.Content.ReadAsStringAsync());

        // Revocation is a state, not a delete: the key is still listed, carrying the decision.
        using var listed = await _client.GetAsync("/__admin/apikeys");
        var entry = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("id").GetString() == id);
        Assert.Equal("revoked", entry.GetProperty("status").GetString());
        Assert.Equal("pilot ended", entry.GetProperty("revokedReason").GetString());
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("revokedBy").GetString()));
    }

    [Fact]
    public async Task The_operator_who_revoked_a_key_is_named_even_with_the_audit_trail_off()
    {
        // Who revoked a key is part of the key, not an audit feature: on a host with credentials but
        // no --audit, an authenticated operator must not be written down as "unknown", which is false
        // rather than merely missing.
        var host = MockifyrHost.Build([
            "--port", "0", "--https-port", "0", "--sandbox-auth", "true",
            "--tenant-credential", "acme:acme-user:acme-pass",
        ]);
        await host.StartAsync();
        try
        {
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("acme-user:acme-pass")));
            client.DefaultRequestHeaders.Add("X-Mockifyr-Tenant", "acme");

            using var issue = await client.PostAsync("/__admin/apikeys", new StringContent(
                """{"name":"partner"}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
            var id = JsonDocument.Parse(await issue.Content.ReadAsStringAsync()).RootElement
                .GetProperty("id").GetString();

            using var revoked = await client.DeleteAsync($"/__admin/apikeys/{id}");
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

            using var listed = await client.GetAsync("/__admin/apikeys");
            var entry = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
                .RootElement.GetProperty("keys").EnumerateArray().Single();
            Assert.Equal("tenant:acme", entry.GetProperty("revokedBy").GetString());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_read_only_key_may_read_and_is_refused_a_write_with_403()
    {
        // 403 rather than 401: the credential is fine, the operation is not, and a 401 would send an
        // integrator to re-check a key that has nothing wrong with it.
        var issued = await IssueAsync("""{"name":"monitoring","scope":"read"}""");
        var key = issued.GetProperty("key").GetString()!;
        Assert.Equal("read", issued.GetProperty("scope").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Call(key, HttpMethod.Get)).StatusCode);

        using var refused = await Call(key, HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("ApiKey.ReadOnly", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_read_only_key_cannot_write_on_the_partner_surface_either()
    {
        var issued = await IssueAsync("""{"name":"monitoring","scope":"read"}""");
        var key = issued.GetProperty("key").GetString()!;

        using var read = new HttpRequestMessage(HttpMethod.Get, "/__sandbox/resources");
        read.Headers.Add("X-Api-Key", key);
        Assert.Equal(HttpStatusCode.OK, (await _client!.SendAsync(read)).StatusCode);

        using var write = new HttpRequestMessage(HttpMethod.Post, "/__sandbox/resources/reset");
        write.Headers.Add("X-Api-Key", key);
        using var refused = await _client.SendAsync(write);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Sandbox.ReadOnly", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Rotation_leaves_both_keys_working_so_a_partner_can_deploy_first()
    {
        var issued = await IssueAsync("""{"name":"partner","quotaPerHour":50}""");
        var old = issued.GetProperty("key").GetString()!;
        var id = issued.GetProperty("id").GetString()!;

        using var rotation = await _client!.PostAsync(
            $"/__admin/apikeys/{id}/rotate?overlapMinutes=60", new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.Created, rotation.StatusCode);
        var successor = JsonDocument.Parse(await rotation.Content.ReadAsStringAsync()).RootElement;
        var fresh = successor.GetProperty("key").GetString()!;

        Assert.NotEqual(old, fresh);
        Assert.Equal(id, successor.GetProperty("rotatedFrom").GetString());
        Assert.Equal(50, successor.GetProperty("quotaPerHour").GetInt32());

        // Both live: this is the whole point — a rotation that is an outage does not get done.
        Assert.Equal(HttpStatusCode.OK, (await Call(old, HttpMethod.Get)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(fresh, HttpMethod.Get)).StatusCode);

        using var listed = await _client.GetAsync("/__admin/apikeys");
        var previous = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("id").GetString() == id);
        Assert.Equal("active", previous.GetProperty("status").GetString());
        Assert.True(previous.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Rotation_with_no_overlap_ends_the_old_key_immediately()
    {
        // The leak case: the reason to accept an outage is that the credential is already out.
        var issued = await IssueAsync("""{"name":"leaked"}""");
        var old = issued.GetProperty("key").GetString()!;
        var id = issued.GetProperty("id").GetString()!;

        using var rotation = await _client!.PostAsync(
            $"/__admin/apikeys/{id}/rotate?overlapMinutes=0", new StringContent(string.Empty));
        var fresh = JsonDocument.Parse(await rotation.Content.ReadAsStringAsync())
            .RootElement.GetProperty("key").GetString()!;

        using var refused = await Call(old, HttpMethod.Get);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("ApiKey.Revoked", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await Call(fresh, HttpMethod.Get)).StatusCode);
    }

    [Fact]
    public async Task A_key_issued_before_the_lifecycle_existed_still_reads_as_active_and_read_write()
    {
        // The shape change had to be invisible to keys that predate it, on every provider — this is the
        // in-memory half; ApiKeyPersistenceTests covers the round trip through stored JSON.
        var issued = await IssueAsync("""{"name":"plain"}""");

        Assert.Equal("readwrite", issued.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, issued.GetProperty("expiresAt").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await Call(issued.GetProperty("key").GetString()!, HttpMethod.Post)).StatusCode);
    }
}
