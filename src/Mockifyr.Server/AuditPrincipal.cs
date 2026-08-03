using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Mockifyr.Server;

/// <summary>
/// Turns the credential on an admin request into an audit principal label (#247).
/// </summary>
/// <remarks>
/// Never returns any part of a secret. The system credential becomes <c>system</c>, a per-tenant
/// credential (#224) becomes <c>tenant:&lt;name&gt;</c>, and anything else — including a wrong password
/// and any credential on an open host — is <c>anonymous</c>. The label is an attribution claim in a
/// record someone will rely on, so a near miss must never be attributed to the real principal.
/// </remarks>
public sealed class AuditPrincipalResolver(
    string? systemAuthorization,
    TenantCredentials tenantCredentials,
    OidcTokenValidator? oidc = null)
{
    /// <summary>The label for the principal behind <paramref name="authorization"/>.</summary>
    public string Resolve(string authorization)
    {
        // An OIDC identity is recorded by the name a human recognises — `oidc:jane@example.com` — so a
        // reviewer reading the trail can act on it. The token itself never reaches an entry, for the
        // same reason a password never did.
        if (oidc is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            // Validated synchronously here: the middleware has already accepted this request, so the
            // token is known good and this is a re-read of claims rather than a second trust decision.
            var principal = oidc.ValidateAsync(authorization, CancellationToken.None).GetAwaiter().GetResult();
            return principal is null ? "anonymous" : $"oidc:{principal.Subject}";
        }

        if (tenantCredentials.TenantFor(authorization) is { } tenant)
        {
            return $"tenant:{tenant}";
        }

        // Constant-time, and only when a system credential exists at all: comparing against an empty
        // expected value would label every anonymous caller on an open host as "system".
        if (!string.IsNullOrEmpty(systemAuthorization) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes(systemAuthorization)))
        {
            return "system";
        }

        return "anonymous";
    }
}

/// <summary>
/// Pure classification of an admin request into the fields an audit entry carries (#247). Separated
/// from the middleware so the rules are unit-testable and mutation-tested on their own.
/// </summary>
public static class AuditAction
{
    /// <summary>
    /// Whether this request is an administrative change worth a permanent record.
    /// </summary>
    /// <remarks>
    /// Reads are excluded: <c>GET</c> traffic is already in the request journal, and mixing it in would
    /// evict the changes an operator came looking for. The trail's own route is excluded too — reading
    /// history is not making it.
    /// </remarks>
    public static bool IsAuditable(string method, PathString path) =>
        path.StartsWithSegments("/__admin")
        && !path.StartsWithSegments("/__admin/audit")
        && !HttpMethods.IsGet(method)
        && !HttpMethods.IsHead(method)
        && !HttpMethods.IsOptions(method);

    /// <summary>
    /// The addressed resource id, when the route carried one: the last segment of a path deeper than
    /// the collection itself (<c>/__admin/mappings/{id}</c> → the id, <c>/__admin/mappings</c> → null).
    /// A collection route addresses no id, and inventing one would be a lie in the record.
    /// </summary>
    public static string? TargetOf(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return segments.Length > 2 ? segments[^1] : null;
    }
}
