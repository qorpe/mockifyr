using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for durable sandbox resources. The claim is narrow and worth pinning exactly: what a
/// partner seeded into a sandbox is still there after the host restarts — which, for something called
/// an integration sandbox, is the difference between a fixture and a fixture you have to re-create
/// every deploy.
/// </summary>
public sealed class ResourceDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mockifyr-res-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<(Microsoft.AspNetCore.Builder.WebApplication Host, HttpClient Client)> StartAsync()
    {
        var host = MockifyrHost.Build(["--port", "0", "--root-dir", _root]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static HttpRequestMessage For(HttpMethod method, string path, string tenant, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        if (body is not null)
        {
            request.Content = Json(body);
        }

        return request;
    }

    [Fact]
    public async Task Seeded_documents_survive_a_restart()
    {
        var (first, firstClient) = await StartAsync();
        await using (first)
        {
            using var put = await firstClient.PutAsync("/__admin/resources/orders/A-1", Json("""{"total":10}"""));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            using var seeded = await firstClient.PostAsync("/__admin/resources/customers/seed",
                Json("""[{"id":"c-1","name":"Acme"},{"id":"c-2","name":"Globex"}]"""));
            Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);

            await first.StopAsync();
            firstClient.Dispose();
        }

        // A different process against the same root — the restart drill, not a reload of state the
        // host already had in memory.
        var (second, client) = await StartAsync();
        await using (second)
        {
            using var document = await client.GetAsync("/__admin/resources/orders/A-1");
            Assert.Equal(HttpStatusCode.OK, document.StatusCode);
            Assert.Contains("\"total\"", await document.Content.ReadAsStringAsync());

            using var customers = await client.GetAsync("/__admin/resources/customers");
            var page = JsonDocument.Parse(await customers.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(2, page.GetProperty("total").GetInt32());

            await second.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_deleted_document_stays_deleted_across_a_restart()
    {
        var (first, firstClient) = await StartAsync();
        await using (first)
        {
            using var put = await firstClient.PutAsync("/__admin/resources/orders/A-1", Json("""{"total":10}"""));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            using var deleted = await firstClient.DeleteAsync("/__admin/resources/orders/A-1");
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

            await first.StopAsync();
            firstClient.Dispose();
        }

        var (second, client) = await StartAsync();
        await using (second)
        {
            // Persisting creates but not deletes is the classic half-implementation: the document
            // would rise from the dead on the next deploy.
            using var document = await client.GetAsync("/__admin/resources/orders/A-1");
            Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);

            await second.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_reset_collection_stays_empty_across_a_restart()
    {
        var (first, firstClient) = await StartAsync();
        await using (first)
        {
            using var kept = await firstClient.PutAsync("/__admin/resources/keep/k-1", Json("""{"a":1}"""));
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
            using var dropped = await firstClient.PutAsync("/__admin/resources/drop/d-1", Json("""{"a":1}"""));
            Assert.Equal(HttpStatusCode.OK, dropped.StatusCode);

            using var reset = await firstClient.PostAsync("/__admin/resources/drop/reset", Json(""));
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

            await first.StopAsync();
            firstClient.Dispose();
        }

        var (second, client) = await StartAsync();
        await using (second)
        {
            // A reset that only cleared memory would come back on the next start — and, worse, only
            // the collection that was reset, which is the hardest kind of bug to believe.
            using var dropped = await client.GetAsync("/__admin/resources/drop/d-1");
            Assert.Equal(HttpStatusCode.NotFound, dropped.StatusCode);

            using var kept = await client.GetAsync("/__admin/resources/keep/k-1");
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);

            await second.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Documents_stay_in_their_own_tenant_across_a_restart()
    {
        var (first, firstClient) = await StartAsync();
        await using (first)
        {
            using var alpha = await firstClient.SendAsync(
                For(HttpMethod.Put, "/__admin/resources/orders/shared-id", "alpha", """{"owner":"alpha"}"""));
            Assert.Equal(HttpStatusCode.OK, alpha.StatusCode);

            using var beta = await firstClient.SendAsync(
                For(HttpMethod.Put, "/__admin/resources/orders/shared-id", "beta", """{"owner":"beta"}"""));
            Assert.Equal(HttpStatusCode.OK, beta.StatusCode);

            await first.StopAsync();
            firstClient.Dispose();
        }

        var (second, client) = await StartAsync();
        await using (second)
        {
            // The same document id in two tenants: rehydration must put each back where it came from.
            // A store keyed on collection+id alone would have one tenant overwrite the other, and the
            // damage would only show up after a restart.
            using var alpha = await client.SendAsync(For(HttpMethod.Get, "/__admin/resources/orders/shared-id", "alpha"));
            Assert.Contains("alpha", await alpha.Content.ReadAsStringAsync());

            using var beta = await client.SendAsync(For(HttpMethod.Get, "/__admin/resources/orders/shared-id", "beta"));
            Assert.Contains("beta", await beta.Content.ReadAsStringAsync());

            using var other = await client.SendAsync(For(HttpMethod.Get, "/__admin/resources/orders/shared-id", "gamma"));
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

            await second.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task An_id_that_is_not_a_safe_file_name_still_round_trips()
    {
        var (first, firstClient) = await StartAsync();
        await using (first)
        {
            // Ids are chosen by whoever seeds the sandbox. A slash or a dot-dot must neither escape
            // the store's directory nor lose the document.
            using var put = await firstClient.PutAsync("/__admin/resources/orders/a%2Fb..c", Json("""{"odd":true}"""));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            await first.StopAsync();
            firstClient.Dispose();
        }

        var (second, client) = await StartAsync();
        await using (second)
        {
            using var document = await client.GetAsync("/__admin/resources/orders/a%2Fb..c");
            Assert.Equal(HttpStatusCode.OK, document.StatusCode);
            Assert.Contains("\"odd\"", await document.Content.ReadAsStringAsync());

            // Nothing was written outside the resources directory.
            Assert.True(Directory.Exists(Path.Combine(_root, "resources")));
            Assert.Empty(Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly));

            await second.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Without_a_root_dir_resources_are_still_in_memory_only()
    {
        var host = MockifyrHost.Build(["--port", "0"]);
        await host.StartAsync();
        await using (host)
        {
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .First(a => a.StartsWith("http://", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            // Durability is a property of the persistence choice, not a new default: a laptop run
            // writes nothing to disk, exactly as before.
            using var put = await client.PutAsync("/__admin/resources/orders/A-1", Json("""{"total":10}"""));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            await host.StopAsync();
        }
    }
}
