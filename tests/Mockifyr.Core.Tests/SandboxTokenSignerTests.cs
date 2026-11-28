using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mockifyr.Core;

namespace Mockifyr.Core.Tests;

/// <summary>
/// The sandbox access token (#450, ADR 0016): what is signed, and every way a token this host did not
/// issue, or no longer honours, is refused. No oracle exists for a simulated grant, so the checks are
/// against RFC 7515/7519/7638 and an independent verifier built from the published key set.
/// </summary>
public sealed class SandboxTokenSignerTests
{
    private static readonly RSA Key = RSA.Create(2048);
    private static readonly SandboxTokenSigner Signer = new(Key);
    private static readonly SandboxOAuthOptions Options = SandboxOAuthOptions.Default;
    private static readonly DateTimeOffset Now = new(2027, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ApiKey Acme = new("key-1", new TenantId("acme"), "ci", "s", "h", "mfk_abcd", Now, null);

    private static SandboxTokenValidation Validate(string token, DateTimeOffset at) =>
        Signer.Validate(token, _ => at, Options);

    [Fact]
    public void A_minted_token_names_its_key_and_tenant_and_validates()
    {
        var token = Signer.Mint(Acme, "payments:read", Now, Options);

        var verdict = Validate(token, Now);

        Assert.Equal(SandboxTokenStatus.Valid, verdict.Status);
        Assert.Equal("key-1", verdict.KeyId);
        Assert.Equal(new TenantId("acme"), verdict.Tenant);

        var payload = Decode(token.Split('.')[1]);
        Assert.Equal("mockifyr-sandbox", payload.GetProperty("iss").GetString());
        Assert.Equal("key-1", payload.GetProperty("client_id").GetString());
        Assert.Equal("acme", payload.GetProperty("tenant").GetString());
        Assert.Equal("payments:read", payload.GetProperty("scope").GetString());
        Assert.Equal(Now.ToUnixTimeSeconds(), payload.GetProperty("iat").GetInt64());
        Assert.Equal(Now.AddHours(1).ToUnixTimeSeconds(), payload.GetProperty("exp").GetInt64());

        var header = Decode(token.Split('.')[0]);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("at+jwt", header.GetProperty("typ").GetString());
        Assert.Equal(Signer.Kid, header.GetProperty("kid").GetString());
    }

    [Fact]
    public void A_token_without_a_requested_scope_carries_no_scope_claim()
    {
        var payload = Decode(Signer.Mint(Acme, null, Now, Options).Split('.')[1]);
        Assert.False(payload.TryGetProperty("scope", out _));
    }

    [Fact]
    public void Expiry_is_exact_at_exp_on_the_tenants_clock()
    {
        var token = Signer.Mint(Acme, null, Now, Options);

        Assert.Equal(SandboxTokenStatus.Valid, Validate(token, Now.AddSeconds(3599)).Status);
        Assert.Equal(SandboxTokenStatus.Expired, Validate(token, Now.AddSeconds(3600)).Status);
        Assert.Equal(SandboxTokenStatus.Expired, Validate(token, Now.AddDays(2)).Status);
    }

    [Fact]
    public void The_clock_consulted_is_the_tenant_the_token_names()
    {
        var token = Signer.Mint(Acme, null, Now, Options);
        TenantId? asked = null;

        Signer.Validate(token, t => { asked = t; return Now; }, Options);

        Assert.Equal(new TenantId("acme"), asked);
    }

    [Fact]
    public void A_changed_claim_breaks_the_signature()
    {
        var parts = Signer.Mint(Acme, null, Now, Options).Split('.');
        var forged = Encode(Decode(parts[1]).GetRawText().Replace("\"acme\"", "\"globex\"", StringComparison.Ordinal));

        Assert.Equal(SandboxTokenStatus.Invalid, Validate($"{parts[0]}.{forged}.{parts[2]}", Now).Status);
    }

    [Fact]
    public void A_token_signed_by_another_key_is_refused_even_with_this_keys_kid()
    {
        using var other = RSA.Create(2048);
        var token = SignRaw(other, $$"""{"alg":"RS256","typ":"at+jwt","kid":"{{Signer.Kid}}"}""", ValidPayload());

        var verdict = Validate(token, Now);

        Assert.Equal(SandboxTokenStatus.Invalid, verdict.Status);
        Assert.Null(verdict.KeyId);
        Assert.Null(verdict.Tenant);
    }

    [Fact]
    public void A_second_signer_does_not_accept_the_firsts_tokens()
    {
        var token = Signer.Mint(Acme, null, Now, Options);
        Assert.Equal(SandboxTokenStatus.Invalid, SandboxTokenSigner.Generate().Validate(token, _ => Now, Options).Status);
    }

    // The class: a header that changes what the token means or how it is verified. A fixed allow-list
    // (alg, typ, kid) covers the members nobody has named yet.
    [Theory]
    [InlineData("""{"alg":"none","kid":"KID"}""")]
    [InlineData("""{"alg":"HS256","kid":"KID"}""")]
    [InlineData("""{"alg":"RS512","kid":"KID"}""")]
    [InlineData("""{"alg":"rs256","kid":"KID"}""")]
    [InlineData("""{"kid":"KID"}""")]
    [InlineData("""{"alg":"RS256"}""")]
    [InlineData("""{"alg":"RS256","kid":"someone-else"}""")]
    [InlineData("""{"alg":"RS256","kid":"KID","crit":["exp"]}""")]
    [InlineData("""{"alg":"RS256","kid":"KID","jku":"https://example.invalid/keys"}""")]
    [InlineData("""{"alg":"RS256","kid":"KID","jwk":{"kty":"RSA"}}""")]
    [InlineData("""{"alg":"RS256","kid":"KID","x5u":"https://example.invalid/cert"}""")]
    [InlineData("""{"alg":"RS256","kid":"KID","alg":"RS256"}""")]
    [InlineData("""["alg","RS256"]""")]
    [InlineData("""not json""")]
    public void A_header_outside_the_allow_list_is_refused_even_when_correctly_signed(string header)
    {
        var token = SignRaw(Key, header.Replace("KID", Signer.Kid, StringComparison.Ordinal), ValidPayload());
        Assert.Equal(SandboxTokenStatus.Invalid, Validate(token, Now).Status);
    }

    [Fact]
    public void An_hmac_signed_with_the_public_key_is_refused()
    {
        // The classic confusion: a verifier that trusts the header's alg checks an HMAC keyed with the
        // public key it publishes.
        var header = Encode($$"""{"alg":"HS256","typ":"at+jwt","kid":"{{Signer.Kid}}"}""");
        var payload = Encode(ValidPayload());
        var publicKey = Encoding.UTF8.GetBytes(Signer.Jwks());
        var mac = HMACSHA256.HashData(publicKey, Encoding.ASCII.GetBytes(header + "." + payload));

        Assert.Equal(SandboxTokenStatus.Invalid, Validate($"{header}.{payload}.{B64(mac)}", Now).Status);
    }

    // The class: claims a reader could take two ways, or that are missing. Signed correctly each time,
    // so only the claim rule can refuse them.
    [Theory]
    [InlineData("""{"iss":"someone-else","client_id":"key-1","tenant":"acme","exp":EXP}""")]
    [InlineData("""{"client_id":"key-1","tenant":"acme","exp":EXP}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","tenant":"acme","exp":EXP}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":"","tenant":"acme","exp":EXP}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":"key-1","exp":EXP}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":"key-1","tenant":"","exp":EXP}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":"key-1","tenant":"acme"}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":"key-1","tenant":"acme","exp":"EXP"}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":"key-1","tenant":"acme","exp":EXP.5}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":1,"tenant":"acme","exp":EXP}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":"key-1","tenant":["acme"],"exp":EXP}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":"key-1","tenant":"acme","tenant":"globex","exp":EXP}""")]
    [InlineData("""{"iss":"mockifyr-sandbox","client_id":"key-1","tenant":"acme","exp":EXP,"exp":99999999999}""")]
    [InlineData("""[1,2,3]""")]
    public void A_payload_with_a_missing_wrong_or_ambiguous_claim_is_refused(string payload)
    {
        var token = SignRaw(Key, Header(), payload.Replace("EXP", Now.AddHours(1).ToUnixTimeSeconds().ToString(), StringComparison.Ordinal));
        Assert.Equal(SandboxTokenStatus.Invalid, Validate(token, Now).Status);
    }

    [Fact]
    public void A_correctly_signed_hand_made_payload_is_accepted_which_is_what_makes_the_refusals_above_mean_something()
    {
        Assert.Equal(SandboxTokenStatus.Valid, Validate(SignRaw(Key, Header(), ValidPayload()), Now).Status);
    }

    // The class: anything that is not exactly three non-empty base64url segments under the length cap.
    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("..")]
    [InlineData("eyJh..c2ln")]
    [InlineData("eyJh.eyJh.")]
    [InlineData("eyJh=.eyJh.c2ln")]
    [InlineData("eyJh+.eyJh.c2ln")]
    [InlineData("eyJh/.eyJh.c2ln")]
    [InlineData("eyJh .eyJh.c2ln")]
    [InlineData("e.eyJh.c2ln")]
    [InlineData("eyJİ.eyJh.c2ln")]
    public void A_malformed_token_is_refused_without_throwing(string token)
    {
        Assert.Equal(SandboxTokenStatus.Invalid, Validate(token, Now).Status);
    }

