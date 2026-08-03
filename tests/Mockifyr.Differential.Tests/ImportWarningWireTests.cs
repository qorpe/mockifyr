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
    public async Task A_bodyFileName_stub_is_created_and_the_gap_is_reported()
    {
        var (host, client) = await StartAsync();
        await using (host)
        {
            using var created = await client.PostAsync("/__admin/mappings", Json(
                """{"request":{"method":"GET","urlPath":"/file-backed"},"response":{"status":200,"bodyFileName":"body.json"}}"""));

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var payload = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
            Assert.Contains("bodyFileName", payload.GetProperty("warnings").EnumerateArray().Single().GetString());

            // And the warned-about behaviour is exactly what happens: matched, empty body. The warning
            // is the only thing standing between that and an afternoon of debugging.
            using var served = await client.GetAsync("/file-backed");
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Empty(await served.Content.ReadAsStringAsync());

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
                  {"request":{"method":"GET","urlPath":"/a"},"response":{"status":200,"bodyFileName":"a.json"}},
                  {"request":{"method":"GET","urlPath":"/b"},"response":{"status":200,"bodyFileName":"b.json"}},
                  {"request":{"method":"GET","urlPath":"/c"},"response":{"status":200,"delayDistribution":{"type":"lognormal"}}},
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
}
