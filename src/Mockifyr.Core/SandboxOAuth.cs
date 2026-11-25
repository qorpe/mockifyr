using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mockifyr.Core;

/// <summary>
/// The simulated client-credentials grant for sandbox tenants (#450, ADR 0016): what the token
/// endpoint is called, how long a token lives and what it names as its issuer.
/// </summary>
/// <remarks>
/// Off unless <c>--sandbox-oauth</c> is given beside <c>--sandbox-auth</c>. It is not an
/// authorization server: one grant, the client is an existing sandbox key, and the token is only ever
/// accepted by this host.
/// </remarks>
/// <param name="TokenPath">Where the token endpoint answers.</param>
/// <param name="Lifetime">How long an issued token is valid, on the tenant's clock.</param>
/// <param name="Issuer">The <c>iss</c> claim a token carries and must carry back.</param>
public sealed record SandboxOAuthOptions(string TokenPath, TimeSpan Lifetime, string Issuer)
{
    /// <summary>The token endpoint's path when none is configured.</summary>
    public const string DefaultTokenPath = "/oauth/token";

    /// <summary>Where the signing key set is published. Fixed: verifiers look for it there.</summary>
    public const string JwksPath = "/.well-known/jwks.json";

    /// <summary>The issuer when none is configured.</summary>
    public const string DefaultIssuer = "mockifyr-sandbox";

    /// <summary>One hour, the lifetime most client-credentials APIs issue.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    /// <summary>The longest lifetime accepted: a day. Beyond it a token stops exercising refresh code.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(1);

    /// <summary>An unconfigured grant.</summary>
    public static SandboxOAuthOptions Default { get; } = new(DefaultTokenPath, DefaultLifetime, DefaultIssuer);

