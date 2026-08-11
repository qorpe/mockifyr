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
/// A secret environment value across every surface that reports one (#348): the admin API, an export
/// bundle, and the response a stub actually serves. Mockifyr-specific, so a self-test; no Docker.
/// </summary>
/// <remarks>
/// The assertions are written as "the value we want" wherever they can be. A test phrased as "the
/// secret does not appear" passes just as well when the whole key is missing, when the endpoint 404s,
/// or when a typo makes the request fetch nothing — which is how a redaction test comes to guard
/// nothing at all.
/// </remarks>
public sealed class SecretEnvironmentWireTests : IAsyncLifetime
{
    private const string Literal = "whsec-live-9c1f";

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

        using var declared = await _client.PutAsync("/__admin/environments/signingKey", Json($$"""
            {"activeValue":"live","values":[
              {"name":"live","value":"{{Literal}}","secret":true},
              {"name":"test","value":"whsec-test-0000"}
            ]}
            """));
        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<JsonElement> ReadKey()
    {
        using var response = await _client!.GetAsync("/__admin/environments");
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("environments")
            .EnumerateArray().Single(e => e.GetProperty("key").GetString() == "signingKey").Clone();
    }

    [Fact]
    public async Task The_admin_api_reports_the_marker_and_withholds_both_literals()
    {
        var key = await ReadKey();

        // Positive first: the key is really there and really marked, so the absences below mean
        // something. Then the two places a literal could escape — the value in the list, and the
        // `resolved` literal computed from the active one.
        Assert.True(key.GetProperty("secret").GetBoolean());
        var live = key.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("name").GetString() == "live");
        Assert.True(live.GetProperty("secret").GetBoolean());
        Assert.False(live.TryGetProperty("value", out _));
        Assert.Equal(JsonValueKind.Null, key.GetProperty("resolved").ValueKind);

        // The non-secret sibling is untouched — redaction that swallowed everything would be easy and
        // useless.
        var test = key.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("name").GetString() == "test");
        Assert.Equal("whsec-test-0000", test.GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_stub_still_renders_the_secret_because_that_is_the_whole_point()
    {
        using var stub = await _client!.PostAsync("/__admin/mappings", Json("""
            {"request":{"method":"GET","url":"/sign"},"response":{"status":200,"body":"sig={{signingKey}}"}}
            """));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        using var served = await _client.GetAsync("/sign");

        Assert.Equal($"sig={Literal}", await served.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Saving_the_key_back_exactly_as_it_was_read_does_not_destroy_the_secret()
    {
        // The hazard redaction creates: a screen reads (literal withheld), the operator renames the
        // other value, and the write sends back what it was shown. Taken literally that stores "".
        var asRead = await ReadKey();
        using var saved = await _client!.PutAsync("/__admin/environments/signingKey", Json($$"""
            {"activeValue":"live","values":[
              {"name":"live","secret":true},
              {"name":"testing","value":"whsec-test-0000"}
            ]}
            """));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Asserted by serving it: whether the literal survived is a question only the served response
        // can answer, since every reporting surface withholds it by design.
        using var stub = await _client.PostAsync("/__admin/mappings", Json("""
            {"request":{"method":"GET","url":"/after-save"},"response":{"status":200,"body":"sig={{signingKey}}"}}
            """));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        using var served = await _client.GetAsync("/after-save");
        Assert.Equal($"sig={Literal}", await served.Content.ReadAsStringAsync());
        Assert.True(asRead.GetProperty("secret").GetBoolean());
    }

    [Fact]
    public async Task An_explicit_new_literal_rotates_it()
    {
        using var rotated = await _client!.PutAsync("/__admin/environments/signingKey", Json("""
            {"activeValue":"live","values":[{"name":"live","value":"whsec-live-rotated","secret":true}]}
            """));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        using var stub = await _client.PostAsync("/__admin/mappings", Json("""
            {"request":{"method":"GET","url":"/rotated"},"response":{"status":200,"body":"sig={{signingKey}}"}}
            """));
        Assert.Equal(HttpStatusCode.Created, stub.StatusCode);

        using var served = await _client.GetAsync("/rotated");
        Assert.Equal("sig=whsec-live-rotated", await served.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_export_bundle_carries_the_marker_and_not_the_literal()
    {
        // Bundles are the artefact people attach to tickets and commit to repositories, so redaction
        // that stopped at the API would stop short of where the leak actually happens.
        using var response = await _client!.GetAsync("/__admin/backup");
        var archive = await response.Content.ReadAsStringAsync();

        var key = JsonDocument.Parse(archive).RootElement.GetProperty("environments")
            .EnumerateArray().Single(e => e.GetProperty("key").GetString() == "signingKey");
        var live = key.GetProperty("values").EnumerateArray().Single(v => v.GetProperty("name").GetString() == "live");

        Assert.True(live.GetProperty("secret").GetBoolean());
        Assert.False(live.TryGetProperty("value", out _));

        // And the literal is nowhere in the document at all — the one place a blunt substring check is
        // the right check, because here it is the actual claim.
        Assert.DoesNotContain(Literal, archive, StringComparison.Ordinal);
    }
}
