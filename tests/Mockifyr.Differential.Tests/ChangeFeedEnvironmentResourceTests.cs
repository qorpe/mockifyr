using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Validation of change-feed reload for environment keys and sandbox resources (#279) — the half the
/// feed did not carry until now. Two live hosts share one backend with <c>--change-feed</c>; a key or a
/// document written, changed or deleted on one is honoured by the other <em>without a restart</em>.
/// </summary>
/// <remarks>
/// <para>
/// Written once and run against both shared backends (Redis pub/sub and Postgres <c>LISTEN</c>/
/// <c>NOTIFY</c>), because the defect being fixed was per-provider — the environment and resource
/// providers announced nothing at all — so a suite that proved one backend would have proved half a fix.
/// </para>
/// <para>
/// This is coherence infrastructure, not dialect behaviour: no oracle is involved, and the serve-time
/// semantics of environments (G17) and resources (G19a) are covered by their own suites. Propagation is
/// asynchronous, so assertions poll within a timeout. Requires Docker.
/// </para>
/// </remarks>
public abstract class ChangeFeedEnvironmentResourceTests
{
    private const string TenantHeader = "X-Mockifyr-Tenant";

    /// <summary>
    /// A stub whose body is the environment reference, so what each host resolves is observable over the
    /// wire. Environment substitution (G17) is its own pass, so no response transformer is involved: an
    /// unresolved key leaves the literal <c>{{apiHost}}</c> in the body, which makes "B has not learned
    /// the key yet" and "B resolved it to the wrong value" two different failures.
    /// </summary>
    private const string TemplatedStub =
        """{"request":{"method":"GET","url":"/host"},"response":{"status":200,"body":"{{apiHost}}"}}""";

    private static string EnvironmentKeyJson(string active) =>
        $$"""
        {"key":"apiHost","activeValue":"{{active}}",
         "values":[{"name":"staging","value":"https://staging.example.com"},
                   {"name":"prod","value":"https://example.com"}]}
        """;

    protected abstract string[] BackendArguments { get; }

    [Fact]
    public async Task An_environment_key_written_on_one_instance_is_resolved_by_the_other()
    {
        await using var hostA = await StartHostAsync();
        await using var hostB = await StartHostAsync();
        using var clientA = Client(hostA);
        using var clientB = Client(hostB);

        using var stub = await clientA.PostAsync("/__admin/mappings", Json(TemplatedStub));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
        using var created = await clientA.PutAsync("/__admin/environments/apiHost", Json(EnvironmentKeyJson("staging")));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        // B resolves the key it never saw written — the whole point of the feed.
        Assert.Equal("https://staging.example.com", await PollBodyAsync(clientB, "/host", "https://staging.example.com"));

        // The case that made this a bug worth fixing: an operator flips the active value and one replica
        // keeps serving the old one. Both must move.
        using var flipped = await clientA.PutAsync(
            "/__admin/environments/apiHost/active", Json("""{"activeValue":"prod"}"""));
        Assert.Equal(HttpStatusCode.OK, flipped.StatusCode);
        Assert.Equal("https://example.com", await PollBodyAsync(clientB, "/host", "https://example.com"));
    }

    [Fact]
    public async Task An_environment_key_deleted_on_one_instance_is_pruned_from_the_other()
    {
        await using var hostA = await StartHostAsync();
        await using var hostB = await StartHostAsync();
        using var clientA = Client(hostA);
        using var clientB = Client(hostB);

        using var created = await clientA.PutAsync("/__admin/environments/apiHost", Json(EnvironmentKeyJson("staging")));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        await PollAsync(clientB, async () => (await KeysOfAsync(clientB)).Contains("apiHost"));
        Assert.Contains("apiHost", await KeysOfAsync(clientB));

        // A delete that only clears the writer's memory is the trap the stub reconciler already avoids:
        // the key would come back on B's next reload and outlive the deletion.
        using var deleted = await clientA.DeleteAsync("/__admin/environments/apiHost");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        await PollAsync(clientB, async () => !(await KeysOfAsync(clientB)).Contains("apiHost"));
        Assert.DoesNotContain("apiHost", await KeysOfAsync(clientB));
    }

    [Fact]
    public async Task A_sandbox_document_written_on_one_instance_is_readable_on_the_other()
    {
        await using var hostA = await StartHostAsync();
        await using var hostB = await StartHostAsync();
        using var clientA = Client(hostA);
        using var clientB = Client(hostB);

        using var written = await clientA.PutAsync("/__admin/resources/orders/42", Json("""{"total":10}"""));
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        await PollAsync(clientB, async () =>
            (await clientB.GetAsync("/__admin/resources/orders/42")).StatusCode == HttpStatusCode.OK);

        using var read = await clientB.GetAsync("/__admin/resources/orders/42");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(10, document.RootElement.GetProperty("body").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_document_mirrored_onto_the_other_instance_keeps_the_writer_version()
    {
        await using var hostA = await StartHostAsync();
        await using var hostB = await StartHostAsync();
        using var clientA = Client(hostA);
        using var clientB = Client(hostB);

        using var first = await clientA.PutAsync("/__admin/resources/orders/42", Json("""{"total":10}"""));
        using var second = await clientA.PutAsync("/__admin/resources/orders/42", Json("""{"total":20}"""));
        // A reads its own write, at its own version. This looks trivial and is not: a host also hears its
        // own announcements, and a reload triggered by the first write can read the backend before the
        // second one lands and then restore that older view over it — the operator gets their change
        // handed back at the previous version. Hence the writer identity on every announcement.
        var (versionOnA, updatedAtOnA) = await VersionAsync(clientA, "orders", "42");
        Assert.Equal(2, versionOnA);

        await PollAsync(clientB, async () => (await VersionAsync(clientB, "orders", "42")).Version == 2);

        // Now provoke reloads that have nothing to do with this document. Replaying it through the store's
        // own Put would advance its version on every one of them, so the same document would report a
        // different version on each replica — a difference a client can see. This is why reload restores
        // rather than writes.
        for (var change = 0; change < 3; change++)
        {
            using var noise = await clientA.PostAsync("/__admin/mappings", Json(
                $$$"""{"request":{"method":"GET","url":"/noise-{{{change}}}"},"response":{"status":200}}"""));
            Assert.Equal(HttpStatusCode.Created, noise.StatusCode);
            await PollAsync(clientB, async () =>
                (await clientB.GetAsync($"/noise-{change}")).StatusCode == HttpStatusCode.OK);
        }

        var (versionOnB, updatedAtOnB) = await VersionAsync(clientB, "orders", "42");
        Assert.Equal(versionOnA, versionOnB);
        Assert.Equal(updatedAtOnA, updatedAtOnB);
    }

    [Fact]
    public async Task A_document_deleted_on_one_instance_is_pruned_from_the_other()
    {
        await using var hostA = await StartHostAsync();
        await using var hostB = await StartHostAsync();
        using var clientA = Client(hostA);
        using var clientB = Client(hostB);

        using var written = await clientA.PutAsync("/__admin/resources/orders/42", Json("""{"total":10}"""));
        await PollAsync(clientB, async () =>
            (await clientB.GetAsync("/__admin/resources/orders/42")).StatusCode == HttpStatusCode.OK);

        // Assert B actually has it before deleting. Without this the test passes on a host that never
        // received the document at all — "gone" and "never arrived" look identical from the far end.
        using var present = await clientB.GetAsync("/__admin/resources/orders/42");
        Assert.Equal(HttpStatusCode.OK, present.StatusCode);

        using var deleted = await clientA.DeleteAsync("/__admin/resources/orders/42");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        await PollAsync(clientB, async () =>
            (await clientB.GetAsync("/__admin/resources/orders/42")).StatusCode == HttpStatusCode.NotFound);
        using var gone = await clientB.GetAsync("/__admin/resources/orders/42");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Reload_keeps_two_tenants_documents_apart_under_the_same_id()
    {
        await using var hostA = await StartHostAsync();
        await using var hostB = await StartHostAsync();
        using var clientA = Client(hostA);
        using var clientB = Client(hostB);

        // The same collection and id in two tenants: a reload that reconciled by id alone would let one
        // tenant's document overwrite — or prune — the other's, which no wire test of a single tenant
        // would ever notice.
        using var acme = await PutAsTenantAsync(clientA, "acme", "/__admin/resources/orders/42", """{"total":10}""");
        using var globex = await PutAsTenantAsync(clientA, "globex", "/__admin/resources/orders/42", """{"total":99}""");

        await PollAsync(clientB, async () => await TotalAsync(clientB, "acme") == 10);
        Assert.Equal(10, await TotalAsync(clientB, "acme"));
        Assert.Equal(99, await TotalAsync(clientB, "globex"));

        // Deleting one tenant's document must not disturb the other's.
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/__admin/resources/orders/42");
        request.Headers.Add(TenantHeader, "acme");
        using var deleted = await clientA.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        await PollAsync(clientB, async () => await TotalAsync(clientB, "acme") is null);
        Assert.Null(await TotalAsync(clientB, "acme"));
        Assert.Equal(99, await TotalAsync(clientB, "globex"));
    }

    private static async Task<HttpResponseMessage> PutAsTenantAsync(
        HttpClient client, string tenant, string path, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = Json(body) };
        request.Headers.Add(TenantHeader, tenant);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response;
    }

    private static async Task<int?> TotalAsync(HttpClient client, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/resources/orders/42");
        request.Headers.Add(TenantHeader, tenant);
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("body").GetProperty("total").GetInt32();
    }

    private static async Task<(long Version, string UpdatedAt)> VersionAsync(
        HttpClient client, string collection, string id)
    {
        using var response = await client.GetAsync($"/__admin/resources/{collection}/{id}");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (-1, string.Empty);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (document.RootElement.GetProperty("version").GetInt64(),
            document.RootElement.GetProperty("updatedAt").GetString() ?? string.Empty);
    }

    private static async Task<IReadOnlyList<string>> KeysOfAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/__admin/environments");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. document.RootElement.GetProperty("environments").EnumerateArray()
            .Select(element => element.GetProperty("key").GetString() ?? string.Empty)];
    }

    // Propagation is asynchronous on both backends, so every cross-instance assertion polls first and
    // then asserts on a fresh read — a poll that times out still fails on the assertion, with its message.
    private static async Task PollAsync(HttpClient client, Func<Task<bool>> satisfied)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await satisfied())
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    private static async Task<string> PollBodyAsync(HttpClient client, string path, string expected)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            if (body == expected)
            {
                return body;
            }

            await Task.Delay(100);
        }

        using var last = await client.GetAsync(path);
        return await last.Content.ReadAsStringAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<WebApplication> StartHostAsync()
    {
        var app = MockifyrHost.Build([.. new[] { "--port", "0", "--change-feed", "true" }.Concat(BackendArguments)]);
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

/// <summary>The Redis pub/sub feed (G16e) carrying environments and resources.</summary>
public sealed class RedisChangeFeedEnvironmentResourceTests : ChangeFeedEnvironmentResourceTests, IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    protected override string[] BackendArguments => ["--redis", _redis.GetConnectionString()];

    public Task InitializeAsync() => _redis.StartAsync();

    public async Task DisposeAsync() => await _redis.DisposeAsync();
}

/// <summary>The Postgres LISTEN/NOTIFY feed (G16f) carrying environments and resources.</summary>
public sealed class PostgresChangeFeedEnvironmentResourceTests : ChangeFeedEnvironmentResourceTests, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    protected override string[] BackendArguments => ["--postgres", _postgres.GetConnectionString()];

    public Task InitializeAsync() => _postgres.StartAsync();

    public async Task DisposeAsync() => await _postgres.DisposeAsync();
}
