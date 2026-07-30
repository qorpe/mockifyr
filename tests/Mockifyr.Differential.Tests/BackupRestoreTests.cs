using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for backup and restore (#252). The claim under test is the one a runbook rests on:
/// take an archive, bring up a host that has never seen this tenant, restore, and everything the
/// operator authored is serving again — stubs, environments, sandbox documents, API keys and scenario
/// states — with no manual step in between.
/// </summary>
public sealed class BackupRestoreTests
{
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Authors one of everything the archive is supposed to carry.</summary>
    private static async Task SeedAsync(HttpClient client)
    {
        using var stub = await client.PostAsync("/__admin/mappings", Json(
            """
            {"request":{"method":"GET","urlPath":"/orders"},
             "response":{"status":200,"body":"{{apiHost}}"},
             "metadata":{"team":"payments"}}
            """));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        using var scenarioStub = await client.PostAsync("/__admin/mappings", Json(
            """
            {"scenarioName":"checkout","requiredScenarioState":"PAID",
             "request":{"method":"GET","urlPath":"/receipt"},
             "response":{"status":200,"body":"paid"}}
            """));
        Assert.Equal(HttpStatusCode.Created, scenarioStub.StatusCode);

        using var environment = await client.PutAsync("/__admin/environments/apiHost", Json(
            """{"key":"apiHost","activeValue":"staging","values":[{"name":"staging","value":"https://staging.example.com"},{"name":"prod","value":"https://example.com"}]}"""));
        Assert.Equal(HttpStatusCode.OK, environment.StatusCode);

        using var resource = await client.PutAsync("/__admin/resources/orders/42", Json("""{"total":10}"""));
        Assert.Equal(HttpStatusCode.OK, resource.StatusCode);

        using var scenarioState = await client.PutAsync("/__admin/scenarios/checkout/state", Json("""{"state":"PAID"}"""));
        Assert.Equal(HttpStatusCode.OK, scenarioState.StatusCode);
    }

    [Fact]
    public async Task An_archive_restored_into_a_fresh_host_reproduces_everything()
    {
        string archive;
        string token;

        var (source, sourceClient) = await StartAsync("--sandbox-auth", "true");
        await using (source)
        {
            await SeedAsync(sourceClient);

            using var issued = await sourceClient.PostAsync("/__admin/apikeys", Json("""{"name":"partner-ci","quotaPerHour":500}"""));
            Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
            token = (await ReadJsonAsync(issued)).GetProperty("key").GetString()!;

            archive = await sourceClient.GetStringAsync("/__admin/backup");

            await source.StopAsync();
            sourceClient.Dispose();
        }

        // A different process that has never seen this tenant — the restore-into-a-new-host drill, not
        // a reload of state the host already had.
        var (target, client) = await StartAsync("--sandbox-auth", "true");
        await using (target)
        {
            using var restore = await client.PostAsync("/__admin/restore", Json(archive));
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
            var summary = (await ReadJsonAsync(restore)).GetProperty("restored");
            Assert.Equal(2, summary.GetProperty("mappings").GetInt32());
            Assert.Equal(1, summary.GetProperty("environments").GetInt32());
            Assert.Equal(1, summary.GetProperty("resources").GetInt32());
            Assert.Equal(1, summary.GetProperty("apiKeys").GetInt32());
            Assert.Equal(1, summary.GetProperty("scenarios").GetInt32());

            // Stubs serve again — and the environment key resolves, which proves the environment
            // section landed rather than the stub merely existing.
            Assert.Equal("https://staging.example.com", await client.GetStringAsync("/orders"));

            // The scenario is back in the state it was left in: the stub behind PAID serves, which it
            // would not in the default Started state.
            Assert.Equal("paid", await client.GetStringAsync("/receipt"));

            // Sandbox documents came back with their bodies.
            using var document = await client.GetAsync("/__admin/resources/orders/42");
            Assert.Equal(HttpStatusCode.OK, document.StatusCode);
            Assert.Contains("\"total\"", await document.Content.ReadAsStringAsync());

            // The key a consumer already holds still authenticates — nobody has to re-issue credentials
            // to every partner after a restore, which is the point of carrying the verifier.
            using var authenticated = new HttpRequestMessage(HttpMethod.Get, "/orders");
            authenticated.Headers.Add("X-Api-Key", token);
            using var served = await client.SendAsync(authenticated);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);

            using var keys = await client.GetAsync("/__admin/apikeys");
            var restoredKey = (await ReadJsonAsync(keys)).GetProperty("keys").EnumerateArray().Single();
            Assert.Equal("partner-ci", restoredKey.GetProperty("name").GetString());
            Assert.Equal(500, restoredKey.GetProperty("quotaPerHour").GetInt32());

            await target.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_restore_replaces_rather_than_merges()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var kept = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/keep"},"response":{"status":200,"body":"keep"}}"""));
            Assert.Equal(HttpStatusCode.Created, kept.StatusCode);
            var archive = await client.GetStringAsync("/__admin/backup");

            using var later = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/added-later"},"response":{"status":200,"body":"later"}}"""));
            Assert.Equal(HttpStatusCode.Created, later.StatusCode);
            Assert.Equal("later", await client.GetStringAsync("/added-later"));

            using var restore = await client.PostAsync("/__admin/restore", Json(archive));
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);

            // A restored host is the host that was backed up. Merging would leave a stub the archive
            // knows nothing about still serving, which is the opposite of what a restore is for.
            Assert.Equal("keep", await client.GetStringAsync("/keep"));
            using var gone = await client.GetAsync("/added-later");
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task An_archive_can_be_restored_into_a_different_tenant()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var seed = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = Json("""{"request":{"method":"GET","urlPath":"/only-in-prod"},"response":{"status":200,"body":"prod"}}"""),
            };
            seed.Headers.Add("X-Mockifyr-Tenant", "prod");
            using var seeded = await client.SendAsync(seed);
            Assert.Equal(HttpStatusCode.Created, seeded.StatusCode);

            using var backupRequest = new HttpRequestMessage(HttpMethod.Get, "/__admin/backup");
            backupRequest.Headers.Add("X-Mockifyr-Tenant", "prod");
            using var backupResponse = await client.SendAsync(backupRequest);
            var archive = await backupResponse.Content.ReadAsStringAsync();

            // Restoring production's archive into a staging tenant is a normal drill; the caller's
            // header decides the destination, never the tenant name written inside the file.
            using var restoreRequest = new HttpRequestMessage(HttpMethod.Post, "/__admin/restore")
            {
                Content = Json(archive),
            };
            restoreRequest.Headers.Add("X-Mockifyr-Tenant", "staging");
            using var restored = await client.SendAsync(restoreRequest);
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

            using var served = new HttpRequestMessage(HttpMethod.Get, "/only-in-prod");
            served.Headers.Add("X-Mockifyr-Tenant", "staging");
            using var response = await client.SendAsync(served);
            Assert.Equal("prod", await response.Content.ReadAsStringAsync());

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Restoring_something_that_is_not_an_archive_is_refused_and_changes_nothing()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var seeded = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/intact"},"response":{"status":200,"body":"intact"}}"""));
            Assert.Equal(HttpStatusCode.Created, seeded.StatusCode);

            // A mapping bundle is the file an operator is most likely to reach for by mistake. Treating
            // it as an archive with every section missing would silently wipe the tenant.
            using var refused = await client.PostAsync("/__admin/restore", Json("""{"mappings":[]}"""));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("Backup.Invalid", (await ReadJsonAsync(refused)).GetProperty("error").GetString());

            Assert.Equal("intact", await client.GetStringAsync("/intact"));

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task The_archive_is_downloadable_and_carries_no_observations()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            await SeedAsync(client);
            await client.GetAsync("/orders");              // journal entries the archive must not carry
            await client.GetAsync("/nothing-here");

            using var response = await client.GetAsync("/__admin/backup");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("attachment", response.Content.Headers.ContentDisposition?.ToString() ?? "");
            Assert.Contains("mockifyr-backup-default-", response.Content.Headers.ContentDisposition?.FileName ?? "");

            var archive = await response.Content.ReadAsStringAsync();
            var root = JsonDocument.Parse(archive).RootElement;
            Assert.Equal(1, root.GetProperty("mockifyrBackup").GetInt32());

            // The journal and the message inbox are observations of what happened, not configuration.
            // Restoring them would fabricate a history the target host never served.
            Assert.False(root.TryGetProperty("requests", out _));
            Assert.False(root.TryGetProperty("journal", out _));
            Assert.False(root.TryGetProperty("messages", out _));
            Assert.DoesNotContain("nothing-here", archive);

            await host.StopAsync();
            client.Dispose();
        }
    }
}
