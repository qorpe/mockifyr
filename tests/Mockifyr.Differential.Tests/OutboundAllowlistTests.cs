using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// The outbound allowlist at the wire (#349): with <c>--allow-outbound-host</c> in force, a webhook or
/// a proxy stub naming anything else is refused, and the refusal is visible rather than a call that
/// quietly never happened. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class OutboundAllowlistTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build([
            "--port", "0", "--https-port", "0",
            "--allow-outbound-host", "allowed.example",
        ]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task AddStub(string mapping)
    {
        using var response = await _client!.PostAsync(
            "/__admin/mappings", new StringContent(mapping, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_webhook_to_a_host_that_is_not_allowed_is_refused_and_says_so_on_the_journal()
    {
        await AddStub("""
        {"request":{"method":"GET","url":"/fires-a-hook"},"response":{"status":200},
         "postServeActions":[{"name":"webhook","parameters":{
            "method":"POST","url":"http://internal.invalid/receive","body":"{}"}}]}
        """);

        using var served = await _client!.GetAsync("/fires-a-hook");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        // The stub still serves — an allowlist governs what this host calls, not what it answers.
        // The refusal shows up beside the request that triggered it, the way a failed delivery does.
        var detail = await LatestDetail();
        Assert.Contains("internal.invalid", detail, StringComparison.Ordinal);
        Assert.Contains("allowlist", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_webhook_to_an_allowed_host_is_not_refused_by_the_policy()
    {
        // The host does not exist, so delivery fails — but it must fail as a *connection* problem,
        // never as an allowlist refusal, or the test would pass for the wrong reason and the policy
        // could be rejecting everything.
        await AddStub("""
        {"request":{"method":"GET","url":"/allowed-hook"},"response":{"status":200},
         "postServeActions":[{"name":"webhook","parameters":{
            "method":"POST","url":"http://allowed.example/receive","body":"{}"}}]}
        """);

        using var served = await _client!.GetAsync("/allowed-hook");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        Assert.DoesNotContain("allowlist", await LatestDetail(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proxy_stub_naming_a_host_that_is_not_allowed_does_not_reach_it()
    {
        await AddStub("""
        {"request":{"method":"GET","url":"/proxied"},"response":{"proxyBaseUrl":"http://internal.invalid"}}
        """);

        using var response = await _client!.GetAsync("/proxied");

        // A refusal that explains itself, not an opaque 500: this is the one proxy outcome the host can
        // describe completely, and a caller who gets nothing back cannot tell it from an upstream that
        // is simply down.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("internal.invalid", body, StringComparison.Ordinal);
        Assert.Contains("allowlist", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The newest journal entry in full. Sub-events — webhook deliveries and their errors — live on the
    /// detail route by design; the list is kept lean, so asserting against it would prove nothing.
    /// </summary>
    private async Task<string> LatestDetail()
    {
        using var list = await _client!.GetAsync("/__admin/requests");
        var id = System.Text.Json.JsonDocument.Parse(await list.Content.ReadAsStringAsync())
            .RootElement.GetProperty("requests").EnumerateArray().Last().GetProperty("id").GetString();

        using var detail = await _client.GetAsync($"/__admin/requests/{id}");
        return await detail.Content.ReadAsStringAsync();
    }
}

/// <summary>
/// The same host without an allowlist (#349). Its own class because the policy is decided once at
/// startup, and the claim is that an unconfigured host behaves exactly as it always has.
/// </summary>
public sealed class OutboundWithoutAllowlistTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--https-port", "0"]);
        await _host.StartAsync();

        var address = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .First(a => a.StartsWith("http://", StringComparison.Ordinal))
            .Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1");
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Nothing_is_refused_for_being_outbound()
    {
        using var stub = await _client!.PostAsync("/__admin/mappings", new StringContent("""
        {"request":{"method":"GET","url":"/hook"},"response":{"status":200},
         "postServeActions":[{"name":"webhook","parameters":{
            "method":"POST","url":"http://anywhere.invalid/receive","body":"{}"}}]}
        """, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        using var served = await _client.GetAsync("/hook");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        using var list = await _client.GetAsync("/__admin/requests");
        var id = System.Text.Json.JsonDocument.Parse(await list.Content.ReadAsStringAsync())
            .RootElement.GetProperty("requests").EnumerateArray().Last().GetProperty("id").GetString();
        using var detail = await _client.GetAsync($"/__admin/requests/{id}");

        // The delivery still fails (the host does not exist), but never for this reason.
        Assert.DoesNotContain("allowlist", await detail.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
