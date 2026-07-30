using System.Security.Cryptography;
using System.Text;
using Mockifyr.Core;
using Mockifyr.Crypto;

namespace Mockifyr.Application.Tests;

/// <summary>
/// Pure-logic coverage for field-level JWE decryption (G20a, ADR 0012). The encryptor here is what a
/// partner's client library does, so these are round-trips against an independent implementation of
/// the same RFC, not against the decryptor's own assumptions.
/// </summary>
public sealed class JweFieldDecryptorTests
{
    private static readonly byte[] Key = Convert.FromHexString(new string('a', 64));

    /// <summary>Produces a JWE compact token (dir + A256GCM) exactly as RFC 7516 §5.1 prescribes.</summary>
    private static string Encrypt(string plaintext, byte[]? key = null)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"dir","enc":"A256GCM"}"""));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var body = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[body.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key ?? Key, 16);
        aes.Encrypt(nonce, body, ciphertext, tag, Encoding.ASCII.GetBytes(header));
        return $"{header}..{Base64Url(nonce)}.{Base64Url(ciphertext)}.{Base64Url(tag)}";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static CanonicalRequest Request(string body) =>
        CanonicalRequestBuilder.Build("POST", "/pay", [], Encoding.UTF8.GetBytes(body), "https");

    private static string BodyOf(CanonicalRequest request) => Encoding.UTF8.GetString(request.Body);

    private static readonly PayloadDecryptDirective Directive = new(JweFieldDecryptor.SchemeName, ["encData"]);

    [Fact]
    public void An_encrypted_object_field_becomes_matchable_json()
    {
        var token = Encrypt("""{"pan":"4111111111111111","amount":10}""");
        var decryptor = new JweFieldDecryptor(Key);

        var view = decryptor.Decrypt(Request($$"""{"merchant":"acme","encData":"{{token}}"}"""), Directive);
        var body = BodyOf(view);

        // The envelope survives and the ciphertext is replaced by real JSON — so jsonPath works.
        Assert.Contains("\"merchant\":\"acme\"", body);
        Assert.Contains("4111111111111111", body);
        Assert.DoesNotContain(token, body);
    }

    [Fact]
    public void A_plain_string_payload_round_trips_as_a_string()
    {
        var decryptor = new JweFieldDecryptor(Key);
        var view = decryptor.Decrypt(Request($$"""{"encData":"{{Encrypt("hello world")}}"}"""), Directive);

        Assert.Contains("hello world", BodyOf(view));
    }

    [Fact]
    public void A_wrong_key_a_tampered_tag_and_a_malformed_token_all_leave_the_request_untouched()
    {
        var decryptor = new JweFieldDecryptor(Key);
        var otherKey = RandomNumberGenerator.GetBytes(32);

        foreach (var token in new[]
        {
            Encrypt("""{"pan":"4111"}""", otherKey),          // encrypted for someone else
            Encrypt("""{"pan":"4111"}""")[..^4] + "AAAA",      // tampered authentication tag
            "not.a.jwe",                                        // wrong shape
            "a.b.c.d.e",                                        // right shape, not base64url
        })
        {
            var original = Request($$"""{"encData":"{{token}}"}""");
            var view = decryptor.Decrypt(original, Directive);

            // Never an exception, never a partial rewrite: a payload that does not decrypt simply
            // does not match.
            Assert.Same(original.Body, view.Body);
        }
    }

    [Fact]
    public void Bodies_that_cannot_carry_the_field_are_returned_as_is()
    {
        var decryptor = new JweFieldDecryptor(Key);

        foreach (var body in new[] { "not json at all", "[1,2,3]", """{"other":"x"}""", "" })
        {
            var original = Request(body);
            Assert.Same(original.Body, decryptor.Decrypt(original, Directive).Body);
        }

        // An empty field list is a no-op too.
        var noFields = Request("""{"encData":"x"}""");
        Assert.Same(noFields.Body, decryptor.Decrypt(noFields, new PayloadDecryptDirective(JweFieldDecryptor.SchemeName, [])).Body);
    }

    [Fact]
    public void A_decrypted_json_payload_is_embedded_as_json_not_as_a_string()
    {
        var decryptor = new JweFieldDecryptor(Key);
        var view = decryptor.Decrypt(Request($$"""{"encData":"{{Encrypt("""{"pan":"4111"}""")}}"}"""), Directive);

        // Structure matters, not just the characters: a string-embedded payload would still contain
        // "4111" but jsonPath could never address $.encData.pan.
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(BodyOf(view))!;
        Assert.IsType<System.Text.Json.Nodes.JsonObject>(envelope["encData"]);
        Assert.Equal("4111", envelope["encData"]!["pan"]!.GetValue<string>());
    }

    [Fact]
    public void Tokens_that_are_not_five_parts_never_throw()
    {
        var decryptor = new JweFieldDecryptor(Key);

        // A single-part token must be rejected by the shape check — reading parts[1] without it
        // would be an unhandled IndexOutOfRangeException, i.e. a 500 for an attacker-chosen body.
        foreach (var token in new[] { "abc", "a.b", "a.b.c.d.e.f" })
        {
            var original = Request($$"""{"encData":"{{token}}"}""");
            Assert.Same(original.Body, decryptor.Decrypt(original, Directive).Body);
        }
    }

    [Fact]
    public void A_wrapped_key_token_is_refused_even_when_its_ciphertext_is_valid()
    {
        // Same valid ciphertext, but the encrypted-key part is present: that is alg != dir, a token
        // for a scheme this decryptor does not implement. Accepting it would silently decrypt a
        // payload whose key agreement was never verified.
        var parts = Encrypt("""{"pan":"4111"}""").Split('.');
        var wrapped = $"{parts[0]}.d29ybGQ.{parts[2]}.{parts[3]}.{parts[4]}";

        var original = Request($$"""{"encData":"{{wrapped}}"}""");
        Assert.Same(original.Body, new JweFieldDecryptor(Key).Decrypt(original, Directive).Body);
    }

    [Fact]
    public void A_wrong_length_nonce_or_tag_is_rejected_before_the_cipher_sees_it()
    {
        // AesGcm throws ArgumentException (not CryptographicException) on a bad nonce length, so the
        // length guard is what keeps a malformed token from becoming a 500.
        var parts = Encrypt("""{"pan":"4111"}""").Split('.');
        var shortNonce = $"{parts[0]}..{Base64Url(new byte[8])}.{parts[3]}.{parts[4]}";
        var shortTag = $"{parts[0]}..{parts[2]}.{parts[3]}.{Base64Url(new byte[8])}";

        var decryptor = new JweFieldDecryptor(Key);
        foreach (var token in new[] { shortNonce, shortTag })
        {
            var original = Request($$"""{"encData":"{{token}}"}""");
            Assert.Same(original.Body, decryptor.Decrypt(original, Directive).Body);
        }
    }

    [Fact]
    public void Only_its_own_scheme_is_handled()
    {
        var decryptor = new JweFieldDecryptor(Key);

        Assert.True(decryptor.Handles("jwe-dir-a256gcm"));
        Assert.True(decryptor.Handles("JWE-DIR-A256GCM"));
        Assert.False(decryptor.Handles("aes-cbc"));
    }

    [Fact]
    public void The_key_reader_accepts_base64_and_base64url_and_refuses_anything_else()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(key, JweFieldDecryptor.ReadKey(Convert.ToBase64String(key)));
        Assert.Equal(key, JweFieldDecryptor.ReadKey(Base64Url(key)));

        Assert.Null(JweFieldDecryptor.ReadKey(null));
        Assert.Null(JweFieldDecryptor.ReadKey(""));
        Assert.Null(JweFieldDecryptor.ReadKey("not-base64!!"));
        // A 128-bit key is refused rather than silently used — A256GCM needs 256 bits.
        Assert.Null(JweFieldDecryptor.ReadKey(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));
    }

    [Fact]
    public void The_view_picks_the_decryptor_that_handles_the_declared_scheme()
    {
        var view = new PayloadDecryptionView([new JweFieldDecryptor(Key)]);
        var request = Request($$"""{"encData":"{{Encrypt("""{"pan":"4111"}""")}}"}""");

        // Declared and handled → decrypted view.
        Assert.Contains("4111", BodyOf(view.For(request, Directive)));
        // Declared but unknown scheme → untouched, no exception.
        Assert.Same(request, view.For(request, new PayloadDecryptDirective("something-else", ["encData"])));
        // Not declared → the very same request, the default path.
        Assert.Same(request, view.For(request, null));
        // No decryptor registered at all → likewise.
        Assert.True(new PayloadDecryptionView([]).IsEmpty);
        Assert.Same(request, new PayloadDecryptionView([]).For(request, Directive));
    }
}
