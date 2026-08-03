using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Mockifyr.Core;
using System.IdentityModel.Tokens.Jwt;

namespace Mockifyr.Server;

/// <summary>
/// How this host validates OIDC bearer tokens on the admin API (#251), parsed from the command line.
/// </summary>
/// <param name="Authority">
/// The issuer's base URL. Discovery (<c>/.well-known/openid-configuration</c>) and the JWKS are read
/// from it, so signing keys rotate with the provider and nothing is pinned in configuration.
/// </param>
/// <param name="Audience">The audience the token must carry, or null to accept any.</param>
/// <param name="TenantClaim">
/// The claim whose value names the tenant this principal may address. Absent, an authenticated
/// principal is a system-scope one — the OIDC equivalent of <c>--admin-user</c>.
/// </param>
/// <param name="RequiredRole">A role/claim value the token must carry at all, or null for none.</param>
/// <param name="RoleClaim">Which claim carries roles (default <c>roles</c>).</param>
public sealed record OidcOptions(
    string Authority,
    string? Audience,
    string? TenantClaim,
    string? RequiredRole,
    string RoleClaim,
    string? ClientId = null)
{
    /// <summary>Reads the options from configuration, or null when <c>--oidc-authority</c> is absent.</summary>
    public static OidcOptions? Parse(Func<string, string?> configuration)
    {
        var authority = configuration("oidc-authority");
        if (string.IsNullOrWhiteSpace(authority))
        {
            return null;
        }

        return new OidcOptions(
            authority.TrimEnd('/'),
            NullIfBlank(configuration("oidc-audience")),
            NullIfBlank(configuration("oidc-tenant-claim")),
            NullIfBlank(configuration("oidc-required-role")),
            NullIfBlank(configuration("oidc-role-claim")) ?? "roles",
            NullIfBlank(configuration("oidc-client-id")));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>What a validated token said about the caller.</summary>
/// <param name="Subject">A human-recognisable name for the audit trail — never the token.</param>
/// <param name="Tenant">The tenant this principal may address, or null for system scope.</param>
public sealed record OidcPrincipal(string Subject, string? Tenant);

/// <summary>
/// Validates OIDC bearer tokens against the issuer's published keys (#251).
/// </summary>
/// <remarks>
/// <para>
/// A third principal source alongside the system credential and per-tenant credentials, sitting in the
/// same middleware chain rather than replacing it: a host may run with OIDC for people and
/// <c>--admin-user</c> for machines, which is what makes adopting it incremental instead of a flag day.
/// </para>
/// <para>
/// Keys come from discovery and are refreshed by the configuration manager, so a provider rotating its
/// signing key needs nothing here — the same reasoning as the key ring in #250, applied to somebody
/// else's keys.
/// </para>
/// </remarks>
public sealed class OidcTokenValidator
{
    private readonly OidcOptions _options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configuration;
    private readonly JwtSecurityTokenHandler _handler = new();

    public OidcTokenValidator(OidcOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{options.Authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(httpClient ?? new HttpClient()) { RequireHttps = false });

        // RequireHttps is off because a test issuer and an in-cluster one both speak plain HTTP. The
        // control that matters is the signature, which is checked against the issuer's own keys; an
        // attacker who can rewrite the discovery document can already rewrite the token.
    }

    /// <summary>
    /// Validates the <c>Authorization: Bearer …</c> header, or returns null when it is absent, not a
    /// bearer token, or does not validate. Never throws: the header is attacker-controlled input on
    /// the request path.
    /// </summary>
    public async Task<OidcPrincipal?> ValidateAsync(string authorizationHeader, CancellationToken cancellationToken)
    {
        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            return null;
        }

        try
        {
            var configuration = await _configuration.GetConfigurationAsync(cancellationToken);
            var parameters = new TokenValidationParameters
            {
                ValidIssuer = configuration.Issuer,
                ValidateIssuer = true,
                IssuerSigningKeys = configuration.SigningKeys,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ValidAudience = _options.Audience,
                ValidateAudience = _options.Audience is not null,
                ClockSkew = TimeSpan.FromSeconds(30),
            };

            var claims = _handler.ValidateToken(token, parameters, out _);

            if (_options.RequiredRole is { } required && !HasRole(claims, required))
            {
                return null;
            }

            return new OidcPrincipal(SubjectOf(claims), TenantOf(claims));
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or InvalidOperationException
            or HttpRequestException or TaskCanceledException)
        {
            // An invalid, expired or unverifiable token — and an unreachable issuer — all mean the same
            // thing here: this caller is not authenticated. An unreachable provider must not become a
            // 500 on every admin request.
            return null;
        }
    }

    private bool HasRole(ClaimsPrincipal claims, string required) =>
        claims.FindAll(_options.RoleClaim).Any(claim =>
            string.Equals(claim.Value, required, StringComparison.Ordinal))
        || claims.FindAll(ClaimTypes.Role).Any(claim =>
            string.Equals(claim.Value, required, StringComparison.Ordinal));

    private string? TenantOf(ClaimsPrincipal claims) =>
        _options.TenantClaim is { } claimType
            ? claims.FindFirst(claimType)?.Value is { Length: > 0 } tenant ? tenant : null
            : null;

    /// <summary>
    /// The label the audit trail records. A name a human recognises, preferred over an opaque subject
    /// id — an audit entry naming <c>a3f9…</c> tells a reviewer nothing they can act on.
    /// </summary>
    private static string SubjectOf(ClaimsPrincipal claims) =>
        claims.FindFirst("preferred_username")?.Value
        ?? claims.FindFirst("email")?.Value
        ?? claims.FindFirst("sub")?.Value
        ?? claims.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "unknown";
}
