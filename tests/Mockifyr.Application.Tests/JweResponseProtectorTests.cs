using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mockifyr.Core;
using Mockifyr.Crypto;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for response protection (G20b, ADR 0012). Every assertion decrypts with the
/// paired decryptor, so these are round-trips through the RFC rather than checks against the
/// protector's own idea of correctness.
/// </summary>
public sealed class JweResponseProtectorTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static CanonicalResponse Response(string body) => new()
    {
        Status = 200,
        Headers = Array.Empty<KeyValuePair<string, string>>().ToLookup(x => x.Key, x => x.Value),
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static string BodyOf(CanonicalResponse response) => Encoding.UTF8.GetString(response.Body);

    /// <summary>Reads a token back the way a partner's client library would.</summary>
    private static string Decrypt(string token)
    {
        var parts = token.Split('.');
        static byte[] B64(string v)
        {
            var p = v.Replace('-', '+').Replace('_', '/');
            p += (p.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            return Convert.FromBase64String(p);
        }

        var nonce = B64(parts[2]);
        var ciphertext = B64(parts[3]);
        var tag = B64(parts[4]);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.ASCII.GetBytes(parts[0]));
        return Encoding.UTF8.GetString(plaintext);
    }

    [Fact]
    public void Named_fields_are_encrypted_and_the_envelope_stays_readable()
    {
        var protector = new JweResponseProtector(Key);
        var directive = new PayloadProtectDirective(JweResponseProtector.SchemeName, ["encData"]);

        var result = protector.Protect(
            Response("""{"status":"approved","encData":{"pan":"4111111111111111","amount":250}}"""), directive);
        var envelope = JsonNode.Parse(BodyOf(result))!;

        // The envelope survives — that is the whole point of field-level protection.
        Assert.Equal("approved", envelope["status"]!.GetValue<string>());
        Assert.DoesNotContain("4111111111111111", BodyOf(result));

        // …and the field decrypts back to exactly what was rendered.
        var plaintext = JsonNode.Parse(Decrypt(envelope["encData"]!.GetValue<string>()))!;
        Assert.Equal("4111111111111111", plaintext["pan"]!.GetValue<string>());
        Assert.Equal(250, plaintext["amount"]!.GetValue<int>());
    }

    [Fact]
    public void A_scalar_field_round_trips_as_its_raw_value()
    {
        var protector = new JweResponseProtector(Key);
        var result = protector.Protect(
            Response("""{"token":"secret-value"}"""),
            new PayloadProtectDirective(JweResponseProtector.SchemeName, ["token"]));

        var envelope = JsonNode.Parse(BodyOf(result))!;
        Assert.Equal("secret-value", Decrypt(envelope["token"]!.GetValue<string>()));
    }

    [Fact]
    public void No_fields_named_means_the_whole_body_becomes_one_token()
    {
        var protector = new JweResponseProtector(Key);
        var result = protector.Protect(
            Response("""{"status":"approved","amount":10}"""),
            new PayloadProtectDirective(JweResponseProtector.SchemeName, []));

        var token = BodyOf(result);
        Assert.DoesNotContain("approved", token);
        Assert.Equal("""{"status":"approved","amount":10}""", Decrypt(token));
    }

    [Fact]
    public void Every_token_carries_a_fresh_nonce()
    {
        var protector = new JweResponseProtector(Key);
        var directive = new PayloadProtectDirective(JweResponseProtector.SchemeName, []);

        var first = BodyOf(protector.Protect(Response("""{"a":1}"""), directive));
        var second = BodyOf(protector.Protect(Response("""{"a":1}"""), directive));

        // Reusing a nonce under one key would void GCM's confidentiality guarantee entirely.
        Assert.NotEqual(first, second);
        Assert.Equal(Decrypt(first), Decrypt(second));
    }

    [Fact]
    public void Bodies_that_cannot_carry_named_fields_are_served_as_rendered()
    {
        var protector = new JweResponseProtector(Key);
        var directive = new PayloadProtectDirective(JweResponseProtector.SchemeName, ["encData"]);

        foreach (var body in new[] { "not json", "[1,2,3]", """{"other":"x"}""" })
        {
            var original = Response(body);
            // Visible degradation beats a silent whole-body fallback that only looks like it worked.
            Assert.Same(original.Body, protector.Protect(original, directive).Body);
        }

        var empty = Response(string.Empty);
        Assert.Same(empty.Body, protector.Protect(empty, directive).Body);
    }

    [Fact]
    public void Only_its_own_scheme_is_handled_and_the_applier_respects_that()
    {
        var protector = new JweResponseProtector(Key);
        Assert.True(protector.Handles("jwe-dir-a256gcm"));
        Assert.True(protector.Handles("JWE-DIR-A256GCM"));
        Assert.False(protector.Handles("pkcs7"));

        var applier = new PayloadProtectionApplier([protector]);
        var response = Response("""{"a":1}""");

        // Declared and handled → protected; unknown scheme or nothing declared → as rendered.
        Assert.NotSame(response.Body, applier.For(response, new PayloadProtectDirective("jwe-dir-a256gcm", [])).Body);
        Assert.Same(response, applier.For(response, new PayloadProtectDirective("pkcs7", [])));
        Assert.Same(response, applier.For(response, null));

        var none = new PayloadProtectionApplier([]);
        Assert.True(none.IsEmpty);
        Assert.Same(response, none.For(response, new PayloadProtectDirective("jwe-dir-a256gcm", [])));
    }
}
