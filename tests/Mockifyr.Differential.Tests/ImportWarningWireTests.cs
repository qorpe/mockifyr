using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire coverage for import warnings (1.0): a stub that uses a field this engine accepts but does not
/// act on is still created — refusing it would break importing a mapping set written for the reference
/// engine — but the caller is told, so the gap is read as a message instead of debugged as behaviour.
/// </summary>
public sealed class ImportWarningWireTests
{
    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication Host, HttpClient Client)> StartAsync()
    {
        var host = MockifyrHost.Build(["--port", "0"]);
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        return (host, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_delayDistribution_stub_is_created_and_the_gap_is_reported()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var created = await client.PostAsync("/__admin/mappings", Json(
                """
                {"request":{"method":"GET","urlPath":"/slow"},
                 "response":{"status":200,"body":"ok","delayDistribution":{"type":"lognormal","median":90,"sigma":0.1}}}
                """));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var payload = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
            Assert.Contains("lognormal", payload.GetProperty("warnings").EnumerateArray().Single().GetString());

            // And the warned-about behaviour is exactly what happens: served, with no delay. The
            // warning is the only thing standing between that and an afternoon of debugging.
            using var served = await client.GetAsync("/slow");
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal("ok", await served.Content.ReadAsStringAsync());

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_bodyFileName_stub_no_longer_warns_and_says_so_loudly_when_the_file_is_absent()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var created = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/file-backed"},"response":{"status":200,"bodyFileName":"body.json"}}"""));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            // The field is implemented now, so warning about it would be crying wolf.
            Assert.False(JsonDocument.Parse(await created.Content.ReadAsStringAsync())
                .RootElement.TryGetProperty("warnings", out _));

            // This host has no --root-dir, so it has no file store. The answer is a 500 that names the
            // file — not a 200 with an empty body, which would read as a matching problem.
            using var served = await client.GetAsync("/file-backed");
            Assert.Equal(HttpStatusCode.InternalServerError, served.StatusCode);
            Assert.Contains("body.json", await served.Content.ReadAsStringAsync());

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task An_ordinary_stub_carries_no_warnings_field_at_all()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var created = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/plain"},"response":{"status":200,"body":"ok"}}"""));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var payload = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;

            // Absent, not empty: existing clients parse this response, and a field that appears on
            // every create would be noise they have to learn to ignore.
            Assert.False(payload.TryGetProperty("warnings", out _));
            Assert.True(payload.TryGetProperty("id", out _));

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task A_bundle_import_reports_each_distinct_gap_once()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var imported = await client.PostAsync("/__admin/mappings/import", Json(
                """
                {"mappings":[
                  {"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"delayDistribution":{"type":"lognormal"}}},
                  {"request":{"method":"GET","urlPath":"/b"},"response":{"status":200,"delayDistribution":{"type":"lognormal"}}},
                  {"request":{"method":"GET","urlPath":"/c"},"response":{"status":200,"delayDistribution":{"type":"chunkedDribble"}}},
                  {"request":{"method":"GET","urlPath":"/d"},"response":{"status":200,"body":"fine"}}
                ]}
                """));

            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            var warnings = JsonDocument.Parse(await imported.Content.ReadAsStringAsync())
                .RootElement.GetProperty("warnings").EnumerateArray().ToList();

            // Two distinct gaps across four stubs — not three lines, and not one.
            Assert.Equal(2, warnings.Count);

            // Every stub in the bundle still landed: warning is not refusing.
            using var stubs = await client.GetAsync("/__admin/mappings");
            Assert.Equal(4, JsonDocument.Parse(await stubs.Content.ReadAsStringAsync())
                .RootElement.GetProperty("mappings").GetArrayLength());

            await host.StopAsync();
            client.Dispose();
        }
    }

    private const string PublishingStub =
        """
        {"request":{"method":"POST","urlPath":"/payments"},"response":{"status":201,"body":"{\"ok\":true}"},
         "postServeActions":[{"name":"publish","parameters":{"topic":"payments.events","body":"emitted"}}]}
        """;

    [Fact]
    public async Task A_publish_action_on_a_host_with_no_broker_is_created_and_reported()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var created = await client.PostAsync("/__admin/mappings", Json(PublishingStub));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var warning = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
                .RootElement.GetProperty("warnings").EnumerateArray().Single().GetString()!;
            Assert.Contains("--kafka-bootstrap", warning, StringComparison.Ordinal);

            // And the warned-about behaviour is exactly what happens: a perfect 201 and no event at all.
            // Without the warning that reads as a broker outage, and the flag is the last place anyone
            // would look.
            using var served = await client.PostAsync("/payments", Json("""{"orderId":"ord-7"}"""));
            Assert.Equal(HttpStatusCode.Created, served.StatusCode);

            await host.StopAsync();
            client.Dispose();
        }
    }

    [Fact]
    public async Task The_same_stub_on_a_host_with_a_broker_is_not_reported()
    {
        // Nothing has to connect for this: the warning asks whether a publisher exists, not whether the
        // broker answers — an unreachable broker is a journal entry, which is a different report.
        var host = MockifyrHost.Build(["--port", "0", "--kafka-bootstrap", "localhost:19092"]);
        await using (host)
        {
            await host.StartAsync();
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var created = await client.PostAsync("/__admin/mappings", Json(PublishingStub));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.False(JsonDocument.Parse(await created.Content.ReadAsStringAsync())
                .RootElement.TryGetProperty("warnings", out _));

            await host.StopAsync();
        }
    }
}
