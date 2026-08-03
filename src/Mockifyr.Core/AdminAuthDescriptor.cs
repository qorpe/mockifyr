namespace Mockifyr.Core;

/// <summary>
/// How a client is expected to authenticate against the admin API (#251), as reported by
/// <c>/__admin/health</c>.
/// </summary>
/// <remarks>
/// Public parameters only — an authority URL and a public client id. Nothing here is a secret, which
/// is what lets an unauthenticated login screen read it: it has to know where to send the user before
/// there is any identity to check.
/// </remarks>
/// <param name="Mode"><c>none</c>, <c>basic</c> or <c>oidc</c>.</param>
/// <param name="Authority">The OIDC issuer, when the mode is <c>oidc</c>.</param>
/// <param name="ClientId">The public client id the dashboard signs in with, when configured.</param>
public sealed record AdminAuthDescriptor(string Mode, string? Authority = null, string? ClientId = null)
{
    /// <summary>An open host: no admin authentication configured.</summary>
    public static readonly AdminAuthDescriptor None = new("none");
}
