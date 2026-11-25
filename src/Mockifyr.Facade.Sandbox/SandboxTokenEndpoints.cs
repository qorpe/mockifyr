using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Mockifyr.Core;

namespace Mockifyr.Facade.Sandbox;

/// <summary>
/// The simulated client-credentials grant (#450, ADR 0016): a token endpoint and the key set that
/// verifies what it issues.
/// </summary>
/// <remarks>
/// <para>
/// The client is a sandbox key that already exists: its id is the <c>client_id</c> and the token shown
/// once at issue is the <c>client_secret</c>. No new credential, store or lifecycle — revoking or
/// expiring the key ends its tokens too, because the serve path checks the key on every call.
/// </para>
/// <para>
/// Errors are RFC 6749 §5.2's, word for word, because the point of the endpoint is that a partner's
/// token client meets here exactly what it will meet in production. A refusal never says whether a
/// <c>client_id</c> exists until the secret has proved possession of it.
/// </para>
/// </remarks>
public static class SandboxTokenEndpoints
{
    /// <summary>The largest token request read. A real one is a few hundred bytes.</summary>
    public const int MaxRequestBytes = 8 * 1024;

    private static readonly string[] SingleValued = ["grant_type", "client_id", "client_secret", "scope"];

    /// <summary>Maps the token endpoint and the key set. A no-op unless the grant is configured.</summary>
    public static IEndpointRouteBuilder MapSandboxTokenEndpoints(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints.ServiceProvider.GetService<SandboxTokenSigner>() is not { } signer)
        {
            return endpoints;
        }

        var options = endpoints.ServiceProvider.GetRequiredService<SandboxOAuthOptions>();

        endpoints.MapGet(SandboxOAuthOptions.JwksPath, () => Results.Text(signer.Jwks(), "application/json"));

        endpoints.MapPost(options.TokenPath, (Func<HttpContext, Task<IResult>>)(context => IssueAsync(context, signer, options)));

        return endpoints;
    }

    private static async Task<IResult> IssueAsync(HttpContext context, SandboxTokenSigner signer, SandboxOAuthOptions options)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";

        // RFC 6749 §4.4.2: the request is form-encoded. Anything else — JSON, multipart, nothing, or a
        // type that merely starts with the right words — is refused before a byte of it is buffered.
        // The media type is parsed and compared whole, because the form reader parses it too and would
        // throw on any spelling a prefix test let through.
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType)
            || !string.Equals(mediaType.MediaType.Value, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "The request must be application/x-www-form-urlencoded.");
        }

        if (context.Request.ContentLength > MaxRequestBytes)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "The request is too large.");
        }

        IFormCollection form;
        try
        {
            // Bounded whether or not a Content-Length was sent: a chunked body is held to the same limit.
            context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
            {
                BufferBodyLengthLimit = MaxRequestBytes,
                ValueCountLimit = 16,
                KeyLengthLimit = 64,
                ValueLengthLimit = MaxRequestBytes,
            }));
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "The request is too large.");
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }

        // §3.2: a parameter sent twice is an error, not a choice of one of them.
        if (SingleValued.Any(name => form.TryGetValue(name, out var values) && values.Count > 1))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "A parameter was sent more than once.");
        }

        var grantType = form["grant_type"].ToString();
        if (grantType.Length == 0)
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_request", "grant_type is required.");
        }

        if (!string.Equals(grantType, "client_credentials", StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status400BadRequest, "unsupported_grant_type", "Only client_credentials is supported.");
        }

        string? scope = form.TryGetValue("scope", out var scopeValue) ? scopeValue.ToString() : null;
        if (scope is not null && !SandboxOAuthOptions.IsWellFormedScope(scope))
        {
            return Error(StatusCodes.Status400BadRequest, "invalid_scope", "The scope is malformed.");
        }

        var (clientId, clientSecret, outcome) = Credentials(context, form);
        if (outcome is not null)
        {
            return outcome;
        }

        var keys = context.RequestServices.GetRequiredService<IApiKeyStore>();
        var key = clientId is null ? null : keys.Get(clientId);

        // The secret is checked even when the id is unknown, so the time a refusal takes does not say
        // which half was wrong.
        var proved = ApiKeyMaterial.Verify(clientSecret ?? string.Empty, key?.Salt ?? DecoySalt, key?.Hash ?? DecoyHash);
        if (key is null || !proved)
        {
            return InvalidClient("Client authentication failed.");
        }

        // Possession is proved, so the reason may be told — the same rule the serve path follows (#355).
        var status = key.StatusAt(DateTimeOffset.UtcNow);
        if (status is not ApiKeyStatus.Active)
        {
            return InvalidClient(status == ApiKeyStatus.Expired
                ? "This sandbox key has expired."
                : "This sandbox key has been revoked.");
        }

        var now = context.RequestServices.GetRequiredService<IClockResolver>().UtcNow(key.Tenant);
        var token = signer.Mint(key, scope, now, options);
        var body = new Dictionary<string, object>
        {
            ["access_token"] = token,
            ["token_type"] = "Bearer",
            ["expires_in"] = (long)options.Lifetime.TotalSeconds,
        };
        if (scope is not null)
        {
            body["scope"] = scope;
        }

        return Results.Json(body);
    }

    /// <summary>
    /// The client's id and secret, from HTTP Basic (§2.3.1) or the body — exactly one of the two.
    /// </summary>
    private static (string? Id, string? Secret, IResult? Refusal) Credentials(HttpContext context, IFormCollection form)
    {
        var inBody = form.ContainsKey("client_id") || form.ContainsKey("client_secret");
        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count > 1)
        {
            return (null, null, Error(StatusCodes.Status400BadRequest, "invalid_request", "More than one Authorization header was sent."));
        }

        var header = authorization.ToString();
        if (header.Length == 0)
        {
            return inBody
                ? (form["client_id"].ToString(), form["client_secret"].ToString(), null)
                : (null, null, InvalidClient("Client authentication is required."));
        }

        // §2.3: a client uses one authentication method per request.
        if (inBody)
        {
            return (null, null, Error(StatusCodes.Status400BadRequest, "invalid_request", "Credentials were sent both in the header and in the body."));
        }

        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, InvalidClient("Only HTTP Basic client authentication is supported in the header."));
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return (null, null, InvalidClient("Client authentication failed."));
        }

        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return (null, null, InvalidClient("Client authentication failed."));
        }

        // §2.3.1: both halves are form-encoded before they are joined.
        return (WebUtility.UrlDecode(decoded[..colon]), WebUtility.UrlDecode(decoded[(colon + 1)..]), null);
    }

    private static IResult InvalidClient(string description) => new InvalidClientResult(description);

    private static IResult Error(int status, string error, string description) =>
        Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, statusCode: status);

    private static readonly string DecoySalt = Convert.ToBase64String(new byte[16]);

    private static readonly string DecoyHash = Convert.ToBase64String(new byte[32]);

    /// <summary>§5.2's <c>invalid_client</c>: a 401 that names the scheme the client should use.</summary>
    private sealed class InvalidClientResult(string description) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"mockifyr-sandbox\"";
            return Error(StatusCodes.Status401Unauthorized, "invalid_client", description).ExecuteAsync(httpContext);
        }
    }
}
