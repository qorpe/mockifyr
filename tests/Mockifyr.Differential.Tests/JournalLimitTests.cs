using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Differential.Harness;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Differential validation of the bounded request journal (#220): both sides start with
/// <c>--max-request-journal-entries 3</c>, serve five requests, and must retain the same three —
/// the newest ones, oldest evicted first. The reference oracle defines the eviction semantics.
/// Requires Docker.
/// </summary>
public sealed class JournalLimitTests : IAsyncLifetime
{
    private const string StubJson =
        """{"request":{"method":"GET","urlPattern":"/j/.*"},"response":{"status":200,"body":"ok"}}""";

    private readonly WireMockOracle _oracle = new("--max-request-journal-entries", "3");

    public Task InitializeAsync() => _oracle.StartAsync();

    public async Task DisposeAsync() => await _oracle.DisposeAsync();

    [Fact]
    public async Task Journal_keeps_the_newest_entries_when_the_cap_is_exceeded()
    {
        await _oracle.LoadMappingAsync(StubJson);
        foreach (var i in Enumerable.Range(1, 5))
        {
            await _oracle.SendAsync(new Generator.RequestSpec { Method = "GET", Url = $"/j/{i}" });
        }

        using var oracleAdmin = _oracle.CreateAdminClient();
        var oracleUrls = JournalUrls(await oracleAdmin.GetStringAsync("/__admin/requests"));

        var app = MockifyrHost.Build(["--port", "0", "--max-request-journal-entries", "3"]);
        await app.StartAsync();
        await using (app)
        {
            using var client = Client(app);
            using var created = await client.PostAsync("/__admin/mappings",
                new StringContent(StubJson, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            foreach (var i in Enumerable.Range(1, 5))
            {
                using var _ = await client.GetAsync($"/j/{i}");
            }

            var mockifyrUrls = JournalUrls(await client.GetStringAsync("/__admin/requests"));

            // The oracle defines the semantics: exactly the cap survives, and it is the NEWEST
            // three — the two oldest requests are gone on both sides.
            Assert.Equal(3, oracleUrls.Count);
            Assert.Equal(oracleUrls.OrderBy(u => u), mockifyrUrls.OrderBy(u => u));
            Assert.Equal(new[] { "/j/3", "/j/4", "/j/5" }, mockifyrUrls.OrderBy(u => u).ToArray());
        }
    }

    /// <summary>Extracts request URLs from either side's journal listing (both wrap in `requests`).</summary>
    private static List<string> JournalUrls(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var urls = new List<string>();
        foreach (var entry in doc.RootElement.GetProperty("requests").EnumerateArray())
        {
            // Oracle shape nests under `request.url`; the Mockifyr admin lists `url` flat.
            urls.Add(entry.TryGetProperty("request", out var nested)
                ? nested.GetProperty("url").GetString()!
                : entry.GetProperty("url").GetString()!);
        }

        return urls;
    }

    private static HttpClient Client(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1")),
    };
}
