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
/// Environment composition at the wire (#352): a value that resolves another, a cycle refused at write
/// time, a shared host value inherited and overridden, and a secret that stays secret through a
/// composed value. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
public sealed class EnvironmentCompositionWireTests : IAsyncLifetime
{
    private WebApplication? _host;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build([
            "--port", "0", "--https-port", "0",
            "--env", "apiBase=https://shared.example",
            "--env", "sharedIban=DE89370400440532013000",
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

    private async Task<HttpResponseMessage> PutKeyAsync(string key, string body, string tenant = "default")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/__admin/environments/{key}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        return await _client!.SendAsync(request);
    }

    private async Task<JsonElement> EnvironmentsAsync(string tenant = "default")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__admin/environments");
        request.Headers.Add("X-Mockifyr-Tenant", tenant);
        using var response = await _client!.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task A_served_response_resolves_a_value_through_another_value()
    {
        Assert.Equal(HttpStatusCode.OK, (await PutKeyAsync("paymentsUrl",
            """{"activeValue":"v","values":[{"name":"v","value":"{{apiBase}}/v2/payments"}]}""", "compose")).StatusCode);

        using var stub = new HttpRequestMessage(HttpMethod.Post, "/__admin/mappings")
        {
            Content = new StringContent(
                """{"request":{"method":"GET","urlPath":"/where"},"response":{"status":200,"body":"{{paymentsUrl}}"}}""",
                Encoding.UTF8, "application/json"),
        };
        stub.Headers.Add("X-Mockifyr-Tenant", "compose");
        Assert.Equal(HttpStatusCode.Created, (await _client!.SendAsync(stub)).StatusCode);

        using var call = new HttpRequestMessage(HttpMethod.Get, "/where");
        call.Headers.Add("X-Mockifyr-Tenant", "compose");
        using var served = await _client.SendAsync(call);

        Assert.Equal("https://shared.example/v2/payments", await served.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_cycle_is_refused_when_it_is_written_rather_than_when_it_is_served()
    {
        await PutKeyAsync("a", """{"activeValue":"v","values":[{"name":"v","value":"{{b}}"}]}""", "cycle");

        using var refused = await PutKeyAsync("b", """{"activeValue":"v","values":[{"name":"v","value":"{{a}}"}]}""", "cycle");

        // 400, which is what every other environment validation on this surface answers — a new code
        // for a new rule would be a contract change dressed as a feature.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = await refused.Content.ReadAsStringAsync();
        Assert.Contains("Environment.ReferenceCycle", error);
        // The message names the keys, because "there is a cycle" is a puzzle handed back to whoever
        // just made one.
        Assert.Contains("a", error);
        Assert.Contains("b", error);
    }

    [Fact]
    public async Task A_shared_value_is_listed_as_inherited_until_the_tenant_defines_it()
    {
        var before = (await EnvironmentsAsync("inherit")).GetProperty("environments").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "apiBase");
        Assert.True(before.GetProperty("inherited").GetBoolean());
        Assert.Equal("https://shared.example", before.GetProperty("resolved").GetString());

        await PutKeyAsync("apiBase", """{"activeValue":"v","values":[{"name":"v","value":"https://own.example"}]}""", "inherit");

        var after = (await EnvironmentsAsync("inherit")).GetProperty("environments").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "apiBase");

        Assert.False(after.GetProperty("inherited").GetBoolean());
        Assert.Equal("https://own.example", after.GetProperty("resolved").GetString());
        // The other shared value is untouched by the override.
        Assert.True((await EnvironmentsAsync("inherit")).GetProperty("environments").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "sharedIban").GetProperty("inherited").GetBoolean());
    }

    [Fact]
    public async Task A_secret_stays_withheld_through_a_value_that_composes_it()
    {
        await PutKeyAsync("apiToken",
            """{"activeValue":"v","values":[{"name":"v","value":"sk-live-secret","secret":true}]}""", "secrets");
        await PutKeyAsync("authHeader",
            """{"activeValue":"v","values":[{"name":"v","value":"Bearer {{apiToken}}"}]}""", "secrets");

        var listed = await EnvironmentsAsync("secrets");
        var header = listed.GetProperty("environments").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "authHeader");

        Assert.True(header.GetProperty("secret").GetBoolean());
        Assert.Equal(JsonValueKind.Null, header.GetProperty("resolved").ValueKind);
        Assert.DoesNotContain("sk-live-secret", listed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_constant_is_refused_when_it_is_offered_more_than_one_value()
    {
        using var refused = await PutKeyAsync("region",
            """{"activeValue":"eu","constant":true,"values":[{"name":"eu","value":"eu-west"},{"name":"us","value":"us-east"}]}""",
            "constants");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Environment.ConstantHasOneValue", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_constant_is_reported_as_one_so_the_screen_can_show_it_is_not_a_choice()
    {
        Assert.Equal(HttpStatusCode.OK, (await PutKeyAsync("region",
            """{"activeValue":"eu","constant":true,"values":[{"name":"eu","value":"eu-west"}]}""",
            "constants")).StatusCode);

        var entry = (await EnvironmentsAsync("constants")).GetProperty("environments").EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "region");

        Assert.True(entry.GetProperty("constant").GetBoolean());
    }
}
