using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HandlebarsDotNet;

namespace Mockifyr.Templating;

/// <summary>
/// The <c>jwt</c> templating helper (G15): <c>{{jwt sub='u1' role='admin'}}</c> renders a signed JWT.
/// Claim defaults are <c>iss=wiremock</c>, <c>aud=wiremock.io</c>, <c>sub=user-123</c>, <c>iat=now</c>,
/// <c>exp=now+maxAge</c> (default <c>36500 days</c>) — and any non-reserved parameter becomes a private
/// claim. Signed with HS256.
///
/// <para>The default signing secret is random per instance and <c>iat</c> is the current time, so the
/// token can't be byte-diffed; it is <b>self-tested</b> by content parity — the decoded header and
/// claims are checked directly — plus a structural check on the racy <c>iat</c>/<c>exp</c> and the
/// signature. RS256, configurable secrets, <c>nbf</c>, and array claims are deferred.</para>
/// </summary>
internal static class JwtHelpers
{
    // Mockifyr uses a fixed default signing secret (a random per-start secret is a follow-up). Content
    // parity does not depend on the secret, only the claims.
    private const string DefaultSecret = "mockifyr-default-hs256-secret";

    // The RSA key + key id for RS256, generated once per instance (like the reference extension). Both
    // are effectively random, so — like the HS256 signature — they are excluded from content parity.
    private static readonly RSA RsaKey = RSA.Create(2048);
    private static readonly string Kid = Base64Url(RandomNumberGenerator.GetBytes(23))[..30];

    // Handled specially or consumed (not emitted as private claims). Mockifyr reserves iss/aud/sub/
    // exp/nbf and also consumes maxAge. `alg` selects the algorithm AND deliberately leaks
    // into the payload as a claim, so it is not reserved.
    private static readonly HashSet<string> Reserved =
        new(StringComparer.Ordinal) { "exp", "iss", "aud", "sub", "nbf", "maxAge" };

    public static void Register(IHandlebars handlebars)
    {
        handlebars.RegisterHelper("jwt", (_, arguments) => CreateToken(arguments.Hash));
        handlebars.RegisterHelper("jwks", (_, _) => RenderJwks());
    }

    // Renders the JSON Web Key Set for the RS256 public key — the same key the `jwt` helper signs RS256
    // tokens with (matching `kid`), so a token verifier can resolve it. Structure mirrors the reference
    // extension exactly: { "keys": [ { kty, kid, use, alg, n, e } ] }, n/e big-endian base64url. The key
    // is random per instance, so like the token it is validated structurally + by self-consistency.
    private static string RenderJwks()
    {
        var parameters = RsaKey.ExportParameters(includePrivateParameters: false);
        var jwk = new JsonObject
        {
            ["kty"] = "RSA",
            ["kid"] = Kid,
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["n"] = Base64Url(parameters.Modulus!),
            ["e"] = Base64Url(parameters.Exponent!),
        };

        return new JsonObject { ["keys"] = new JsonArray(jwk) }.ToJsonString();
    }

    // The hash's values are nullable as of Handlebars.Net 2.4.3, which annotated its API more
    // precisely rather than changing it: a helper written `{{jwt sub=undefinedThing}}` always could
    // hand us a null. The signature follows the truth instead of asserting the old one.
    private static object CreateToken(IReadOnlyDictionary<string, object?>? hash)
    {
        // The tenant's clock (#290), so a token minted for a frozen host expires on that host's terms.
        var now = RenderClock.UtcNow;
        var payload = new JsonObject
        {
            ["exp"] = now.Add(ParseMaxAge(Get(hash, "maxAge"))).ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["iss"] = Get(hash, "iss") ?? "wiremock",
            ["aud"] = Get(hash, "aud") ?? "wiremock.io",
            ["sub"] = Get(hash, "sub") ?? "user-123",
        };

        if (hash is not null)
        {
            foreach (var (key, value) in hash)
            {
                if (!Reserved.Contains(key))
                {
                    payload[key] = ToNode(value);
                }
            }
        }

        var alg = Get(hash, "alg") ?? "HS256";
        var header = new JsonObject { ["alg"] = alg, ["typ"] = "JWT" };
        if (alg == "RS256")
        {
            header["kid"] = Kid;
        }

        var signingInput = Base64Url(Bytes(header)) + "." + Base64Url(Bytes(payload));
        var signature = alg == "RS256"
            ? RsaKey.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            : Sign(signingInput);
        return signingInput + "." + Base64Url(signature);
    }

    // "amount unit" (e.g. "12 days"); default 36500 days, matching the reference.
    private static TimeSpan ParseMaxAge(string? maxAge)
    {
        var parts = maxAge?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 2 } || !long.TryParse(parts[0], out var amount))
        {
            return TimeSpan.FromDays(36500);
        }

        return parts[1].ToLowerInvariant() switch
        {
            "seconds" => TimeSpan.FromSeconds(amount),
            "minutes" => TimeSpan.FromMinutes(amount),
            "hours" => TimeSpan.FromHours(amount),
            "days" => TimeSpan.FromDays(amount),
            _ => TimeSpan.FromDays(36500),
        };
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        bool b => b,
        int i => i,
        long l => l,
        double d => d,
        _ => value.ToString(),
    };

    private static byte[] Sign(string signingInput)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(DefaultSecret));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
    }

    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Get(IReadOnlyDictionary<string, object?>? hash, string key) =>
        hash is not null && hash.TryGetValue(key, out var value) ? value?.ToString() : null;
}
