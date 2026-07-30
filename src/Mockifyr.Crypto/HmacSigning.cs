using System.Security.Cryptography;
using System.Text;
using Mockifyr.Core;

namespace Mockifyr.Crypto;

/// <summary>
/// Shared HMAC-SHA256 signing conventions (G20c, ADR 0012), in the PSD2 / Berlin Group shape without
/// its full signing-string ceremony: a <c>Digest</c> header carries <c>SHA-256=&lt;base64&gt;</c> of
/// the body, and the signature header carries the base64 HMAC of that digest value. Signing the
/// digest rather than the body is what makes the scheme composable — the digest is a stable,
/// header-sized commitment to bytes that may be encrypted, chunked or streamed.
/// </summary>
internal static class HmacConventions
{
    public const string Scheme = "hmac-sha256";

    /// <summary>The <c>Digest</c> header value for a body: <c>SHA-256=&lt;base64 of SHA-256&gt;</c>.</summary>
    public static string Digest(byte[] body) => "SHA-256=" + Convert.ToBase64String(SHA256.HashData(body));

    /// <summary>The signature over a digest header value.</summary>
    public static string Sign(byte[] key, string digestValue) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(digestValue)));
}

/// <summary>
/// Verifies an HMAC-SHA256 request signature (G20c): the declared digest header must match the body
/// actually received, and the signature header must be the HMAC of that digest value. Both halves
/// are required — checking only the signature would accept a valid signature over someone else's
/// digest, and checking only the digest would accept an unsigned request.
/// </summary>
public sealed class HmacSignatureVerifier(byte[] key) : ISignatureVerifier
{
    /// <summary>The scheme name a stub declares to select this verifier.</summary>
    public const string SchemeName = HmacConventions.Scheme;

    /// <inheritdoc />
    public bool Handles(string scheme) => string.Equals(scheme, SchemeName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool Verify(CanonicalRequest request, SignatureRequirement requirement)
    {
        var digest = Single(request, requirement.DigestHeader);
        var signature = Single(request, requirement.Header);
        if (digest is null || signature is null)
        {
            return false;
        }

        // The digest must describe the body we actually received: a signature over a stale or
        // attacker-chosen digest must not pass.
        if (!FixedTimeEquals(digest, HmacConventions.Digest(request.Body)))
        {
            return false;
        }

        return FixedTimeEquals(signature, HmacConventions.Sign(key, digest));
    }

    private static string? Single(CanonicalRequest request, string header) =>
        request.Headers[header] is { } values && values.Any() ? values.First() : null;

    /// <summary>Constant-time comparison of two ASCII-safe values of possibly different length.</summary>
    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}

/// <summary>
/// Signs a served response (G20c): adds the digest of the body that goes on the wire and the HMAC of
/// that digest, so a client that verifies what it receives is satisfied by the mock.
/// </summary>
public sealed class HmacResponseSigner(byte[] key) : IResponseSigner
{
    /// <summary>The scheme name a stub declares to select this signer.</summary>
    public const string SchemeName = HmacConventions.Scheme;

    /// <inheritdoc />
    public bool Handles(string scheme) => string.Equals(scheme, SchemeName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public CanonicalResponse Sign(CanonicalResponse response, ResponseSignature signature)
    {
        var digest = HmacConventions.Digest(response.Body);
        var pairs = response.Headers
            .SelectMany(group => group.Select(value => new KeyValuePair<string, string>(group.Key, value)))
            // A stub that hardcoded either header loses to the computed one — a stale digest is
            // worse than none, because a verifying client would reject the response outright.
            .Where(pair => !Matches(pair.Key, signature.DigestHeader) && !Matches(pair.Key, signature.Header))
            .Append(new KeyValuePair<string, string>(signature.DigestHeader, digest))
            .Append(new KeyValuePair<string, string>(signature.Header, HmacConventions.Sign(key, digest)));

        return response with
        {
            Headers = pairs.ToLookup(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static bool Matches(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
