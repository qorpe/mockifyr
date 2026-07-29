using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Wire test for #227: with <c>--mask-headers</c>/<c>--mask-body-fields</c>, a real host must never
/// hand the masked values back through <c>/__admin/requests/{id}</c> — the surface the issue is
/// about — while everything else in the journal entry stays intact and serving is unaffected.
/// </summary>
public sealed class JournalMaskingWireTests : IAsyncLifetime
{
    private Microsoft.AspNetCore.Builder.WebApplication? _host;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(
            ["--port", "0", "--mask-headers", "Authorization,X-Api-Key", "--mask-body-fields", "pan,cvv"]);
        await _host.StartAsync();
        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            await _host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Masked_values_never_reach_the_journal_detail_route()
    {
        using var stub = new StringContent(
            """{"request":{"method":"POST","urlPath":"/pay"},"response":{"status":201,"body":"ok"}}""",
            Encoding.UTF8, "application/json");
        using var created = await _client.PostAsync("/__admin/mappings", stub);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/pay")
        {
            Content = new StringContent(
                """{"amount":10,"card":{"pan":"4111111111111111","cvv":"123"},"note":"visible"}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer super-secret");
        request.Headers.Add("X-Api-Key", "key-abc");
        request.Headers.Add("X-Trace-Id", "trace-42");
        using var served = await _client.SendAsync(request);

        // Serving is untouched by masking — the stub still matched and answered.
        Assert.Equal(HttpStatusCode.Created, served.StatusCode);
        Assert.Equal("ok", await served.Content.ReadAsStringAsync());

        using var list = await _client.GetAsync("/__admin/requests");
        using var doc = System.Text.Json.JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("requests").EnumerateArray().First().GetProperty("id").GetString();

        using var detail = await _client.GetAsync($"/__admin/requests/{id}");
        var body = await detail.Content.ReadAsStringAsync();

        // The secrets are gone…
        Assert.DoesNotContain("super-secret", body);
        Assert.DoesNotContain("key-abc", body);
        Assert.DoesNotContain("4111111111111111", body);
        // …and so is the masked CVV, while the readable envelope and other headers survive.
        Assert.Contains("***", body);
        Assert.Contains("trace-42", body);
        Assert.Contains("visible", body);
        Assert.Contains("amount", body);
    }
}
