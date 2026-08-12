using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// Idempotent replay at the wire (#358): a retried write does not create a second payment, and the
/// journal says which of the two was a replay. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class IdempotencyWireTests : IAsyncLifetime
{
    private const string CountingStub =
        """
        {"request":{"method":"POST","urlPath":"/payments"},
         "response":{"status":201,"state":{"operation":"create","collection":"payments"},
                     "body":"{\"id\":\"{{state.id}}\"}"}}
        """;

    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--idempotency", "true"]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        // One stub per tenant this test drives: a stub belongs to a tenant, so the tenants that prove
        // isolation need their own.
        foreach (var tenant in new[] { "default", "no-key", "reuse", "journal", "double-submit" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
            {
                Content = new StringContent(CountingStub, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Mockifyr-Tenant", tenant);
            using var stub = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Pay(string? key, string body = """{"amount":10}""", string tenant = "default")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        return await _client!.SendAsync(request);
    }

    private async Task<int> PaymentCountAsync(string tenant = "default")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/resources/payments");
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await _client!.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("documents").GetArrayLength();
    }

    [Fact]
    public async Task A_retry_with_the_same_key_replays_instead_of_creating_a_second_payment()
    {
        // The whole point: a partner's client library retries on a timeout, and their production
        // integration is built specifically never to see two payments.
        using var first = await Pay("key-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync();

        using var retry = await Pay("key-1");

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(body, await retry.Content.ReadAsStringAsync());
        Assert.Equal("true", retry.Headers.GetValues("Idempotency-Replayed").First());
        Assert.Equal(1, await PaymentCountAsync());
    }

    [Fact]
    public async Task Without_a_key_a_retry_creates_a_second_payment_exactly_as_before()
    {
        await Pay(key: null, tenant: "no-key");
        await Pay(key: null, tenant: "no-key");

        Assert.Equal(2, await PaymentCountAsync("no-key"));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_refused_rather_than_answered()
    {
        await Pay("key-2", """{"amount":10}""", "reuse");

        using var different = await Pay("key-2", """{"amount":99}""", "reuse");

        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Contains("Idempotency.KeyReused", await different.Content.ReadAsStringAsync());
        Assert.Equal(1, await PaymentCountAsync("reuse"));
    }

    [Fact]
    public async Task The_journal_shows_two_requests_and_says_which_one_was_a_replay()
    {
        // Both really arrived. A journal that hid the second would disagree with the client about what
        // happened; one that showed it as a fresh serve would say the sandbox did the work twice.
        await Pay("key-3", tenant: "journal");
        await Pay("key-3", tenant: "journal");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/requests");
        request.Headers.Add("X-Mockifyr-Tenant", "journal");
        using var response = await _client!.SendAsync(request);
        var entries = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("requests").EnumerateArray().ToList();

        Assert.Equal(2, entries.Count);
        Assert.Single(entries, entry => entry.TryGetProperty("replayed", out var flag) && flag.GetBoolean());
    }

    [Fact]
    public async Task A_tenant_can_keep_replay_off_while_the_host_has_it_on()
    {
        // A suite that exists to test double submission must not be quietly fixed by a host setting.
        using var declared = await _client!.PostAsync("/__admin/tenants", new StringContent(
            """{"id":"double-submit","displayName":"Double submit","idempotency":false}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, declared.StatusCode);

        await Pay("key-4", tenant: "double-submit");
        await Pay("key-4", tenant: "double-submit");

        Assert.Equal(2, await PaymentCountAsync("double-submit"));
    }
}

/// <summary>A host that was not asked to replay anything (#358).</summary>
public sealed class IdempotencyOffByDefaultTests
{
    [Fact]
    public async Task An_idempotency_key_is_ignored_unless_the_host_asks_for_it()
    {
        var host = MockifyrHost.Build(["--port", "0", "--https-port", "0"]);
        await host.StartAsync();
        try
        {
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal))
                .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var stub = await client.PostAsync("/__admin/mappings", new StringContent(
                """{"request":{"method":"POST","urlPath":"/payments"},"response":{"status":201,"state":{"operation":"create","collection":"payments"}}}""",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

            for (var i = 0; i < 2; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
                {
                    Content = new StringContent("""{"amount":10}""", Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("Idempotency-Key", "same-key");
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                Assert.False(response.Headers.Contains("Idempotency-Replayed"));
            }

            using var listed = await client.GetAsync("/__admin/resources/payments");
            Assert.Equal(2, JsonDocument.Parse(await listed.Content.ReadAsStringAsync())
                .RootElement.GetProperty("documents").GetArrayLength());
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
