using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for observability (#246): the scrape endpoint exists only when asked for, is
/// reachable without credentials (a scraper cannot authenticate), carries the serving metrics with
/// bounded labels, and never appears on a host that did not enable it.
/// </summary>
public sealed class ObservabilityTests
{
    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication Host, HttpClient Client)> StartAsync(params string[] args)
    {
        var host = MockifyrHost.Build([.. new[] { "--port", "0" }.Concat(args)]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }

    [Fact]
    public async Task Serving_metrics_are_exposed_with_bounded_labels()
    {
        var (host, client) = await StartAsync("--metrics", "true");
        await using (host)
        {
            using var stub = await client.PostAsync("/__admin/mappings", new StringContent(
                """{"request":{"method":"GET","urlPath":"/measured"},"response":{"status":200,"body":"ok"}}""",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

            await client.GetAsync("/measured");          // a match
            await client.GetAsync("/nothing-here");      // a miss

            var scrape = await client.GetStringAsync("/__admin/metrics");

            // The serving counter exists and carries the labels a dashboard needs…
            Assert.Contains("mockifyr_requests_served", scrape);
            Assert.Contains("tenant=\"default\"", scrape);
            Assert.Contains("matched=\"true\"", scrape);
            Assert.Contains("matched=\"false\"", scrape);

            // …and NOT the labels that would explode cardinality on a host with thousands of stubs.
            Assert.DoesNotContain("stub_id", scrape);
            Assert.DoesNotContain("/measured", scrape);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task The_scrape_endpoint_needs_no_credentials_but_everything_else_still_does()
    {
        var (host, client) = await StartAsync("--metrics", "true", "--admin-user", "op", "--admin-pass", "secret");
        await using (host)
        {
            // A Prometheus scraper cannot carry credentials; the series are counts and latencies,
            // never payloads.
            using var scrape = await client.GetAsync("/__admin/metrics");
            Assert.Equal(HttpStatusCode.OK, scrape.StatusCode);

            using var guarded = await client.GetAsync("/__admin/mappings");
            Assert.Equal(HttpStatusCode.Unauthorized, guarded.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Without_the_flag_no_metrics_endpoint_exists()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            // Opt-in: a mock on a laptop should not open a metrics surface. The fallback serves it as
            // an unmatched mock path, so the assertion is "not a scrape response" rather than 404.
            using var scrape = await client.GetAsync("/__admin/metrics");
            var body = await scrape.Content.ReadAsStringAsync();
            Assert.DoesNotContain("mockifyr_requests_served", body);

            // Serving is unaffected either way.
            using var stub = await client.PostAsync("/__admin/mappings", new StringContent(
                """{"request":{"method":"GET","urlPath":"/plain"},"response":{"status":200,"body":"served"}}""",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
            Assert.Equal("served", await client.GetStringAsync("/plain"));

            await host.StopAsync();
            client.Dispose();
        }
    }
}
