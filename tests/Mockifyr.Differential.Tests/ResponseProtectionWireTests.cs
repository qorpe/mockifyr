using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// End-to-end validation of response protection (G20b, ADR 0012) against a real host. The client
/// side here does what a partner's library does — it decrypts what it receives — so these tests
/// prove the round trip the feature exists for: a mock a decrypting client accepts. No oracle
/// exists for payload cryptography, hence the self-test shape (G18/G19 precedent).
/// </summary>
public sealed class ResponseProtectionWireTests : IAsyncLifetime
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private Microsoft.AspNetCore.Builder.WebApplication? _host;
    private HttpClient _client = null!;

    private static byte[] B64(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(padded);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decrypts a JWE compact token the way the partner's client library would.</summary>
    private static string Decrypt(string token)
    {
        var parts = token.Split('.');
        var ciphertext = B64(parts[3]);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(B64(parts[2]), ciphertext, B64(parts[4]), plaintext, Encoding.ASCII.GetBytes(parts[0]));
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Encrypts a request field, so the round trip can be driven in both directions.</summary>
    private static string Encrypt(string plaintext)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"dir","enc":"A256GCM"}"""));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var body = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[body.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, body, ciphertext, tag, Encoding.ASCII.GetBytes(header));
        return $"{header}..{Base64Url(nonce)}.{Base64Url(ciphertext)}.{Base64Url(tag)}";
    }

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--decrypt-key", Convert.ToBase64String(Key)]);
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

    private async Task SeedAsync(string mappingJson)
    {
        using var content = new StringContent(mappingJson, Encoding.UTF8, "application/json");
        using var created = await _client.PostAsync("/__admin/mappings", content);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task A_templated_field_is_encrypted_on_the_way_out_and_decrypts_to_the_rendered_value()
    {
        await SeedAsync("""
        {"request":{"method":"POST","urlPath":"/approve"},
         "response":{"status":200,"transformers":["response-template"],
          "protect":{"scheme":"jwe-dir-a256gcm","fields":["encData"]},
          "body":"{\"status\":\"approved\",\"encData\":{\"echo\":\"{{jsonPath request.body '$.ref'}}\"}}"}}
        """);

        using var response = await _client.PostAsync("/approve",
            new StringContent("""{"ref":"REF-9931"}""", Encoding.UTF8, "application/json"));
        var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        // Protection runs AFTER templating: the encrypted field carries the rendered value.
        Assert.Equal("approved", envelope["status"]!.GetValue<string>());
        Assert.Equal("REF-9931", JsonNode.Parse(Decrypt(envelope["encData"]!.GetValue<string>()))!["echo"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_full_round_trip_works_encrypted_in_and_encrypted_out()
    {
        // The shape a bank integration actually has: the client encrypts what it sends and decrypts
        // what it receives, and the mock has to satisfy both halves at once.
        await SeedAsync("""
        {"request":{"method":"POST","urlPath":"/pay",
          "decrypt":{"scheme":"jwe-dir-a256gcm","fields":["encData"]},
          "bodyPatterns":[{"matchesJsonPath":{"expression":"$.encData.currency","equalTo":"SAR"}}]},
         "response":{"status":201,"transformers":["response-template"],
          "protect":{"scheme":"jwe-dir-a256gcm","fields":["encResult"]},
          "body":"{\"ok\":true,\"encResult\":{\"pan\":\"{{jsonPath request.body '$.encData.pan'}}\"}}"}}
        """);

        var request = $$"""{"encData":"{{Encrypt("""{"pan":"4111111111111111","currency":"SAR"}""")}}"}""";
        using var response = await _client.PostAsync("/pay", new StringContent(request, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("4111111111111111", body);

        var envelope = JsonNode.Parse(body)!;
        Assert.True(envelope["ok"]!.GetValue<bool>());
        Assert.Equal("4111111111111111", JsonNode.Parse(Decrypt(envelope["encResult"]!.GetValue<string>()))!["pan"]!.GetValue<string>());
    }

    [Fact]
    public async Task Whole_body_protection_produces_one_token()
    {
        await SeedAsync("""
        {"request":{"method":"GET","urlPath":"/whole"},
         "response":{"status":200,"protect":{"scheme":"jwe-dir-a256gcm"},"body":"{\"secret\":\"all-of-it\"}"}}
        """);

        var token = await _client.GetStringAsync("/whole");

        Assert.DoesNotContain("all-of-it", token);
        Assert.Equal("""{"secret":"all-of-it"}""", Decrypt(token));
    }

    [Fact]
    public async Task Stubs_that_declare_nothing_serve_plaintext_exactly_as_before()
    {
        await SeedAsync("""
        {"request":{"method":"GET","urlPath":"/plain"},"response":{"status":200,"body":"{\"a\":1}"}}
        """);

        Assert.Equal("""{"a":1}""", await _client.GetStringAsync("/plain"));
    }
}
