using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mockifyr.Core;

namespace Mockifyr.Crypto;

/// <summary>
/// Encrypts a rendered response with JWE compact serialization, direct key agreement and A256GCM
/// (G20b, ADR 0012) — the same scheme <see cref="JweFieldDecryptor"/> reads, so a partner's client
/// library round-trips against this mock without special-casing anything. Two shapes, chosen by the
/// stub: named fields (readable envelope, the common case) or the whole body as one token.
/// </summary>
public sealed class JweResponseProtector(byte[] key) : IPayloadProtector
{
    /// <summary>The scheme name a stub declares to select this protector.</summary>
    public const string SchemeName = JweFieldDecryptor.SchemeName;

    private const string ProtectedHeader = """{"alg":"dir","enc":"A256GCM"}""";

    /// <inheritdoc />
    public bool Handles(string scheme) => string.Equals(scheme, SchemeName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public CanonicalResponse Protect(CanonicalResponse response, PayloadProtectDirective directive)
    {
        if (response.Body.Length == 0)
        {
            return response;
        }

        // No fields named → the whole body becomes one token (the fixed-partner shape).
        if (directive.Fields.Count == 0)
        {
            return response with { Body = Encoding.UTF8.GetBytes(Encrypt(Encoding.UTF8.GetString(response.Body))) };
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(response.Body);
        }
        catch (System.Text.Json.JsonException)
        {
            // Field-level protection was asked for on a body that has no fields. Serving it as
            // rendered makes the misconfiguration visible immediately; silently shipping it as a
            // whole-body token would look like it worked.
            return response;
        }

        if (root is not JsonObject envelope)
        {
            return response;
        }

        var changed = false;
        foreach (var field in directive.Fields)
        {
            if (envelope[field] is not { } value)
            {
                continue;
            }

            // A field that is an object/array is encrypted as its JSON text; a scalar as its raw
            // value — which is exactly what the decryption side expects to find on the way back.
            var plaintext = value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
                ? text
                : value.ToJsonString();
            envelope[field] = Encrypt(plaintext);
            changed = true;
        }

        return changed
            ? response with { Body = Encoding.UTF8.GetBytes(envelope.ToJsonString()) }
            : response;
    }

    /// <summary>Builds one JWE compact token per RFC 7516 §5.1, with a fresh nonce every time.</summary>
    private string Encrypt(string plaintext)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes(ProtectedHeader));
        // A fresh 96-bit nonce per token: reusing one under the same key would destroy the
        // confidentiality guarantee of GCM entirely.
        var nonce = RandomNumberGenerator.GetBytes(12);
        var body = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[body.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, body, ciphertext, tag, Encoding.ASCII.GetBytes(header));
        return $"{header}..{Base64Url(nonce)}.{Base64Url(ciphertext)}.{Base64Url(tag)}";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
