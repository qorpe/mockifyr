using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;
using Testcontainers.Redis;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// The claim #354 exists to make true: behind two replicas a partner's quota is the number on their
/// key, not that number per pod — and a restart does not forgive what they already spent.
/// </summary>
/// <remarks>
/// <para>
/// Two hosts against one real Redis container, which is the only way to test this: an in-process
/// counter passes any single-host test perfectly and is wrong in exactly the deployment the feature
/// is for. Mockifyr-specific behaviour, so a self-test rather than a differential — the reference
/// engine has no sandbox quotas to be an oracle for.
/// </para>
/// <para>
/// Requires Docker, like the rest of the persistence suite.
/// </para>
/// </remarks>
public sealed class SharedQuotaTests : IAsyncLifetime
{
    private const string Stub =
        """{"request":{"method":"GET","url":"/ping"},"response":{"status":200,"body":"pong"}}""";

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public Task InitializeAsync() => _redis.StartAsync();

    public Task DisposeAsync() => _redis.DisposeAsync().AsTask();

    [Fact]
    public async Task Two_hosts_enforce_the_sum_rather_than_the_limit_each()
    {
        var connection = _redis.GetConnectionString();

        await using var hostA = await StartAsync(connection);
        await using var hostB = await StartAsync(connection);
        using var clientA = Client(hostA);
        using var clientB = Client(hostB);

        await Seed(clientA);
        var key = await IssueKey(clientA, quotaPerHour: 4);
        await AwaitArrival(clientB);

        // Alternating replicas, the way a load balancer would.
        Assert.Equal(HttpStatusCode.OK, (await Ping(clientA, key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Ping(clientB, key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Ping(clientA, key)).StatusCode);

        using var fourth = await Ping(clientB, key);
        Assert.Equal(HttpStatusCode.OK, fourth.StatusCode);
        // The header is the shared total, not this replica's share of it.
        Assert.Equal("0", fourth.Headers.GetValues("X-RateLimit-Remaining").First());

        // The fifth is refused by whichever replica sees it — with an in-process counter this is the
        // request that would have been allowed, because each host would still think it was on its
        // second.
        using var refusedOnB = await Ping(clientB, key);
        Assert.Equal(HttpStatusCode.TooManyRequests, refusedOnB.StatusCode);

        using var refusedOnA = await Ping(clientA, key);
        Assert.Equal(HttpStatusCode.TooManyRequests, refusedOnA.StatusCode);
    }

    [Fact]
    public async Task A_restart_does_not_hand_back_the_quota_a_partner_already_spent()
    {
        var connection = _redis.GetConnectionString();
        string key;

        await using (var first = await StartAsync(connection))
        {
            using var client = Client(first);
            await Seed(client);
            key = await IssueKey(client, quotaPerHour: 2);

            Assert.Equal(HttpStatusCode.OK, (await Ping(client, key)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Ping(client, key)).StatusCode);
        }

        // A deploy in the middle of an hour is not a fresh budget — otherwise the way past a quota is
        // to wait for one.
        await using var restarted = await StartAsync(connection);
        using var after = Client(restarted);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Ping(after, key)).StatusCode);
    }

    [Fact]
    public async Task Usage_reported_by_one_host_includes_what_the_other_served()
    {
        // The admin view of a key is how an operator answers "are they near their limit?", so it has
        // to read the shared counter too rather than this pod's slice of it.
        var connection = _redis.GetConnectionString();

        await using var hostA = await StartAsync(connection);
        await using var hostB = await StartAsync(connection);
        using var clientA = Client(hostA);
        using var clientB = Client(hostB);

        await Seed(clientA);
        var key = await IssueKey(clientA, quotaPerHour: 10);
        await AwaitArrival(clientB);

        await Ping(clientA, key);
        await Ping(clientA, key);
        await Ping(clientB, key);

        using var listed = await clientB.GetAsync("/__admin/apikeys");
        var used = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("keys").EnumerateArray().First().GetProperty("usedThisHour").GetInt32();

        Assert.Equal(3, used);
    }

    [Fact]
    public async Task A_key_revoked_on_one_replica_stops_working_on_the_other()
    {
        // The half that matters. Issuing late costs a partner a retry; a revocation that only lands on
        // the replica that performed it means a withdrawn credential keeps serving traffic.
        var connection = _redis.GetConnectionString();

        await using var hostA = await StartAsync(connection);
        await using var hostB = await StartAsync(connection);
        using var clientA = Client(hostA);
        using var clientB = Client(hostB);

        await Seed(clientA);
        var key = await IssueKey(clientA, quotaPerHour: 100);
        await AwaitArrival(clientB);
        Assert.Equal(HttpStatusCode.OK, (await Ping(clientB, key)).StatusCode);

        using var listed = await clientA.GetAsync("/__admin/apikeys");
        var id = JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
            .RootElement.GetProperty("keys").EnumerateArray().First().GetProperty("id").GetString();
        using var revoked = await clientA.DeleteAsync($"/__admin/apikeys/{id}");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var probe = await Ping(clientB, key);
            if (probe.StatusCode == HttpStatusCode.Unauthorized)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("the revoked key was still accepted by the other replica");
    }

    /// <summary>
    /// Waits for the change feed to carry the stub and the key to the other replica. Polls the admin
    /// API rather than the mock port on purpose: a poll that spent quota would be measuring itself.
    /// </summary>
    private static async Task AwaitArrival(HttpClient client)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var keys = await client.GetAsync("/__admin/apikeys");
            using var stubs = await client.GetAsync("/__admin/mappings");
            var arrived = JsonDocument.Parse(await keys.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("keys").GetArrayLength() > 0
                && (await stubs.Content.ReadAsStringAsync()).Contains("/ping", StringComparison.Ordinal);
            if (arrived)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("the change feed never carried the key and stub to the second replica");
    }

    private static async Task Seed(HttpClient client)
    {
        using var content = new StringContent(Stub, Encoding.UTF8, "application/json");
        using var created = await client.PostAsync("/__admin/mappings", content);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private static async Task<string> IssueKey(HttpClient client, int quotaPerHour)
    {
        using var content = new StringContent(
            $$"""{"name":"partner","quotaPerHour":{{quotaPerHour}}}""", Encoding.UTF8, "application/json");
        using var issued = await client.PostAsync("/__admin/apikeys", content);
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        return JsonDocument.Parse(await issued.Content.ReadAsStringAsync())
            .RootElement.GetProperty("key").GetString()!;
    }

    private static async Task<HttpResponseMessage> Ping(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("X-Api-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<WebApplication> StartAsync(string connection)
    {
        var app = MockifyrHost.Build([
            "--port", "0", "--https-port", "0", "--redis", connection,
            "--sandbox-auth", "true", "--change-feed", "true",
        ]);
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
