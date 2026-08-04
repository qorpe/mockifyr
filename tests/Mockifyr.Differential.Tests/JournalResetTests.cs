using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Differential.Harness;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential validation of journal reset. A suite sharing one host has to discard what earlier
/// tests recorded, or every count asserts about the wrong test — and until this landed there was no
/// way to do it at all short of restarting the host.
/// </summary>
/// <remarks>
/// <para>
/// The oracle decided the spelling, and corrected the assumption on the way: <c>POST
/// /__admin/requests/reset</c> — the obvious guess, and what several mocking tools use — answers
/// <b>404</b> on the reference engine. <c>DELETE /__admin/requests</c> is the one that works, so that
/// is what Mockifyr implements. Both are asserted here, because the 404 is as much a part of the
/// dialect as the 200 and would otherwise be re-invented by the next person who "remembers" the API.
/// </para>
/// <para>Requires Docker.</para>
/// </remarks>
public sealed class JournalResetTests : IAsyncLifetime
{
    private const string StubJson =
        """{"request":{"method":"GET","urlPattern":"/r/.*"},"response":{"status":200,"body":"ok"}}""";

    private readonly WireMockOracle _oracle = new();

    public Task InitializeAsync() => _oracle.StartAsync();

    public async Task DisposeAsync() => await _oracle.DisposeAsync();

    [Fact]
    public async Task Deleting_the_collection_empties_the_journal_on_both_engines()
    {
        await _oracle.LoadMappingAsync(StubJson);
        foreach (var i in Enumerable.Range(1, 3))
        {
            await _oracle.SendAsync(new Generator.RequestSpec { Method = "GET", Url = $"/r/{i}" });
        }

        using var oracleAdmin = _oracle.CreateAdminClient();
        var oracleBefore = await CountAsync(oracleAdmin);
        using var oracleReset = await oracleAdmin.DeleteAsync("/__admin/requests");
        var oracleAfter = await CountAsync(oracleAdmin);

        var app = MockifyrHost.Build(["--port", "0"]);
        await app.StartAsync();
        await using (app)
        {
            using var client = Client(app);
            using var created = await client.PostAsync("/__admin/mappings", Json(StubJson));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            foreach (var i in Enumerable.Range(1, 3))
            {
                using var _ = await client.GetAsync($"/r/{i}");
            }

            var before = await CountAsync(client);
            using var reset = await client.DeleteAsync("/__admin/requests");
            var after = await CountAsync(client);

            // The oracle defines all three: what was counted, that the reset is accepted, and that the
            // journal is empty rather than merely filtered afterwards.
            Assert.Equal(3, oracleBefore);
            Assert.Equal(HttpStatusCode.OK, oracleReset.StatusCode);
            Assert.Equal(0, oracleAfter);

            Assert.Equal(oracleBefore, before);
            Assert.Equal(oracleReset.StatusCode, reset.StatusCode);
            Assert.Equal(oracleAfter, after);
        }
    }

    [Fact]
    public async Task The_listing_is_empty_after_a_reset_on_both_engines()
    {
        await _oracle.LoadMappingAsync(StubJson);
        await _oracle.SendAsync(new Generator.RequestSpec { Method = "GET", Url = "/r/1" });

        using var oracleAdmin = _oracle.CreateAdminClient();
        using (await oracleAdmin.DeleteAsync("/__admin/requests")) { }

        var oracleEntries = JournalCount(await oracleAdmin.GetStringAsync("/__admin/requests"));

        var app = MockifyrHost.Build(["--port", "0"]);
        await app.StartAsync();
        await using (app)
        {
            using var client = Client(app);
            using var created = await client.PostAsync("/__admin/mappings", Json(StubJson));
            using (await client.GetAsync("/r/1")) { }
            using (await client.DeleteAsync("/__admin/requests")) { }

            // Counting and listing read the same store, so a reset that satisfied one and not the other
            // would be the worst outcome — a suite that looks clean and is not.
            Assert.Equal(0, oracleEntries);
            Assert.Equal(oracleEntries, JournalCount(await client.GetStringAsync("/__admin/requests")));
        }
    }

    [Fact]
    public async Task Resetting_an_already_empty_journal_is_accepted_on_both_engines()
    {
        using var oracleAdmin = _oracle.CreateAdminClient();
        using var oracleReset = await oracleAdmin.DeleteAsync("/__admin/requests");

        var app = MockifyrHost.Build(["--port", "0"]);
        await app.StartAsync();
        await using (app)
        {
            using var client = Client(app);
            using var reset = await client.DeleteAsync("/__admin/requests");

            // Teardown runs whether or not the test recorded anything; an empty reset must not be an error.
            Assert.Equal(HttpStatusCode.OK, oracleReset.StatusCode);
            Assert.Equal(oracleReset.StatusCode, reset.StatusCode);
        }
    }

    [Fact]
    public async Task The_reset_spelling_the_oracle_rejects_is_rejected_here_too()
    {
        using var oracleAdmin = _oracle.CreateAdminClient();
        using var oracleGuess = await oracleAdmin.PostAsync("/__admin/requests/reset", Json("{}"));

        var app = MockifyrHost.Build(["--port", "0"]);
        await app.StartAsync();
        await using (app)
        {
            using var client = Client(app);
            using var guess = await client.PostAsync("/__admin/requests/reset", Json("{}"));

            // Implementing the intuitive spelling as an alias would be a divergence, however friendly:
            // a stub set or script that works here must work against the reference engine.
            Assert.Equal(HttpStatusCode.NotFound, oracleGuess.StatusCode);
            Assert.Equal(oracleGuess.StatusCode, guess.StatusCode);
        }
    }

    [Fact]
    public async Task A_reset_clears_only_the_tenant_that_asked()
    {
        // No oracle for this one — the reference engine has no tenants — so it is a self-test, and the
        // one that matters most in practice: parallel suites share a host precisely by taking a tenant
        // each, and a reset that reached across them would corrupt a neighbour's counts silently.
        var app = MockifyrHost.Build(["--port", "0"]);
        await app.StartAsync();
        await using (app)
        {
            using var client = Client(app);
            // A stub belongs to a tenant, so each tenant gets its own — the same isolation that makes
            // sharing a host safe in the first place.
            await CreateForAsync(client, "acme");
            await CreateForAsync(client, "globex");

            await ServeAsync(client, "acme", "/r/1");
            await ServeAsync(client, "globex", "/r/2");

            using var reset = new HttpRequestMessage(HttpMethod.Delete, "/__admin/requests");
            reset.Headers.Add("X-Mockifyr-Tenant", "acme");
            using var resetResponse = await client.SendAsync(reset);
            Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);

            Assert.Equal(0, await CountAsync(client, "acme"));
            Assert.Equal(1, await CountAsync(client, "globex"));
        }
    }

    private static async Task CreateForAsync(HttpClient client, string tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
        {
            Content = Json(StubJson),
        };

        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task ServeAsync(HttpClient client, string tenant, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<int> CountAsync(HttpClient client, string? tenant = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/requests/count")
        {
            Content = Json("{}"),
        };

        if (tenant is not null)
        {
            request.Headers.Add("X-Mockifyr-Tenant", tenant);
        }

        using var response = await client.SendAsync(request);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("count").GetInt32();
    }

    private static int JournalCount(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("requests").GetArrayLength();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static HttpClient Client(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return new HttpClient { BaseAddress = new Uri(address) };
    }
}