    /// <summary>
    /// Whether <paramref name="path"/> can be the token endpoint: an absolute path of plain URL
    /// characters, and none of the paths this host already reserves.
    /// </summary>
    /// <remarks>
    /// An allow-list of characters rather than a list of bad ones: a path with a query, a fragment, a
    /// percent escape or a space would be matched by routing in a way an operator would not predict,
    /// and the host would start with an endpoint nobody can reach.
    /// </remarks>
    public static bool IsUsableTokenPath(string path)
    {
        if (path.Length is < 2 or > 128 || path[0] != '/' || path.EndsWith('/') || path.Contains("//", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in path)
        {
            var legal = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c is '/' or '-' or '_' or '.';
            if (!legal)
            {
                return false;
            }
        }

        // Every reserved prefix is refused case-insensitively, because routing matches paths that way.
        string[] reserved = ["/__admin", "/__sandbox", "/__mockifyr", "/.well-known"];
        return !reserved.Any(r => path.Equals(r, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a requested <c>scope</c> is legal: space-separated scope tokens of RFC 6749 §3.3's
    /// characters, at most 1024 characters in all.
    /// </summary>
    public static bool IsWellFormedScope(string scope)
    {
        if (scope.Length is 0 or > 1024 || scope[0] == ' ' || scope[^1] == ' ' || scope.Contains("  ", StringComparison.Ordinal))
        {
            return false;
        }

        // %x21 / %x23-5B / %x5D-7E, plus the single space that separates tokens.
        foreach (var c in scope)
        {
            var legal = c == ' ' || c == '\x21' || (c >= '\x23' && c <= '\x5B') || (c >= '\x5D' && c <= '\x7E');
            if (!legal)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>What a presented access token turned out to be.</summary>
public enum SandboxTokenStatus
{
    /// <summary>Signed by this host, for a known key, and not expired on its tenant's clock.</summary>
    Valid = 0,

    /// <summary>Not a token this host issued: malformed, wrongly signed, another issuer or algorithm.</summary>
    Invalid = 1,

    /// <summary>Issued here, and past its <c>exp</c> on the tenant's clock.</summary>
    Expired = 2,
}

/// <summary>The verdict on a presented access token.</summary>
/// <param name="Status">Whether it may be used.</param>
/// <param name="KeyId">The sandbox key the token was issued to; set only when signed by this host.</param>
/// <param name="Tenant">The tenant the token names; set only when signed by this host.</param>
public sealed record SandboxTokenValidation(SandboxTokenStatus Status, string? KeyId = null, TenantId? Tenant = null)
{
    /// <summary>The verdict for anything this host did not sign.</summary>
    public static readonly SandboxTokenValidation Invalid = new(SandboxTokenStatus.Invalid);
}

/// <summary>
/// Signs and checks the sandbox's access tokens (#450): RS256 JWTs in the RFC 9068 shape, with the
/// key published as a JSON Web Key Set.
/// </summary>
/// <remarks>
/// <para>
/// Pure apart from the key: every instant is passed in, so a tenant whose clock is frozen (#290) is
/// issued, and later refused, a token on its own terms.
/// </para>
/// <para>
/// Validation is fail-closed and fixed rather than negotiated. The header must name exactly
/// <c>RS256</c> and this key's <c>kid</c>, and may carry nothing else that changes its meaning — no
/// <c>crit</c>, no embedded key, no URL to fetch one from. That rules out the known JWT confusions
/// (<c>alg: none</c>, an HMAC signed with the public key, a key the token supplies itself) by
/// construction rather than by a list of attacks.
/// </para>
/// </remarks>
public sealed class SandboxTokenSigner
{
    /// <summary>The longest token this host will look at. Issued tokens are well under a kilobyte.</summary>
    public const int MaxTokenLength = 4096;

    /// <summary>The smallest RSA key accepted for signing.</summary>
    public const int MinKeySizeBits = 2048;

    private static readonly HashSet<string> AllowedHeaderFields = new(StringComparer.Ordinal) { "alg", "typ", "kid" };

    private readonly RSA _key;

    /// <summary>Signs with <paramref name="key"/>, which must hold its private part.</summary>
    /// <exception cref="ArgumentException">The key is shorter than <see cref="MinKeySizeBits"/>.</exception>
    public SandboxTokenSigner(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.KeySize < MinKeySizeBits)
        {
            throw new ArgumentException($"The signing key has {key.KeySize} bits; at least {MinKeySizeBits} are required.", nameof(key));
        }

        _key = key;
        var parameters = key.ExportParameters(includePrivateParameters: false);
        Modulus = Base64Url(parameters.Modulus!);
        Exponent = Base64Url(parameters.Exponent!);

        // RFC 7638's thumbprint: the same key file gives the same kid on every replica.
        Kid = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(
            $$"""{"e":"{{Exponent}}","kty":"RSA","n":"{{Modulus}}"}""")));
    }

    /// <summary>The key's id: its RFC 7638 thumbprint.</summary>
    public string Kid { get; }

    private string Modulus { get; }

    private string Exponent { get; }

    /// <summary>A signer with a fresh 2048-bit key, for a host that was given none.</summary>
    public static SandboxTokenSigner Generate() => new(RSA.Create(MinKeySizeBits));

    /// <summary>The public key set, as <see cref="SandboxOAuthOptions.JwksPath"/> serves it.</summary>
    public string Jwks() => new JsonObject
    {
        ["keys"] = new JsonArray(new JsonObject
        {
            ["kty"] = "RSA",
            ["kid"] = Kid,
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["n"] = Modulus,
            ["e"] = Exponent,
        }),
    }.ToJsonString();

    /// <summary>Issues a token to <paramref name="key"/> at <paramref name="now"/>, its tenant's instant.</summary>
    public string Mint(ApiKey key, string? scope, DateTimeOffset now, SandboxOAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);

        var header = new JsonObject { ["alg"] = "RS256", ["typ"] = "at+jwt", ["kid"] = Kid };
        var payload = new JsonObject
        {
            ["iss"] = options.Issuer,
            ["sub"] = key.Id,
            ["aud"] = options.Issuer,
            ["client_id"] = key.Id,
            ["tenant"] = key.Tenant.Value,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(options.Lifetime).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (scope is not null)
        {
            payload["scope"] = scope;
        }

        var signingInput = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()))
            + "." + Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>
    /// Whether <paramref name="token"/> is one this host issued and may still be used.
    /// </summary>
    /// <param name="token">The presented bearer token.</param>
    /// <param name="tenantNow">The instant a tenant's clock reads; consulted only once the signature holds.</param>
    /// <param name="options">The issuer the token must name.</param>
    public SandboxTokenValidation Validate(string token, Func<TenantId, DateTimeOffset> tenantNow, SandboxOAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(tenantNow);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return SandboxTokenValidation.Invalid;
        }

        var parts = token.Split('.');
        if (parts.Length != 3
            || FromBase64Url(parts[0]) is not { } headerBytes
            || FromBase64Url(parts[1]) is not { } payloadBytes
            || FromBase64Url(parts[2]) is not { } signature)
        {
            return SandboxTokenValidation.Invalid;
        }

        // The header first, and before any cryptography: a token that asks for another algorithm or
        // another key is refused for what it says, not for what verifying it would cost.
        if (ParseObject(headerBytes) is not { } header
            || header.Any(field => !AllowedHeaderFields.Contains(field.Key))
            || !IsString(header, "alg", "RS256")
            || !IsString(header, "kid", Kid))
        {
            return SandboxTokenValidation.Invalid;
        }

        if (!_key.VerifyData(
                Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return SandboxTokenValidation.Invalid;
        }

        if (ParseObject(payloadBytes) is not { } payload
            || !IsString(payload, "iss", options.Issuer)
            || Text(payload, "client_id") is not { Length: > 0 } keyId
            || Text(payload, "tenant") is not { Length: > 0 } tenantName
            || Seconds(payload, "exp") is not { } exp)
        {
            return SandboxTokenValidation.Invalid;
        }

        var tenant = new TenantId(tenantName);

        // exp is the first instant the token is no longer valid (RFC 7519 §4.1.4).
        return tenantNow(tenant).ToUnixTimeSeconds() >= exp
            ? new SandboxTokenValidation(SandboxTokenStatus.Expired, keyId, tenant)
            : new SandboxTokenValidation(SandboxTokenStatus.Valid, keyId, tenant);
    }

    /// <summary>Whether a presented bearer credential has the shape of a JWT rather than a sandbox key.</summary>
    /// <remarks>A key is base64url with no dot in it, so a dot is enough to tell them apart.</remarks>
    public static bool LooksLikeToken(string presented) => presented.Contains('.', StringComparison.Ordinal);

    private static JsonObject? ParseObject(byte[] utf8)
    {
        try
        {
            // Duplicate property names are refused: two "tenant" claims must not mean whichever one a
            // reader happens to keep.
            return JsonNode.Parse(utf8, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsString(JsonObject node, string name, string expected) =>
        Text(node, name) is { } value && string.Equals(value, expected, StringComparison.Ordinal);

    private static string? Text(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Seconds(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        foreach (var c in text)
        {
            var legal = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c is '-' or '_';
            if (!legal)
            {
                return null;
            }
        }

        var padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => "!" };
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
