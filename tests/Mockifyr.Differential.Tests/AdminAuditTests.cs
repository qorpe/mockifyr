using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for the admin audit trail (#247). The claims under test are the ones an auditor
/// actually relies on: every change lands exactly once, reads do not, refusals are recorded with their
/// real outcome, no secret ever appears in an entry, one tenant cannot read another's history, and a
/// host without the flag records nothing at all.
/// </summary>
public sealed class AdminAuditTests
{
    private const string AuditPath = "/__admin/audit";

    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication Host, HttpClient Client)> StartAsync(
        params string[] args)
    {
        var host = MockifyrHost.Build([.. new[] { "--port", "0" }.Concat(args)]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string BasicHeader(string user, string pass) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

    private static async Task<JsonElement[]> ReadAuditAsync(HttpClient client, string? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AuditPath);
        if (tenant is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenant);
        }

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. doc.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.Clone())];
    }

    [Fact]
    public async Task Each_change_is_recorded_once_and_reads_are_not()
    {
        var (host, client) = await StartAsync("--audit", "true");
        await using (host)
        {
            using var created = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/audited"},"response":{"status":200,"body":"ok"}}"""));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var stubId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
                .RootElement.GetProperty("id").GetString()!;

            // Reads of every shape — the served mock path, the journal, the stub listing, the trail
            // itself — must leave no trace, or the changes an operator is looking for get evicted by
            // traffic.
            await client.GetAsync("/audited");
            await client.GetAsync("/__admin/requests");
            await client.GetAsync("/__admin/mappings");
            await ReadAuditAsync(client);

            using var deleted = await client.DeleteAsync($"/__admin/mappings/{stubId}");
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

            var entries = await ReadAuditAsync(client);
            Assert.Equal(2, entries.Length);

            // Newest first.
            Assert.Equal($"DELETE /__admin/mappings/{stubId}", entries[0].GetProperty("action").GetString());
            Assert.Equal(stubId, entries[0].GetProperty("target").GetString());
            Assert.Equal(200, entries[0].GetProperty("outcome").GetInt32());

            Assert.Equal("POST /__admin/mappings", entries[1].GetProperty("action").GetString());
            Assert.Equal(201, entries[1].GetProperty("outcome").GetInt32());
            // A collection route addresses no id, and inventing one would be a lie in the record.
            Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("target").ValueKind);

            foreach (var entry in entries)
            {
                Assert.Equal("default", entry.GetProperty("tenant").GetString());
                Assert.Equal("anonymous", entry.GetProperty("principal").GetString());
                Assert.NotEqual(Guid.Empty, entry.GetProperty("id").GetGuid());
            }

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_refused_change_is_recorded_with_its_outcome()
    {
        var (host, client) = await StartAsync("--audit", "true");
        await using (host)
        {
            // A change the handler rejected is the most interesting kind: it tells a reviewer someone
            // tried. Recording it as if it succeeded — or not at all — is the failure mode.
            using var refused = await client.PostAsync("/__admin/mappings", Json("{ not json"));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);

            var entries = await ReadAuditAsync(client);
            var entry = Assert.Single(entries);
            Assert.Equal("POST /__admin/mappings", entry.GetProperty("action").GetString());
            // The handler's actual answer, whatever it was — the entry reports the outcome rather than
            // assuming the attempt succeeded.
            Assert.Equal(422, entry.GetProperty("outcome").GetInt32());

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Principals_are_labelled_and_no_credential_material_is_stored()
    {
        var (host, client) = await StartAsync(
            "--audit", "true",
            "--admin-user", "op", "--admin-pass", "s3cr3t-pass",
            "--tenant-credential", "acme:acme-user:acme-pass");
        await using (host)
        {
            using var systemChange = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = Json("""{"request":{"method":"GET","urlPath":"/by-system"},"response":{"status":200}}"""),
            };
            systemChange.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicHeader("op", "s3cr3t-pass"));
            using var systemResponse = await client.SendAsync(systemChange);
            Assert.Equal(HttpStatusCode.Created, systemResponse.StatusCode);

            using var tenantChange = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = Json("""{"request":{"method":"GET","urlPath":"/by-tenant"},"response":{"status":200}}"""),
            };
            tenantChange.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicHeader("acme-user", "acme-pass"));
            tenantChange.Headers.Add("X-Mockifyr-Tenant", "acme");
            using var tenantResponse = await client.SendAsync(tenantChange);
            Assert.Equal(HttpStatusCode.Created, tenantResponse.StatusCode);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", BasicHeader("op", "s3cr3t-pass"));

            var systemEntries = await ReadAuditAsync(client);
            Assert.Equal("system", Assert.Single(systemEntries).GetProperty("principal").GetString());

            var tenantEntries = await ReadAuditAsync(client, "acme");
            Assert.Equal("tenant:acme", Assert.Single(tenantEntries).GetProperty("principal").GetString());

            // The whole trail, serialized, must not carry any part of a credential — an audit log that
            // leaks secrets is worse than none, because it concentrates them.
            using var raw = new HttpRequestMessage(HttpMethod.Get, AuditPath);
            using var rawResponse = await client.SendAsync(raw);
            var body = await rawResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("s3cr3t-pass", body);
            Assert.DoesNotContain("acme-pass", body);
            Assert.DoesNotContain("Basic ", body);
            Assert.DoesNotContain(BasicHeader("op", "s3cr3t-pass"), body);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_cross_tenant_refusal_is_audited_but_an_unauthenticated_attempt_is_not()
    {
        var (host, client) = await StartAsync(
            "--audit", "true",
            "--admin-user", "op", "--admin-pass", "secret",
            "--tenant-credential", "acme:acme-user:acme-pass");
        await using (host)
        {
            // Known principal reaching for a tenant it does not own (#224): recorded, with the 403.
            using var crossTenant = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = Json("""{"request":{"method":"GET","urlPath":"/nope"},"response":{"status":200}}"""),
            };
            crossTenant.Headers.Authorization = new AuthenticationHeaderValue("Basic", BasicHeader("acme-user", "acme-pass"));
            crossTenant.Headers.Add("X-Mockifyr-Tenant", "other");
            using var crossResponse = await client.SendAsync(crossTenant);
            Assert.Equal(HttpStatusCode.Forbidden, crossResponse.StatusCode);

            // No credential at all: not an administrative change, no principal to name, and auditing it
            // would hand anyone a lever to evict the bounded trail by repetition.
            using var anonymous = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/anon"},"response":{"status":200}}"""));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", BasicHeader("op", "secret"));

            var refused = Assert.Single(await ReadAuditAsync(client, "other"));
            Assert.Equal("tenant:acme", refused.GetProperty("principal").GetString());
            Assert.Equal(403, refused.GetProperty("outcome").GetInt32());

            Assert.Empty(await ReadAuditAsync(client));

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task One_tenant_cannot_read_another_tenants_history()
    {
        var (host, client) = await StartAsync("--audit", "true");
        await using (host)
        {
            using var first = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = Json("""{"request":{"method":"GET","urlPath":"/a"},"response":{"status":200}}"""),
            };
            first.Headers.Add("X-Mockifyr-Tenant", "alpha");
            using var firstResponse = await client.SendAsync(first);
            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

            using var second = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = Json("""{"request":{"method":"GET","urlPath":"/b"},"response":{"status":200}}"""),
            };
            second.Headers.Add("X-Mockifyr-Tenant", "beta");
            using var secondResponse = await client.SendAsync(second);
            Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);

            var alpha = await ReadAuditAsync(client, "alpha");
            var beta = await ReadAuditAsync(client, "beta");

            Assert.Equal("alpha", Assert.Single(alpha).GetProperty("tenant").GetString());
            Assert.Equal("beta", Assert.Single(beta).GetProperty("tenant").GetString());
            Assert.Empty(await ReadAuditAsync(client));

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Without_the_flag_nothing_is_recorded()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var created = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/unaudited"},"response":{"status":200,"body":"ok"}}"""));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            // Opt-in: the endpoint answers, and answers empty. Serving is untouched either way.
            Assert.Empty(await ReadAuditAsync(client));
            Assert.Equal("ok", await client.GetStringAsync("/unaudited"));

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task The_trail_is_bounded_and_evicts_oldest_first()
    {
        var (host, client) = await StartAsync("--audit", "true", "--audit-limit", "3");
        await using (host)
        {
            for (var i = 0; i < 5; i++)
            {
                using var created = await client.PostAsync("/__admin/mappings", Json(
                    $$$"""{"request":{"method":"GET","urlPath":"/bounded-{{{i}}}"},"response":{"status":200}}"""));
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            // An unbounded in-memory trail is a slow leak, so retention is the operator's choice and
            // the oldest entries go first — the journal's model (#220), not a second one to learn.
            var entries = await ReadAuditAsync(client);
            Assert.Equal(3, entries.Length);
            Assert.All(entries, entry => Assert.Equal("POST /__admin/mappings", entry.GetProperty("action").GetString()));

            await host.StopAsync();
            client.Dispose();
        }
    }
}