    [Fact]
    public void A_valid_token_with_whitespace_around_a_segment_is_refused()
    {
        var parts = Signer.Mint(Acme, null, Now, Options).Split('.');
        Assert.Equal(SandboxTokenStatus.Invalid, Validate($"{parts[0]}.{parts[1]} .{parts[2]}", Now).Status);
    }

    [Fact]
    public void A_token_in_standard_base64_rather_than_base64url_is_refused()
    {
        // Convert.FromBase64String accepts '+', '/', '=' and whitespace, so without the allow-list a
        // token re-encoded in standard base64 would verify. Re-encoding until a '+' or '/' appears keeps
        // the case meaningful whatever bytes this run's key produced.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var parts = Signer.Mint(Acme, null, Now, Options).Split('.');
            var standard = Convert.ToBase64String(FromB64(parts[2]));
            if (!standard.Contains('+', StringComparison.Ordinal) && !standard.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Equal(SandboxTokenStatus.Invalid, Validate($"{parts[0]}.{parts[1]}.{standard}", Now).Status);
            Assert.Equal(SandboxTokenStatus.Invalid, Validate($"{parts[0]}.{parts[1]}.{standard.TrimEnd('=')}", Now).Status);
            Assert.Equal(SandboxTokenStatus.Invalid, Validate($"{parts[0]}.{parts[1]}.{parts[2]}==", Now).Status);
            Assert.Equal(SandboxTokenStatus.Invalid, Validate($"{parts[0]}.{parts[1]}.{parts[2][..4]} {parts[2][4..]}", Now).Status);
            return;
        }

        Assert.Fail("No signature with a '+' or '/' in fifty tries.");
    }

