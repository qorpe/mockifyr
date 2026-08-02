using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mockifyr.Core;

namespace Mockifyr.Crypto;

/// <summary>
/// Field-level decryption for JWE compact serialization with direct key agreement and A256GCM
/// (G20a, ADR 0012) — the shape card schemes and bank integrations use when only the sensitive
/// fields of an otherwise readable envelope are protected. Lives at the edge: it holds the key,
/// Core never does. No external dependency — <c>AesGcm</c> is BCL.
/// </summary>
public sealed class JweFieldDecryptor(IKeySource keys) : IPayloadDecryptor
{
    /// <summary>The single-key form an inline <c>--decrypt-key</c> produces.</summary>
    public JweFieldDecryptor(byte[] key) : this(new StaticKeySource(key))
    {
    }

    /// <summary>The scheme name a stub declares to select this decryptor.</summary>
    public const string SchemeName = "jwe-dir-a256gcm";

    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <inheritdoc />
    public bool Handles(string scheme) => string.Equals(scheme, SchemeName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public CanonicalRequest Decrypt(CanonicalRequest request, PayloadDecryptDirective directive)
    {
        if (request.Body.Length == 0)
        {
            return request;
        }

        // Whole-body inbound decryption (G20d): no field named means the entire body IS one JWE
        // token — the fixed-partner shape, and the mirror of what G20b produces on the way out.
        // This is the case a byte comparison cannot express at all: a correct sender uses a fresh
        // IV, so the bytes differ on every request even for identical plaintext.
        if (directive.Fields.Count == 0)
        {
            var whole = TryDecrypt(Encoding.UTF8.GetString(request.Body).Trim());
            return whole is null ? request : request with { Body = Encoding.UTF8.GetBytes(whole) };
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(request.Body);
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: nothing to address by field name. A stub declaring field-level decryption
            // simply does not match, which is the honest outcome — never an exception.
            return request;
        }

        if (root is not JsonObject envelope)
        {
            return request;
        }

        var changed = false;
        foreach (var field in directive.Fields)
        {
            if (envelope[field] is not JsonValue value ||
                !value.TryGetValue<string>(out var token) ||
                TryDecrypt(token) is not { } plaintext)
            {
                continue;
            }

            // A decrypted field is re-embedded as JSON when it IS JSON (the common case — an
            // encrypted sub-object), else as the plain string. Both then match and template
            // naturally: {{jsonPath request.body '$.card.pan'}} works either way.
            envelope[field] = ParseOrString(plaintext);
            changed = true;
        }

        return changed
            ? request with { Body = Encoding.UTF8.GetBytes(envelope.ToJsonString()) }
            : request;
    }

    private static JsonNode? ParseOrString(string plaintext)
    {
        try
        {
            return JsonNode.Parse(plaintext) ?? JsonValue.Create(plaintext);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(plaintext);
        }
    }

    /// <summary>
    /// Decrypts one JWE compact token, or null when it is malformed, was encrypted for another key,
    /// or fails its authentication tag. Every failure is a null — a tampered payload must read as
    /// "did not match", never as a 500.
    /// </summary>
    private string? TryDecrypt(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 5)
        {
            return null;
        }

        // Every active key is tried, newest first (#250). A token encrypted with the key that is on
        // its way out still reads during a rollover, which is what makes rotation an "add now,
        // remove later" operation rather than a coordinated restart. Trying a wrong key is not a
        // guess: AES-GCM's authentication tag fails, so only the key that actually encrypted the
        // token can produce plaintext.
        foreach (var candidate in keys.Current.Keys)
        {
            if (TryDecryptWith(candidate.Material, parts) is { } plaintext)
            {
                return plaintext;
            }
        }

        return null;
    }

    private static string? TryDecryptWith(byte[] key, string[] parts)
    {
        try
        {
            // dir: the second part (encrypted key) is empty — the key is shared, not wrapped.
            if (parts[1].Length != 0)
            {
                return null;
            }

            var nonce = Base64UrlDecode(parts[2]);
            var ciphertext = Base64UrlDecode(parts[3]);
            var tag = Base64UrlDecode(parts[4]);
            if (nonce.Length != NonceBytes || tag.Length != TagBytes)
            {
                return null;
            }

            // The protected header is the AAD, ASCII of its base64url form per RFC 7516 §5.1.
            var associatedData = Encoding.ASCII.GetBytes(parts[0]);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            // Wrong key, tampered ciphertext, non-base64url, or a length the cipher refuses. Every
            // one of these is attacker-reachable input, so all of them read as "did not decrypt" —
            // never as an exception escaping into a 500.
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => throw new FormatException("bad base64url") };
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Reads a 256-bit key from base64 or base64url, or null when the value is not one — kept here
    /// as the name the host already calls; the parsing itself lives with the key ring (#250) so
    /// there is one definition of what a key is.
    /// </summary>
    public static byte[]? ReadKey(string? configured) => KeyRing.ReadKey(configured);
}
