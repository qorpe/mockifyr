using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Mockifyr.Server;

namespace Mockifyr.Differential.Tests;

/// <summary>
/// End-to-end validation of signing (G20c, ADR 0012) against a real host: a client signs what it
/// sends and verifies what it receives, exactly as a PSD2-shaped integration does. No oracle exists,
/// so the signatures are computed independently here (G18/G19 precedent).
/// </summary>
public sealed class SigningWireTests : IAsyncLifetime
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private Microsoft.AspNetCore.Builder.WebApplication? _host;
    private HttpClient _client = null!;

    private static string Digest(string body) =>
        "SHA-256=" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static string Signature(string digest) =>
        Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(digest)));

    public async Task InitializeAsync()
    {
        _host = MockifyrHost.Build(["--port", "0", "--sign-key", Convert.ToBase64String(Key)]);
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

    private async Task<HttpResponseMessage> PostAsync(string url, string body, string? digest, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (digest is not null)
        {
            request.Headers.TryAddWithoutValidation("Digest", digest);
        }

        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-JWS-Signature", signature);
        }

        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task Only_a_correctly_signed_request_matches_a_stub_that_demands_a_signature()
    {
        await SeedAsync("""
        {"request":{"method":"POST","urlPath":"/psd2","signature":{"scheme":"hmac-sha256"}},
         "response":{"status":200,"body":"accepted"}}
        """);

        const string body = """{"amount":10}""";
        var digest = Digest(body);

        using var signed = await PostAsync("/psd2", body, digest, Signature(digest));
        Assert.Equal("accepted", await signed.Content.ReadAsStringAsync());

        // Unsigned, wrongly signed, and body-tampered requests all miss the stub — a 404, not a
        // match with a warning, because the stub's contract is "signed requests only".
        using var unsigned = await PostAsync("/psd2", body, digest, null);
        Assert.Equal(HttpStatusCode.NotFound, unsigned.StatusCode);

        using var wrong = await PostAsync("/psd2", body, digest, Signature(Digest("something else")));
        Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);

        using var tampered = await PostAsync("/psd2", """{"amount":9999}""", digest, Signature(digest));
        Assert.Equal(HttpStatusCode.NotFound, tampered.StatusCode);
    }

    [Fact]
    public async Task A_signed_response_verifies_on_the_client_side()
    {
        await SeedAsync("""
        {"request":{"method":"GET","urlPath":"/signed"},
         "response":{"status":200,"sign":{"scheme":"hmac-sha256"},"body":"{\"ok\":true}"}}
        """);

        using var response = await _client.GetAsync("/signed");
        var body = await response.Content.ReadAsStringAsync();

        // The client does what a partner's library does: recompute the digest, verify the HMAC.
        var digest = response.Headers.GetValues("Digest").Single();
        Assert.Equal(Digest(body), digest);
        Assert.Equal(Signature(digest), response.Headers.GetValues("X-JWS-Signature").Single());
    }

    [Fact]
    public async Task Custom_header_names_are_honored_on_both_sides()
    {
        await SeedAsync("""
        {"request":{"method":"POST","urlPath":"/custom",
          "signature":{"scheme":"hmac-sha256","header":"X-Signature","digestHeader":"Content-Digest"}},
         "response":{"status":200,"sign":{"scheme":"hmac-sha256","header":"X-Signature","digestHeader":"Content-Digest"},
          "body":"{\"ok\":true}"}}
        """);

        const string body = """{"amount":1}""";
        var digest = Digest(body);
        var request = new HttpRequestMessage(HttpMethod.Post, "/custom")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Content-Digest", digest);
        request.Headers.TryAddWithoutValidation("X-Signature", Signature(digest));

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var served = await response.Content.ReadAsStringAsync();
        Assert.Equal(Digest(served), response.Headers.GetValues("Content-Digest").Single());
        Assert.Equal(Signature(Digest(served)), response.Headers.GetValues("X-Signature").Single());
    }

    [Fact]
    public async Task Stubs_that_declare_nothing_are_untouched_by_signing()
    {
        await SeedAsync("""
        {"request":{"method":"GET","urlPath":"/plain"},"response":{"status":200,"body":"plain"}}
        """);

        using var response = await _client.GetAsync("/plain");
        Assert.Equal("plain", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Digest"));
        Assert.False(response.Headers.Contains("X-JWS-Signature"));
    }
}
