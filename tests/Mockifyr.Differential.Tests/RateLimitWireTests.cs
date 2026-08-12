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
/// The burst ceiling at the wire (#354): a second window beside the per-key hourly quota, with the
/// binding one reported in the rate headers. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class RateLimitWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;
    private string _limited = string.Empty;
    private string _unlimited = string.Empty;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build([
            "--port", "0", "--https-port", "0",
            "--sandbox-auth", "true",
            // Three requests per hour of wall clock, so the window does not roll over mid-test.
            "--rate-burst", "3/3600",
        ]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        using var stub = await _client.PostAsync("/__admin/mappings", new StringContent(
            """{"request":{"method":"GET","url":"/ping"},"response":{"status":200,"body":"pong"}}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        _limited = await IssueKey("""{"name":"limited","quotaPerHour":100}""");
        _unlimited = await IssueKey("""{"name":"unlimited"}""");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task<string> IssueKey(string body)
    {
        using var response = await _client!.PostAsync(
            "/__admin/apikeys", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString()!;
    }

    private async Task<HttpResponseMessage> Ping(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("X-Api-Key", key);
        return await _client!.SendAsync(request);
    }

    [Fact]
    public async Task The_burst_ceiling_binds_before_a_roomier_hourly_quota_and_says_so()
    {
        // The key allows 100 an hour; the host allows 3. The headers have to report the limit that is
        // actually about to stop the caller, not the roomier one.
        using var first = await Ping(_limited);
        Assert.Equal("3", first.Headers.GetValues("X-RateLimit-Limit").First());
        Assert.Equal("2", first.Headers.GetValues("X-RateLimit-Remaining").First());

        await Ping(_limited);
        await Ping(_limited);

        using var refused = await Ping(_limited);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task A_key_with_no_quota_is_still_subject_to_the_ceiling()
    {
        // "Unlimited" is a statement about a consumer's budget, not permission to melt the host — and
        // before this, a key with no quota emitted no headers and was counted by nothing.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Ping(_unlimited)).StatusCode);
        }

        using var refused = await Ping(_unlimited);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task Two_keys_do_not_share_a_ceiling()
    {
        // Otherwise one noisy partner would refuse every other partner's traffic — a host-level number
        // has to be per consumer, or it is an outage switch.
        for (var i = 0; i < 3; i++)
        {
            await Ping(_limited);
        }

        Assert.Equal(HttpStatusCode.OK, (await Ping(_unlimited)).StatusCode);
    }
}

/// <summary>
/// The same host with no ceiling configured (#354): the per-key quota behaves exactly as it did.
/// </summary>
public sealed class RateLimitWithoutBurstTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0", "--sandbox-auth", "true"]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };

        using var stub = await _client.PostAsync("/__admin/mappings", new StringContent(
            """{"request":{"method":"GET","url":"/ping"},"response":{"status":200,"body":"pong"}}""",
            Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task A_two_request_quota_still_refuses_the_third()
    {
        using var issued = await _client!.PostAsync("/__admin/apikeys", new StringContent(
            """{"name":"small","quotaPerHour":2}""", Encoding.UTF8, "application/json"));
        var key = JsonDocument.Parse(await issued.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString()!;

        async Task<HttpResponseMessage> Ping()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
            request.Headers.Add("X-Api-Key", key);
            return await _client.SendAsync(request);
        }

        using var one = await Ping();
        Assert.Equal("2", one.Headers.GetValues("X-RateLimit-Limit").First());
        await Ping();

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Ping()).StatusCode);
    }
}