    [Fact]
    public void A_token_exactly_at_the_length_cap_is_read()
    {
        // Pad the payload until the whole token is exactly MaxTokenLength long, so the cap's boundary is
        // proven rather than assumed. base64url cannot reach every length, so a few sizes are tried.
        for (var pad = 2400; pad < 2900; pad++)
        {
            var token = SignRaw(Key, Header(), ValidPayload().Replace("}", ",\"pad\":\"" + new string('x', pad) + "\"}", StringComparison.Ordinal));
            if (token.Length == SandboxTokenSigner.MaxTokenLength)
            {
                Assert.Equal(SandboxTokenStatus.Valid, Validate(token, Now).Status);
                return;
            }
        }

        Assert.Fail("No padding produced a token of exactly the cap's length.");
    }

    [Fact]
    public void The_subject_audience_and_token_id_are_set_and_the_id_is_fresh_each_time()
    {
        var first = Decode(Signer.Mint(Acme, null, Now, Options).Split('.')[1]);
        var second = Decode(Signer.Mint(Acme, null, Now, Options).Split('.')[1]);

        Assert.Equal("key-1", first.GetProperty("sub").GetString());
        Assert.Equal("mockifyr-sandbox", first.GetProperty("aud").GetString());
        Assert.Matches("^[0-9a-f]{32}$", first.GetProperty("jti").GetString()!);
        Assert.NotEqual(first.GetProperty("jti").GetString(), second.GetProperty("jti").GetString());
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new SandboxTokenSigner(null!));
        Assert.Throws<ArgumentNullException>(() => Signer.Mint(null!, null, Now, Options));
        Assert.Throws<ArgumentNullException>(() => Signer.Mint(Acme, null, Now, null!));
        Assert.Throws<ArgumentNullException>(() => Signer.Validate("a.b.c", null!, Options));
        Assert.Throws<ArgumentNullException>(() => Signer.Validate("a.b.c", _ => Now, null!));
    }

    [Fact]
    public void A_token_over_the_length_cap_is_refused_before_it_is_read()
    {
        var token = SignRaw(Key, Header(), ValidPayload().Replace("}", ",\"pad\":\"" + new string('x', 4000) + "\"}", StringComparison.Ordinal));
        Assert.True(token.Length > SandboxTokenSigner.MaxTokenLength);
        Assert.Equal(SandboxTokenStatus.Invalid, Validate(token, Now).Status);
    }

    [Fact]
    public void The_published_key_set_verifies_a_token_independently()
    {
        var token = Signer.Mint(Acme, null, Now, Options);
        using var jwks = JsonDocument.Parse(Signer.Jwks());
        var jwk = Assert.Single(jwks.RootElement.GetProperty("keys").EnumerateArray());
        Assert.Equal("RSA", jwk.GetProperty("kty").GetString());
        Assert.Equal("sig", jwk.GetProperty("use").GetString());
        Assert.Equal("RS256", jwk.GetProperty("alg").GetString());
        Assert.Equal(Signer.Kid, jwk.GetProperty("kid").GetString());
        Assert.False(jwk.TryGetProperty("d", out _));

        using var verifier = RSA.Create(new RSAParameters
        {
            Modulus = FromB64(jwk.GetProperty("n").GetString()!),
            Exponent = FromB64(jwk.GetProperty("e").GetString()!),
        });
        var parts = token.Split('.');
        Assert.True(verifier.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromB64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void The_kid_is_the_rfc7638_thumbprint_so_one_key_file_gives_one_kid_everywhere()
    {
        var parameters = Key.ExportParameters(includePrivateParameters: true);
        using var copy = RSA.Create(parameters);

        Assert.Equal(Signer.Kid, new SandboxTokenSigner(copy).Kid);

        var expected = B64(SHA256.HashData(Encoding.UTF8.GetBytes(
            $$"""{"e":"{{B64(parameters.Exponent!)}}","kty":"RSA","n":"{{B64(parameters.Modulus!)}}"}""")));
        Assert.Equal(expected, Signer.Kid);
    }

    [Fact]
    public void A_key_shorter_than_2048_bits_is_refused()
    {
        using var weak = RSA.Create(1024);
        var refused = Assert.Throws<ArgumentException>(() => new SandboxTokenSigner(weak));
        Assert.Contains("1024 bits; at least 2048", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eyJh.eyJh.c2ln", true)]
    [InlineData("mfk_abcdefghijklmnopqrstuvwxyz0123456789-_", false)]
    public void A_dot_is_what_tells_a_token_from_a_key(string presented, bool expected)
    {
        Assert.Equal(expected, SandboxTokenSigner.LooksLikeToken(presented));
    }

    [Theory]
    [InlineData("/oauth/token", true)]
    [InlineData("/connect/token", true)]
    [InlineData("/v1/oauth2/token.json", true)]
    [InlineData("/a", true)]
    [InlineData("/z", true)]
    [InlineData("/A", true)]
    [InlineData("/Z", true)]
    [InlineData("/0", true)]
    [InlineData("/9", true)]
    [InlineData("/-_.", true)]
    [InlineData("/`", false)]
    [InlineData("/{", false)]
    [InlineData("/@", false)]
    [InlineData("/[", false)]
    [InlineData("//", false)]
    [InlineData("/:", false)]
    [InlineData("oauth/token", false)]
    [InlineData("/", false)]
    [InlineData("/oauth/token/", false)]
    [InlineData("/oauth//token", false)]
    [InlineData("/oauth/token?x=1", false)]
    [InlineData("/oauth/to%6Ben", false)]
    [InlineData("/oauth/tök", false)]
    [InlineData("/oauth/İ", false)]
    [InlineData("/oauth/{id}", false)]
    [InlineData("/oauth token", false)]
    [InlineData("/__admin/token", false)]
    [InlineData("/__ADMIN/token", false)]
    [InlineData("/__admin", false)]
    [InlineData("/__sandbox/token", false)]
    [InlineData("/__mockifyr/token", false)]
    [InlineData("/.well-known/openid-configuration", false)]
    [InlineData("/__administrator", true)]
    public void A_token_path_must_be_plain_and_outside_the_reserved_prefixes(string path, bool expected)
    {
        Assert.Equal(expected, SandboxOAuthOptions.IsUsableTokenPath(path));
    }

    [Fact]
    public void A_token_path_longer_than_128_characters_is_refused()
    {
        Assert.True(SandboxOAuthOptions.IsUsableTokenPath("/" + new string('a', 127)));
        Assert.False(SandboxOAuthOptions.IsUsableTokenPath("/" + new string('a', 128)));
    }

    [Theory]
    [InlineData("read", true)]
    [InlineData("payments:read payments:write", true)]
    [InlineData("a!#[]~", true)]
    [InlineData("", false)]
    [InlineData(" read", false)]
    [InlineData("read ", false)]
    [InlineData("read  write", false)]
    [InlineData("re\"ad", false)]
    [InlineData("re\\ad", false)]
    [InlineData("read\twrite", false)]
    [InlineData("read\nwrite", false)]
    [InlineData("lesen:ü", false)]
    [InlineData("ı", false)]
    public void A_scope_is_rfc6749_scope_tokens_separated_by_one_space(string scope, bool expected)
    {
        Assert.Equal(expected, SandboxOAuthOptions.IsWellFormedScope(scope));
    }

    [Fact]
    public void A_scope_longer_than_1024_characters_is_refused()
    {
        Assert.True(SandboxOAuthOptions.IsWellFormedScope(new string('a', 1024)));
        Assert.False(SandboxOAuthOptions.IsWellFormedScope(new string('a', 1025)));
    }

    private static string Header() => $$"""{"alg":"RS256","typ":"at+jwt","kid":"{{Signer.Kid}}"}""";

    private static string ValidPayload() =>
        $$"""{"iss":"mockifyr-sandbox","client_id":"key-1","tenant":"acme","exp":{{Now.AddHours(1).ToUnixTimeSeconds()}}}""";

    private static string SignRaw(RSA key, string header, string payload)
    {
        var input = Encode(header) + "." + Encode(payload);
        return input + "." + B64(key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static string Encode(string json) => B64(Encoding.UTF8.GetBytes(json));

    private static JsonElement Decode(string segment) => JsonDocument.Parse(FromB64(segment)).RootElement.Clone();

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }
}
