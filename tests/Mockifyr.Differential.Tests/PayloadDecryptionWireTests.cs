using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// End-to-end validation of field-level payload decryption (G20a, ADR 0012) against a real host.
/// No oracle exists — the reference engine has no payload cryptography — so this follows the
/// G18/G19 precedent: a client encrypts exactly as RFC 7516 §5.1 prescribes (an independent
/// implementation of the same spec), drives the wire, and the assertions prove the stub matched on
/// the DECRYPTED field and that templating rendered decrypted values.
/// </summary>
public sealed class PayloadDecryptionWireTests : IAsyncLifetime
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private Microsoft.AspNetCore.Builder.WebApplication? _host;
    private HttpClient _client = null!;

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What a partner's client library does before it sends the request.</summary>
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

    private async Task<HttpResponseMessage> PostAsync(string url, string body) =>
        await _client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task A_stub_matches_on_an_encrypted_field_and_templating_sees_the_plaintext()
    {
        // The stub asserts on data the client encrypted, and echoes it back — neither is expressible
        // without decryption, because the ciphertext differs on every request (random IV).
        await SeedAsync("""
        {"request":{"method":"POST","urlPath":"/pay",
          "decrypt":{"scheme":"jwe-dir-a256gcm","fields":["encData"]},
          "bodyPatterns":[{"matchesJsonPath":{"expression":"$.encData.currency","equalTo":"SAR"}}]},
         "response":{"status":201,"transformers":["response-template"],
          "body":"pan={{jsonPath request.body '$.encData.pan'}} amount={{jsonPath request.body '$.encData.amount'}}"}}
        """);

        var payload = $$"""{"merchant":"acme","encData":"{{Encrypt("""{"pan":"4111111111111111","amount":250,"currency":"SAR"}""")}}"}""";
        using var response = await PostAsync("/pay", payload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("pan=4111111111111111 amount=250", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_requests_with_different_ciphertext_both_match_the_same_stub()
    {
        await SeedAsync("""
        {"request":{"method":"POST","urlPath":"/repeat",
          "decrypt":{"scheme":"jwe-dir-a256gcm","fields":["encData"]},
          "bodyPatterns":[{"matchesJsonPath":"$.encData.pan"}]},
         "response":{"status":200,"body":"matched"}}
        """);

        // A correct client uses a fresh IV per request, so the bytes differ every time — the case
        // where binaryEqualTo cannot work at all.
        var first = $$"""{"encData":"{{Encrypt("""{"pan":"4111"}""")}}"}""";
        var second = $$"""{"encData":"{{Encrypt("""{"pan":"4111"}""")}}"}""";
        Assert.NotEqual(first, second);

        foreach (var payload in new[] { first, second })
        {
            using var response = await PostAsync("/repeat", payload);
            Assert.Equal("matched", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_payload_encrypted_for_another_key_does_not_match_and_the_journal_keeps_the_ciphertext()
    {
        await SeedAsync("""
        {"request":{"method":"POST","urlPath":"/strict",
          "decrypt":{"scheme":"jwe-dir-a256gcm","fields":["encData"]},
          "bodyPatterns":[{"matchesJsonPath":"$.encData.pan"}]},
         "response":{"status":200,"body":"matched"}}
        """);

        // Encrypted with a key this host does not have: it must not match, and must not throw.
        var foreignKey = RandomNumberGenerator.GetBytes(32);
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"dir","enc":"A256GCM"}"""));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var body = Encoding.UTF8.GetBytes("""{"pan":"4111"}""");
        var ciphertext = new byte[body.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(foreignKey, 16))
        {
            aes.Encrypt(nonce, body, ciphertext, tag, Encoding.ASCII.GetBytes(header));
        }

        var token = $"{header}..{Base64Url(nonce)}.{Base64Url(ciphertext)}.{Base64Url(tag)}";
        using var response = await PostAsync("/strict", $$"""{"encData":"{{token}}"}""");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The recorded request is what the client actually sent — decryption is a view, not a
        // rewrite. Replay, export and the differential harness all depend on this.
        using var journal = await _client.GetAsync("/__admin/requests");
        using var doc = System.Text.Json.JsonDocument.Parse(await journal.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("requests").EnumerateArray().First().GetProperty("id").GetString();
        using var detail = await _client.GetAsync($"/__admin/requests/{id}");
        var recorded = await detail.Content.ReadAsStringAsync();
        Assert.Contains(header, recorded);
        Assert.DoesNotContain("4111", recorded);
    }

    [Fact]
    public async Task Stubs_that_declare_nothing_are_untouched()
    {
        await SeedAsync("""
        {"request":{"method":"POST","urlPath":"/plain","bodyPatterns":[{"matchesJsonPath":"$.amount"}]},
         "response":{"status":200,"transformers":["response-template"],"body":"amount={{jsonPath request.body '$.amount'}}"}}
        """);

        using var response = await PostAsync("/plain", """{"amount":42}""");
        Assert.Equal("amount=42", await response.Content.ReadAsStringAsync());
    }
}
